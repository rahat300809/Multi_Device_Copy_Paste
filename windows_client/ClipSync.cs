using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
using Microsoft.Win32;

namespace ClipSync
{
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
    // Standalone Embedded WebSocket Relay Server (Zero Dependencies, Zero Python)
    // =========================================================================
    public class EmbeddedRelayServer
    {
        private TcpListener listener;
        private CancellationTokenSource cts = new CancellationTokenSource();
        private ConcurrentDictionary<string, TcpClient> clients = new ConcurrentDictionary<string, TcpClient>();
        private string lastHash = "";
        public bool IsRunning { get; private set; }
        public int Port { get; private set; }

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
                    BroadcastToOthers(id, message);
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
                    try
                    {
                        kv.Value.GetStream().Write(frame, 0, frame.Length);
                    }
                    catch { }
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
    }

    // =========================================================================
    // LAN Auto-Discovery Service (Zero Config: Laptop 1 & Laptop 2 Auto-Connect)
    // =========================================================================
    public class DiscoveryService
    {
        private const int DISCOVERY_PORT = 8766;
        private CancellationTokenSource cts = new CancellationTokenSource();
        public event Action<string> ServerDiscovered;

        public void StartBroadcasting(int serverPort)
        {
            Task.Run(async () => {
                using (UdpClient udp = new UdpClient())
                {
                    udp.EnableBroadcast = true;
                    IPEndPoint endpoint = new IPEndPoint(IPAddress.Broadcast, DISCOVERY_PORT);
                    byte[] data = Encoding.UTF8.GetBytes("CLIPSYNC_SERVER:" + serverPort);

                    while (!cts.IsCancellationRequested)
                    {
                        try { await udp.SendAsync(data, data.Length, endpoint); } catch { }
                        await Task.Delay(3000);
                    }
                }
            });
        }

        public void StartListening()
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
                            if (msg.StartsWith("CLIPSYNC_SERVER:"))
                            {
                                string portStr = msg.Substring(16);
                                string serverIp = res.RemoteEndPoint.Address.ToString();
                                string uri = "ws://" + serverIp + ":" + portStr + "/ws";
                                if (ServerDiscovered != null) ServerDiscovered(uri);
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

            // Auto-configure Windows Firewall in background if not already allowed
            Task.Run(() => EnsureFirewallConfigured(false));

            InitTray();

            // Native Win32 Clipboard Listener (Zero idle CPU)
            listenerWindow = new ClipboardListenerWindow(OnClipboardChanged);

            // Start Built-in Relay Server & Auto-Discovery
            StartNetworking();
        }

        private void StartNetworking()
        {
            discoveryService = new DiscoveryService();
            discoveryService.ServerDiscovered += (discoveredUrl) => {
                if (serverUrl.Contains("127.0.0.1") || serverUrl.Contains("localhost"))
                {
                    serverUrl = discoveredUrl;
                    UpdateStatus("Auto-Connected to " + discoveredUrl);
                }
            };
            discoveryService.StartListening();

            // Try to start built-in server on port 8765
            embeddedServer = new EmbeddedRelayServer(8765);
            if (embeddedServer.TryStart())
            {
                // This machine is hosting the relay! Broadcast announcement to LAN
                discoveryService.StartBroadcasting(8765);
                serverUrl = "ws://127.0.0.1:8765/ws";
            }

            // Start WebSocket Worker thread
            Task.Run(() => RunWebSocketLoop());
        }

        private void EnsureFirewallConfigured(bool userTriggered)
        {
            string marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".firewall_ok");
            if (!userTriggered && File.Exists(marker)) return;

            try
            {
                Process check = new Process();
                check.StartInfo.FileName = "netsh";
                check.StartInfo.Arguments = "advfirewall firewall show rule name=\"ClipSync Server\"";
                check.StartInfo.CreateNoWindow = true;
                check.StartInfo.UseShellExecute = false;
                check.StartInfo.RedirectStandardOutput = true;
                check.Start();
                string output = check.StandardOutput.ReadToEnd();
                check.WaitForExit();

                if (output.Contains("ClipSync Server"))
                {
                    File.WriteAllText(marker, "ok");
                    if (userTriggered)
                    {
                        MessageBox.Show("Windows Firewall rule 'ClipSync Server' is active and allowing traffic!", "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                // Auto elevate netsh command
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "netsh";
                psi.Arguments = "advfirewall firewall add rule name=\"ClipSync Server\" dir=in action=allow protocol=TCP localport=8765,8766";
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.UseShellExecute = true;

                Process p = Process.Start(psi);
                p.WaitForExit();
                if (p.ExitCode == 0)
                {
                    File.WriteAllText(marker, "ok");
                    if (userTriggered)
                    {
                        MessageBox.Show("Ports 8765 and 8766 are now open in Windows Firewall!", "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                if (userTriggered)
                {
                    MessageBox.Show("Could not add firewall rule: " + ex.Message, "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
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
                    string defaultJson = "{\n  \"server_url\": \"" + serverUrl + "\",\n  \"device_name\": \"" + deviceName + "\",\n  \"auto_start\": true\n}";
                    File.WriteAllText(configPath, defaultJson);
                }
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

            trayMenu.MenuItems.Add(new MenuItem("Server: Auto-Detect (Active)") { Enabled = false });
            trayMenu.MenuItems.Add("-");

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

            trayMenu.MenuItems.Add("Allow Firewall Ports (8765)...", (s, e) => EnsureFirewallConfigured(true));

            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Settings (config.json)", (s, e) => {
                try { Process.Start("notepad.exe", configPath); } catch { }
            });
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

                // Also relay through embedded server directly
                if (embeddedServer != null && embeddedServer.IsRunning)
                {
                    embeddedServer.BroadcastRaw(json);
                }

                if (webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
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
                    UpdateStatus("Waiting for Sync Peer...");
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
                    MessageBox.Show("ClipSync is already running in your System Tray (near clock).", "ClipSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ClipSyncContext());
            }
        }
    }
}
