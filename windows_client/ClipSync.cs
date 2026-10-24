using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace ClipSync
{
    // =========================================================================
    // Discovered Device Model
    // =========================================================================
    public class DiscoveredDevice
    {
        public string Name { get; set; }
        public string DeviceType { get; set; } // "windows" or "android"
        public string IP { get; set; }
        public int Port { get; set; }
        public DateTime LastSeen { get; set; }

        public string DisplayText
        {
            get
            {
                string icon = DeviceType == "android" ? "📱 " : "💻 ";
                return icon + Name + " (" + IP + ":" + Port + ")";
            }
        }
    }

    // =========================================================================
    // Native Win32 Clipboard Listener (0.0% CPU usage)
    // =========================================================================
    public class ClipboardListenerWindow : NativeWindow
    {
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private readonly Action onUpdateCallback;

        public ClipboardListenerWindow(Action callback)
        {
            this.onUpdateCallback = callback;
            CreateParams cp = new CreateParams { Caption = "ClipSyncListenerWindow", Style = 0 };
            CreateHandle(cp);
            AddClipboardFormatListener(this.Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                if (onUpdateCallback != null) onUpdateCallback();
            }
            base.WndProc(ref m);
        }

        public void Cleanup()
        {
            RemoveClipboardFormatListener(this.Handle);
            DestroyHandle();
        }
    }

    // =========================================================================
    // Standalone Embedded WebSocket Relay Server (Zero Dependencies)
    // =========================================================================
    public class EmbeddedRelayServer
    {
        private TcpListener listener;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private ConcurrentDictionary<string, TcpClient> clients = new ConcurrentDictionary<string, TcpClient>();
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }
        public Func<string, string, bool> OnPairRequestReceived;
        public Action<string> OnClipboardMessageReceived;

        public EmbeddedRelayServer(int port = 8765)
        {
            this.Port = port;
        }

        public bool TryStart()
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                IsRunning = true;
                Task.Run(() => ListenLoop(cts.Token));
                return true;
            }
            catch
            {
                IsRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            cts.Cancel();
            try { listener.Stop(); } catch { }
            foreach (var c in clients.Values)
            {
                try { c.Close(); } catch { }
            }
            clients.Clear();
            IsRunning = false;
        }

        private async Task ListenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    TcpClient client = await listener.AcceptTcpClientAsync();
                    string id = Guid.NewGuid().ToString();
                    clients[id] = client;
                    Task.Run(() => HandleClient(id, client, token));
                }
                catch
                {
                    if (token.IsCancellationRequested) break;
                }
            }
        }

        private async Task HandleClient(string id, TcpClient client, CancellationToken token)
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[8192];
            int read = await stream.ReadAsync(buffer, 0, buffer.Length, token);
            if (read <= 0) return;
            string header = Encoding.UTF8.GetString(buffer, 0, read);

            if (header.StartsWith("GET /health"))
            {
                byte[] health = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{\"status\":\"ok\",\"clients\":" + clients.Count + "}");
                await stream.WriteAsync(health, 0, health.Length, token);
                client.Close();
                TcpClient dummy;
                clients.TryRemove(id, out dummy);
                return;
            }

            // WebSocket Handshake
            if (header.Contains("Sec-WebSocket-Key"))
            {
                Match match = Regex.Match(header, "Sec-WebSocket-Key:\\s*(.+)");
                if (match.Success)
                {
                    string key = match.Groups[1].Value.Trim();
                    string acceptKey = ComputeWebSocketAccept(key);
                    string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                      "Upgrade: websocket\r\n" +
                                      "Connection: Upgrade\r\n" +
                                      "Sec-WebSocket-Accept: " + acceptKey + "\r\n\r\n";
                    byte[] responseBytes = Encoding.UTF8.GetBytes(response);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length, token);

                    await ProcessWebSocketFrames(id, client, stream, token);
                }
            }
            client.Close();
            TcpClient removed;
            clients.TryRemove(id, out removed);
        }

        private async Task ProcessWebSocketFrames(string id, TcpClient client, NetworkStream stream, CancellationToken token)
        {
            byte[] headerBuffer = new byte[2];
            while (!token.IsCancellationRequested && client.Connected)
            {
                int read = await ReadExactAsync(stream, headerBuffer, 2, token);
                if (read < 2) break;

                bool fin = (headerBuffer[0] & 0x80) != 0;
                int opcode = headerBuffer[0] & 0x0F;
                bool masked = (headerBuffer[1] & 0x80) != 0;
                ulong payloadLen = (ulong)(headerBuffer[1] & 0x7F);

                if (opcode == 0x8) break; // Close
                if (opcode == 0x9) // Ping
                {
                    byte[] pong = new byte[] { 0x8A, 0x00 };
                    await stream.WriteAsync(pong, 0, pong.Length, token);
                    continue;
                }

                if (payloadLen == 126)
                {
                    byte[] ext = new byte[2];
                    await ReadExactAsync(stream, ext, 2, token);
                    payloadLen = (ulong)((ext[0] << 8) | ext[1]);
                }
                else if (payloadLen == 127)
                {
                    byte[] ext = new byte[8];
                    await ReadExactAsync(stream, ext, 8, token);
                    payloadLen = (ulong)((ext[0] << 56) | (ext[1] << 48) | (ext[2] << 40) | (ext[3] << 32) |
                                         (ext[4] << 24) | (ext[5] << 16) | (ext[6] << 8) | ext[7]);
                }

                byte[] mask = new byte[4];
                if (masked) await ReadExactAsync(stream, mask, 4, token);

                byte[] payload = new byte[payloadLen];
                await ReadExactAsync(stream, payload, (int)payloadLen, token);

                if (masked)
                {
                    for (int i = 0; i < payload.Length; i++)
                    {
                        payload[i] ^= mask[i % 4];
                    }
                }

                if (opcode == 0x1) // Text frame
                {
                    string message = Encoding.UTF8.GetString(payload);

                    // Check for Pair Request
                    if (message.Contains("\"type\":\"pair_req\""))
                    {
                        string fromName = ExtractJson(message, "from_name") ?? "Unknown Device";
                        string fromIp = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                        bool allow = true;
                        if (OnPairRequestReceived != null)
                        {
                            allow = OnPairRequestReceived(fromName, fromIp);
                        }
                        string respJson = "{\"type\":\"pair_resp\",\"status\":\"" + (allow ? "accepted" : "rejected") + "\"}";
                        byte[] frame = CreateWebSocketTextFrame(respJson);
                        await stream.WriteAsync(frame, 0, frame.Length, token);
                        if (!allow) break;
                    }
                    else
                    {
                        if (OnClipboardMessageReceived != null && (message.Contains("\"type\":\"clipboard\"") || message.Contains("\"type\": \"clipboard\"")))
                        {
                            OnClipboardMessageReceived(message);
                        }
                        BroadcastToOthers(id, message);
                    }
                }
            }
        }

        private void BroadcastToOthers(string senderId, string message)
        {
            byte[] frame = CreateWebSocketTextFrame(message);
            foreach (var kv in clients)
            {
                if (kv.Key != senderId && kv.Value.Connected)
                {
                    try { kv.Value.GetStream().Write(frame, 0, frame.Length); } catch { }
                }
            }
        }

        public void BroadcastRaw(string message)
        {
            byte[] frame = CreateWebSocketTextFrame(message);
            foreach (var c in clients.Values)
            {
                if (c.Connected)
                {
                    try { c.GetStream().Write(frame, 0, frame.Length); } catch { }
                }
            }
        }

        private byte[] CreateWebSocketTextFrame(string message)
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            byte[] frameHeader;
            if (payload.Length <= 125)
            {
                frameHeader = new byte[] { 0x81, (byte)payload.Length };
            }
            else if (payload.Length <= 65535)
            {
                frameHeader = new byte[] { 0x81, 126, (byte)(payload.Length >> 8), (byte)(payload.Length & 0xFF) };
            }
            else
            {
                frameHeader = new byte[10];
                frameHeader[0] = 0x81;
                frameHeader[1] = 127;
                ulong len = (ulong)payload.Length;
                for (int i = 0; i < 8; i++)
                {
                    frameHeader[9 - i] = (byte)(len & 0xFF);
                    len >>= 8;
                }
            }
            byte[] result = new byte[frameHeader.Length + payload.Length];
            Buffer.BlockCopy(frameHeader, 0, result, 0, frameHeader.Length);
            Buffer.BlockCopy(payload, 0, result, frameHeader.Length, payload.Length);
            return result;
        }

        private async Task<int> ReadExactAsync(Stream s, byte[] b, int count, CancellationToken t)
        {
            int total = 0;
            while (total < count)
            {
                int r = await s.ReadAsync(b, total, count - total, t);
                if (r <= 0) return total;
                total += r;
            }
            return total;
        }

        private string ComputeWebSocketAccept(string key)
        {
            string magic = key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            using (SHA1 sha1 = SHA1.Create())
            {
                return Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(magic)));
            }
        }

        private static string ExtractJson(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\":\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
    }

    // =========================================================================
    // LAN Device Discovery Service (UDP Broadcast & Listener)
    // =========================================================================
    public class DiscoveryService
    {
        public const int DISCOVERY_PORT = 8766;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private ConcurrentDictionary<string, DiscoveredDevice> devices = new ConcurrentDictionary<string, DiscoveredDevice>();
        public event Action OnDevicesChanged;
        public Func<string, string, bool> OnUdpPairRequestReceived;
        public Action<string, string> OnUdpClipboardReceived;
        private ConcurrentDictionary<string, TaskCompletionSource<bool>> pendingPairRequests = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();

        public void SendUdpClipboard(string text, string hash, string myName)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 2500) return;
            Task.Run(async () => {
                try
                {
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                    string msg = "CLIPSYNC_CLIP:" + b64 + ":" + hash + ":" + myName;
                    byte[] data = Encoding.UTF8.GetBytes(msg);

                    foreach (var dev in GetDiscoveredDevices())
                    {
                        try
                        {
                            using (UdpClient udp = new UdpClient())
                            {
                                await udp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(dev.IP), DISCOVERY_PORT));
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }

        public IEnumerable<DiscoveredDevice> GetDiscoveredDevices()
        {
            // Filter devices seen in the last 15 seconds
            List<DiscoveredDevice> active = new List<DiscoveredDevice>();
            DateTime now = DateTime.Now;
            foreach (var d in devices.Values)
            {
                if ((now - d.LastSeen).TotalSeconds < 15)
                {
                    active.Add(d);
                }
            }
            return active;
        }

        public async Task<bool> SendUdpPairRequestAsync(string targetIp, string myName, string myIp, int myPort)
        {
            var tcs = new TaskCompletionSource<bool>();
            pendingPairRequests[targetIp] = tcs;

            try
            {
                IPEndPoint endpoint = new IPEndPoint(IPAddress.Parse(targetIp), DISCOVERY_PORT);
                string msg = "CLIPSYNC_PAIR_REQ:" + myName + ":" + myIp + ":" + myPort;
                byte[] data = Encoding.UTF8.GetBytes(msg);

                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (UnicastIPAddressInformation uip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (uip.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            try
                            {
                                using (UdpClient localUdp = new UdpClient(new IPEndPoint(uip.Address, 0)))
                                {
                                    await localUdp.SendAsync(data, data.Length, endpoint);
                                }
                            }
                            catch { }
                        }
                    }
                }

                Task timeoutTask = Task.Delay(25000);
                Task completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                TaskCompletionSource<bool> dummy;
                pendingPairRequests.TryRemove(targetIp, out dummy);
            }
        }

        private static List<IPAddress> GetBroadcastAddresses()
        {
            var list = new List<IPAddress>();
            list.Add(IPAddress.Broadcast); // 255.255.255.255
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (UnicastIPAddressInformation uip in ipProps.UnicastAddresses)
                    {
                        if (uip.Address.AddressFamily == AddressFamily.InterNetwork && uip.IPv4Mask != null)
                        {
                            byte[] ipBytes = uip.Address.GetAddressBytes();
                            byte[] maskBytes = uip.IPv4Mask.GetAddressBytes();
                            byte[] broadcastBytes = new byte[4];
                            for (int i = 0; i < 4; i++)
                            {
                                broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                            }
                            IPAddress bcast = new IPAddress(broadcastBytes);
                            if (!list.Contains(bcast)) list.Add(bcast);
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public void StartBroadcasting(string myName, int myPort)
        {
            Task.Run(async () => {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        string msg = "CLIPSYNC_DEV:" + myName + ":windows:" + myPort;
                        byte[] data = Encoding.UTF8.GetBytes(msg);

                        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                                continue;

                            var ipProps = ni.GetIPProperties();
                            foreach (UnicastIPAddressInformation uip in ipProps.UnicastAddresses)
                            {
                                if (uip.Address.AddressFamily == AddressFamily.InterNetwork && uip.IPv4Mask != null)
                                {
                                    IPAddress localIp = uip.Address;
                                    byte[] ipBytes = localIp.GetAddressBytes();
                                    byte[] maskBytes = uip.IPv4Mask.GetAddressBytes();
                                    byte[] broadcastBytes = new byte[4];
                                    for (int i = 0; i < 4; i++)
                                    {
                                        broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                                    }
                                    IPAddress subnetBcast = new IPAddress(broadcastBytes);

                                    try
                                    {
                                        using (UdpClient localUdp = new UdpClient(new IPEndPoint(localIp, 0)))
                                        {
                                            localUdp.EnableBroadcast = true;
                                            await localUdp.SendAsync(data, data.Length, new IPEndPoint(subnetBcast, DISCOVERY_PORT));
                                            await localUdp.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT));
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(2500);
                }
            });
        }

        public void StartListening(string myName)
        {
            Task.Run(async () => {
                try
                {
                    using (UdpClient udp = new UdpClient())
                    {
                        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DISCOVERY_PORT));

                        while (!cts.IsCancellationRequested)
                        {
                            UdpReceiveResult res = await udp.ReceiveAsync();
                            string msg = Encoding.UTF8.GetString(res.Buffer);
                            string remoteIp = res.RemoteEndPoint.Address.ToString();

                            if (msg.StartsWith("CLIPSYNC_DEV:"))
                            {
                                string[] parts = msg.Split(':');
                                if (parts.Length >= 4)
                                {
                                    string devName = parts[1];
                                    string devType = parts[2];
                                    int port = int.Parse(parts[3]);

                                    // Don't list ourselves
                                    if (devName == myName) continue;

                                    string key = remoteIp + ":" + port;
                                    DiscoveredDevice dev;
                                    if (!devices.TryGetValue(key, out dev))
                                    {
                                        dev = new DiscoveredDevice { Name = devName, DeviceType = devType, IP = remoteIp, Port = port };
                                        devices[key] = dev;
                                    }
                                    dev.LastSeen = DateTime.Now;
                                    if (OnDevicesChanged != null) OnDevicesChanged();
                                }
                            }
                            else if (msg.StartsWith("CLIPSYNC_PAIR_REQ:"))
                            {
                                string[] parts = msg.Split(':');
                                if (parts.Length >= 4)
                                {
                                    string fromName = parts[1];
                                    string fromIp = parts[2];
                                    int fromPort = int.Parse(parts[3]);

                                    Task.Run(async () => {
                                        bool allow = true;
                                        if (OnUdpPairRequestReceived != null)
                                        {
                                            allow = OnUdpPairRequestReceived(fromName, fromIp);
                                        }
                                        string respMsg = "CLIPSYNC_PAIR_RESP:" + (allow ? "allow" : "deny") + ":" + myName;
                                        byte[] respBytes = Encoding.UTF8.GetBytes(respMsg);
                                        using (UdpClient respUdp = new UdpClient())
                                        {
                                            await respUdp.SendAsync(respBytes, respBytes.Length, new IPEndPoint(IPAddress.Parse(remoteIp), DISCOVERY_PORT));
                                        }
                                    });
                                }
                            }
                            else if (msg.StartsWith("CLIPSYNC_PAIR_RESP:"))
                            {
                                string[] parts = msg.Split(':');
                                if (parts.Length >= 3)
                                {
                                    string status = parts[1];
                                    string fromName = parts[2];
                                    bool isAllowed = status.Equals("allow", StringComparison.OrdinalIgnoreCase) || status.Equals("accepted", StringComparison.OrdinalIgnoreCase);

                                    TaskCompletionSource<bool> tcs;
                                    if (pendingPairRequests.TryGetValue(remoteIp, out tcs))
                                    {
                                        tcs.TrySetResult(isAllowed);
                                    }
                                }
                            }
                            else if (msg.StartsWith("CLIPSYNC_CLIP:"))
                            {
                                string[] parts = msg.Split(':');
                                if (parts.Length >= 4)
                                {
                                    string b64 = parts[1];
                                    string hash = parts[2];
                                    string senderName = parts[3];
                                    if (senderName != myName && OnUdpClipboardReceived != null)
                                    {
                                        try
                                        {
                                            string text = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                                            OnUdpClipboardReceived(text, hash);
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            });
        }

        public void Stop()
        {
            cts.Cancel();
        }
    }

    // =========================================================================
    // Modern Network Devices & Pairing Manager Form (UI)
    // =========================================================================
    public class DevicesForm : Form
    {
        private ListBox listDevices;
        private Label lblMyIp;
        private Label lblStatus;
        private Button btnConnect;
        private Button btnRefresh;
        private DiscoveryService discovery;
        private ClipSyncContext context;

        public DevicesForm(DiscoveryService discoveryService, ClipSyncContext syncContext)
        {
            this.discovery = discoveryService;
            this.context = syncContext;
            InitializeComponent();

            discovery.OnDevicesChanged += () => {
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(RefreshDeviceList));
                }
            };
            RefreshDeviceList();
        }

        private void InitializeComponent()
        {
            this.Text = "ClipSync — Network Devices & Pairing";
            this.Size = new Size(500, 480);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.BackColor = Color.FromArgb(15, 23, 42);
            this.ForeColor = Color.White;
            this.Font = new Font("Segoe UI", 9.5f);

            Label lblHeader = new Label
            {
                Text = "ClipSync Network Discovery",
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(20, 16),
                AutoSize = true
            };
            this.Controls.Add(lblHeader);

            lblMyIp = new Label
            {
                Text = "My Device: " + Environment.MachineName + " | IP: " + context.GetLocalIpAddress() + " (Port 8765)",
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(20, 48),
                AutoSize = true
            };
            this.Controls.Add(lblMyIp);

            Label lblListTitle = new Label
            {
                Text = "DISCOVERED DEVICES ON LOCAL NETWORK (WIFI / LAN)",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(96, 165, 250),
                Location = new Point(20, 85),
                AutoSize = true
            };
            this.Controls.Add(lblListTitle);

            listDevices = new ListBox
            {
                Location = new Point(20, 110),
                Size = new Size(440, 220),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 10.5f),
                ItemHeight = 28
            };
            listDevices.SelectedIndexChanged += (s, e) => {
                btnConnect.Enabled = listDevices.SelectedItem != null;
            };
            this.Controls.Add(listDevices);

            lblStatus = new Label
            {
                Text = "Scanning for other laptops and phones on network...",
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(20, 340),
                Size = new Size(440, 30)
            };
            this.Controls.Add(lblStatus);

            btnConnect = new Button
            {
                Text = "🔗 Send Connection Request",
                Location = new Point(20, 380),
                Size = new Size(240, 42),
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Enabled = false
            };
            btnConnect.Click += (s, e) => ConnectToSelected();
            this.Controls.Add(btnConnect);

            btnRefresh = new Button
            {
                Text = "🔄 Refresh",
                Location = new Point(280, 380),
                Size = new Size(180, 42),
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnRefresh.Click += (s, e) => RefreshDeviceList();
            this.Controls.Add(btnRefresh);

            Label lblManual = new Label
            {
                Text = "Or Enter IP Directly (Wi-Fi / Tailscale):",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(96, 165, 250),
                Location = new Point(20, 435),
                AutoSize = true
            };
            this.Controls.Add(lblManual);

            TextBox txtManualIp = new TextBox
            {
                Location = new Point(20, 460),
                Size = new Size(240, 32),
                Font = new Font("Segoe UI", 10.5f),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(30, 41, 59),
                BorderStyle = BorderStyle.FixedSingle
            };
            this.Controls.Add(txtManualIp);

            Button btnManualConnect = new Button
            {
                Text = "Direct Connect",
                Location = new Point(280, 458),
                Size = new Size(180, 32),
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };
            btnManualConnect.Click += (s, e) => {
                string ip = txtManualIp.Text.Trim();
                if (string.IsNullOrEmpty(ip))
                {
                    MessageBox.Show("Please enter an IP address (e.g. 192.168.0.108)", "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DiscoveredDevice dev = new DiscoveredDevice { Name = "Device (" + ip + ")", DeviceType = "windows", IP = ip, Port = 8765 };
                lblStatus.Text = "Connecting directly to " + ip + "...";
                btnManualConnect.Enabled = false;
                Task.Run(async () => {
                    bool ok = await context.RequestPairingAndConnect(dev);
                    if (this.IsHandleCreated)
                    {
                        this.BeginInvoke(new Action(() => {
                            btnManualConnect.Enabled = true;
                            if (ok)
                            {
                                MessageBox.Show("✅ Connected to " + ip + "!\nClipboard sync active.", "Connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                this.Close();
                            }
                            else
                            {
                                MessageBox.Show("Connection to " + ip + " failed or timed out.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                lblStatus.Text = "Connection to " + ip + " failed.";
                            }
                        }));
                    }
                });
            };
            this.Controls.Add(btnManualConnect);

            this.Size = new Size(500, 545);
        }

        private void RefreshDeviceList()
        {
            listDevices.Items.Clear();
            var devices = discovery.GetDiscoveredDevices();
            int count = 0;
            foreach (var d in devices)
            {
                listDevices.Items.Add(d);
                count++;
            }
            if (count == 0)
            {
                lblStatus.Text = "No other devices detected yet. Ensure other device has ClipSync open on same Wi-Fi.";
            }
            else
            {
                lblStatus.Text = count + " device(s) found! Select one and click 'Send Connection Request'.";
            }
        }

        private void ConnectToSelected()
        {
            DiscoveredDevice dev = listDevices.SelectedItem as DiscoveredDevice;
            if (dev == null) return;

            lblStatus.Text = "Sending connection request to " + dev.Name + " (" + dev.IP + ")... Waiting for approval...";
            btnConnect.Enabled = false;

            Task.Run(async () => {
                bool ok = await context.RequestPairingAndConnect(dev);
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke(new Action(() => {
                        btnConnect.Enabled = true;
                        if (ok)
                        {
                            MessageBox.Show("✅ Connection APPROVED by " + dev.Name + "!\n\nMulti-device clipboard sync is now active.", "ClipSync Connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            this.Close();
                        }
                        else
                        {
                            MessageBox.Show("❌ Connection to " + dev.Name + " was DECLINED or timed out.", "Connection Declined", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            lblStatus.Text = "Connection declined by " + dev.Name + ".";
                        }
                    }));
                }
            });
        }
    }

    // =========================================================================
    // ClipSync Core Application Context
    // =========================================================================
    public class ClipSyncContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private ContextMenu trayMenu;
        private MenuItem statusItem;
        private MenuItem pauseItem;
        private MenuItem autoStartItem;

        private ClipboardListenerWindow listenerWindow;
        private EmbeddedRelayServer embeddedServer;
        private DiscoveryService discoveryService;
        private DevicesForm activeDevicesForm;

        private ClientWebSocket webSocket;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private string serverUrl = "ws://127.0.0.1:8765/ws";
        private string deviceName = Environment.MachineName;
        private bool isPaused = false;
        private string lastHash = "";
        private string configPath;

        public ClipSyncContext()
        {
            configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            LoadConfig();

            // Auto-start on Windows boot by default so it always starts
            try
            {
                if (!IsAutoStartEnabled())
                {
                    SetAutoStart(true);
                }
            }
            catch { }

            // Auto-configure Windows Firewall in background if not already allowed
            Task.Run(() => EnsureFirewallConfigured(false));

            InitTray();

            // Native Win32 Clipboard Listener (Zero idle CPU)
            listenerWindow = new ClipboardListenerWindow(OnClipboardChanged);

            // Start Built-in Relay Server & Auto-Discovery
            StartNetworking();
        }

        public string GetLocalIpAddress()
        {
            try
            {
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    return endPoint.Address.ToString();
                }
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        private void StartNetworking()
        {
            // Start embedded server
            embeddedServer = new EmbeddedRelayServer(8765);
            embeddedServer.OnPairRequestReceived = OnIncomingPairRequest;
            embeddedServer.OnClipboardMessageReceived = (msg) => ProcessIncomingMessage(msg);
            embeddedServer.TryStart();

            discoveryService = new DiscoveryService();
            discoveryService.OnUdpPairRequestReceived = (fromName, fromIp) => OnIncomingPairRequest(fromName, fromIp);
            discoveryService.OnUdpClipboardReceived = (text, hash) => {
                if (hash == lastHash) return;
                lastHash = hash;
                SafeSetClipboardText(text);
                try
                {
                    string preview = text.Length > 40 ? text.Substring(0, 40) + "..." : text;
                    trayIcon.ShowBalloonTip(1000, "ClipSync", "📋 Synced: " + preview, ToolTipIcon.Info);
                }
                catch { }
            };
            discoveryService.StartBroadcasting(deviceName, 8765);
            discoveryService.StartListening(deviceName);

            // Start WebSocket Client loop
            Task.Run(() => RunWebSocketLoop());
        }

        private HashSet<string> allowedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private bool OnIncomingPairRequest(string fromName, string fromIp)
        {
            if (allowedDevices.Contains(fromName))
            {
                return true;
            }

            DialogResult res = MessageBox.Show(
                "📱 Device: " + fromName + " (" + fromIp + ")\n\nwants to connect and sync clipboard with this computer.\n\nDo you want to ALLOW this device?",
                "ClipSync — Incoming Connection Request",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            );
            if (res == DialogResult.Yes)
            {
                allowedDevices.Add(fromName);
                return true;
            }
            return false;
        }

        public async Task<bool> RequestPairingAndConnect(DiscoveredDevice dev)
        {
            if (dev.DeviceType == "android")
            {
                return await discoveryService.SendUdpPairRequestAsync(dev.IP, deviceName, GetLocalIpAddress(), 8765);
            }
            else
            {
                try
                {
                    string targetWs = "ws://" + dev.IP + ":" + dev.Port + "/ws";
                    using (ClientWebSocket ws = new ClientWebSocket())
                    {
                        ws.Options.Proxy = null;
                        var connectCts = new CancellationTokenSource(5000);
                        await ws.ConnectAsync(new Uri(targetWs), connectCts.Token);

                        // Send pairing request
                        string pairReq = "{\"type\":\"pair_req\",\"from_name\":\"" + deviceName + "\",\"from_ip\":\"" + GetLocalIpAddress() + "\"}";
                        byte[] bytes = Encoding.UTF8.GetBytes(pairReq);
                        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);

                        // Wait for response
                        byte[] buf = new byte[4096];
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                        string resp = Encoding.UTF8.GetString(buf, 0, r.Count);

                        if (resp.Contains("\"status\":\"accepted\""))
                        {
                            serverUrl = targetWs;
                            SaveConfig();
                            cts.Cancel();
                            cts = new CancellationTokenSource();
                            Task.Run(() => RunWebSocketLoop());
                            return true;
                        }
                    }
                }
                catch { }

                return await discoveryService.SendUdpPairRequestAsync(dev.IP, deviceName, GetLocalIpAddress(), 8765);
            }
        }

        private void ShowDevicesWindow()
        {
            if (activeDevicesForm == null || activeDevicesForm.IsDisposed)
            {
                activeDevicesForm = new DevicesForm(discoveryService, this);
            }
            activeDevicesForm.Show();
            activeDevicesForm.BringToFront();
        }

        private void EnsureFirewallConfigured(bool userTriggered)
        {
            string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".firewall_ok_v2");
            if (!userTriggered && File.Exists(marker)) return;

            try
            {
                string exePath = Application.ExecutablePath;

                // 1. Allow the application executable for Private, Public, and Domain network profiles
                RunFirewallCommand("advfirewall firewall delete rule name=\"ClipSync App\"");
                RunFirewallCommand("advfirewall firewall add rule name=\"ClipSync App\" dir=in action=allow program=\"" + exePath + "\" profile=any enable=yes");

                // 2. Open TCP 8765 (WebSocket Relay Server) for all network profiles
                RunFirewallCommand("advfirewall firewall delete rule name=\"ClipSync Server\"");
                RunFirewallCommand("advfirewall firewall add rule name=\"ClipSync Server\" dir=in action=allow protocol=TCP localport=8765 profile=any enable=yes");

                // 3. Open UDP 8766 (LAN Auto-Discovery) for all network profiles
                RunFirewallCommand("advfirewall firewall delete rule name=\"ClipSync Discovery\"");
                RunFirewallCommand("advfirewall firewall add rule name=\"ClipSync Discovery\" dir=in action=allow protocol=UDP localport=8766 profile=any enable=yes");

                File.WriteAllText(marker, "ok");
            }
            catch { }
        }

        private void RunFirewallCommand(string netshArgs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "netsh";
                psi.Arguments = netshArgs;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit(4000);
                }
            }
            catch
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = "netsh";
                    psi.Arguments = netshArgs;
                    psi.Verb = "runas";
                    psi.UseShellExecute = true;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    using (Process p = Process.Start(psi))
                    {
                        if (p != null) p.WaitForExit(4000);
                    }
                }
                catch { }
            }
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    serverUrl = ExtractJsonValue(json, "server_url", serverUrl);
                    deviceName = ExtractJsonValue(json, "device_name", deviceName);
                }
                else
                {
                    SaveConfig();
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                string json = "{\n  \"server_url\": \"" + serverUrl + "\",\n  \"device_name\": \"" + deviceName + "\",\n  \"auto_start\": true\n}";
                File.WriteAllText(configPath, json);
            }
            catch { }
        }

        private string ExtractJsonValue(string json, string key, string fallback)
        {
            string pattern = "\"" + key + "\":";
            int idx = json.IndexOf(pattern);
            if (idx == -1) return fallback;
            int startQuote = json.IndexOf("\"", idx + pattern.Length);
            if (startQuote == -1) return fallback;
            int endQuote = json.IndexOf("\"", startQuote + 1);
            if (endQuote == -1) return fallback;
            return json.Substring(startQuote + 1, endQuote - startQuote - 1);
        }

        private void InitTray()
        {
            trayMenu = new ContextMenu();
            statusItem = new MenuItem("Status: Connecting...");
            statusItem.Enabled = false;
            trayMenu.MenuItems.Add(statusItem);

            trayMenu.MenuItems.Add(new MenuItem("This PC IP: " + GetLocalIpAddress() + " (Port 8765)") { Enabled = false });
            trayMenu.MenuItems.Add("-");

            MenuItem devicesItem = new MenuItem("📶 Nearby Devices & Pairing...", (s, e) => ShowDevicesWindow());
            devicesItem.DefaultItem = true;
            trayMenu.MenuItems.Add(devicesItem);

            pauseItem = new MenuItem("Pause Sync", (s, e) => {
                isPaused = !isPaused;
                pauseItem.Checked = isPaused;
                UpdateStatus(isPaused ? "Paused" : "Connected");
            });
            trayMenu.MenuItems.Add(pauseItem);

            autoStartItem = new MenuItem("Start on Windows Boot", (s, e) => {
                bool newState = !IsAutoStartEnabled();
                SetAutoStart(newState);
                autoStartItem.Checked = newState;
            });
            autoStartItem.Checked = IsAutoStartEnabled();
            trayMenu.MenuItems.Add(autoStartItem);

            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Exit ClipSync", (s, e) => {
                cts.Cancel();
                if (listenerWindow != null) listenerWindow.Cleanup();
                if (embeddedServer != null) embeddedServer.Stop();
                if (discoveryService != null) discoveryService.Stop();
                trayIcon.Visible = false;
                ExitThread();
            });

            trayIcon = new NotifyIcon();
            trayIcon.Text = "ClipSync: Starting...";
            trayIcon.Icon = System.Drawing.SystemIcons.Application;
            trayIcon.ContextMenu = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += (s, e) => ShowDevicesWindow();
        }

        private void UpdateStatus(string text)
        {
            try
            {
                statusItem.Text = "Status: " + text;
                string tip = "ClipSync: " + text;
                trayIcon.Text = tip.Substring(0, Math.Min(63, tip.Length));
            }
            catch { }
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue("ClipSync") != null;
                }
            }
            catch { return false; }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (enable)
                            key.SetValue("ClipSync", "\"" + Application.ExecutablePath + "\"");
                        else
                            key.DeleteValue("ClipSync", false);
                    }
                }
            }
            catch { }
        }

        private void OnClipboardChanged()
        {
            if (isPaused) return;

            string text = SafeGetClipboardText();
            if (string.IsNullOrEmpty(text)) return;

            string hash = ComputeSha256(text);
            if (hash == lastHash) return; // Echo loop prevention

            lastHash = hash;
            BroadcastClipboard(text);
        }

        private string SafeGetClipboardText()
        {
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        return Clipboard.GetText();
                    }
                    return null;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(40);
                }
                catch { break; }
            }
            return null;
        }

        private void SafeSetClipboardText(string text)
        {
            Thread staThread = new Thread(() => {
                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        Clipboard.SetText(text);
                        break;
                    }
                    catch (ExternalException)
                    {
                        Thread.Sleep(40);
                    }
                    catch { break; }
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join(1200);
        }

        private async void BroadcastClipboard(string text)
        {
            try
            {
                string escapedData = EscapeJson(text);
                string json = "{\"type\":\"clipboard\",\"data\":\"" + escapedData + "\",\"device_name\":\"" + deviceName + "\",\"device_type\":\"windows\",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}";

                if (embeddedServer != null && embeddedServer.IsRunning)
                {
                    embeddedServer.BroadcastRaw(json);
                }

                if (webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
                }

                if (discoveryService != null)
                {
                    discoveryService.SendUdpClipboard(text, lastHash, deviceName);
                }
            }
            catch { }
        }

        private async Task RunWebSocketLoop()
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    string fullUrl = serverUrl + (serverUrl.Contains("?") ? "&" : "?") + "device_name=" + Uri.EscapeDataString(deviceName) + "&device_type=windows";
                    UpdateStatus("Connecting...");

                    using (webSocket = new ClientWebSocket())
                    {
                        webSocket.Options.Proxy = null;
                        await webSocket.ConnectAsync(new Uri(fullUrl), cts.Token);
                        UpdateStatus("Connected");

                        var pingCts = new CancellationTokenSource();
                        var pingTask = Task.Run(async () => {
                            while (webSocket.State == WebSocketState.Open && !pingCts.IsCancellationRequested)
                            {
                                await Task.Delay(15000);
                                if (webSocket.State == WebSocketState.Open)
                                {
                                    try
                                    {
                                        byte[] pingBytes = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
                                        await webSocket.SendAsync(new ArraySegment<byte>(pingBytes), WebSocketMessageType.Text, true, pingCts.Token);
                                    }
                                    catch { }
                                }
                            }
                        });

                        byte[] buffer = new byte[65536];
                        while (webSocket.State == WebSocketState.Open && !cts.IsCancellationRequested)
                        {
                            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                pingCts.Cancel();
                                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cts.Token);
                                break;
                            }
                            if (result.MessageType == WebSocketMessageType.Text)
                            {
                                string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                ProcessIncomingMessage(msg);
                            }
                        }
                        pingCts.Cancel();
                    }
                }
                catch
                {
                    UpdateStatus("Ready (Hosting / Listening)");
                }

                if (!cts.IsCancellationRequested)
                {
                    await Task.Delay(3000);
                }
            }
        }

        private void ProcessIncomingMessage(string json)
        {
            if (!json.Contains("\"type\":\"clipboard\"") && !json.Contains("\"type\": \"clipboard\"")) return;

            string data = ExtractJsonData(json);
            if (!string.IsNullOrEmpty(data))
            {
                string hash = ComputeSha256(data);
                if (hash == lastHash) return;
                lastHash = hash;

                SafeSetClipboardText(data);

                try
                {
                    string preview = data.Length > 40 ? data.Substring(0, 40) + "..." : data;
                    trayIcon.ShowBalloonTip(1500, "ClipSync", "📋 Synced: " + preview, ToolTipIcon.Info);
                }
                catch { }
            }
        }

        private string ExtractJsonData(string json)
        {
            string pattern = "\"data\":";
            int idx = json.IndexOf(pattern);
            if (idx == -1) return null;
            int startQuote = json.IndexOf("\"", idx + pattern.Length);
            if (startQuote == -1) return null;

            StringBuilder sb = new StringBuilder();
            bool escape = false;
            for (int i = startQuote + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (escape)
                {
                    if (c == 'n') sb.Append('\n');
                    else if (c == 'r') sb.Append('\r');
                    else if (c == 't') sb.Append('\t');
                    else if (c == '"') sb.Append('"');
                    else if (c == '\\') sb.Append('\\');
                    else sb.Append(c);
                    escape = false;
                }
                else if (c == '\\') escape = true;
                else if (c == '"') break;
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private string EscapeJson(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\b') sb.Append("\\b");
                else if (c == '\f') sb.Append("\\f");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private string ComputeSha256(string raw)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder builder = new StringBuilder();
                foreach (byte b in bytes) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "ClipSyncSingleInstanceMutex_rahat300809", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("ClipSync is already running in your System Tray (near clock). Double click its icon to open Devices & Pairing.", "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ClipSyncContext());
            }
        }
    }
}
