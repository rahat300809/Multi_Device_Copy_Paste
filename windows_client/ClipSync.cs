using System;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClipSync
{
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
            CreateParams cp = new CreateParams
            {
                Caption = "ClipSyncListenerWindow",
                Style = 0
            };
            CreateHandle(cp);
            AddClipboardFormatListener(this.Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                if (onUpdateCallback != null)
                {
                    onUpdateCallback();
                }
            }
            base.WndProc(ref m);
        }

        public void Cleanup()
        {
            RemoveClipboardFormatListener(this.Handle);
            DestroyHandle();
        }
    }

    public class ClipSyncContext : ApplicationContext
    {
        private NotifyIcon trayIcon;
        private ContextMenu trayMenu;
        private MenuItem statusItem;
        private MenuItem pauseItem;
        private MenuItem autoStartItem;

        private ClipboardListenerWindow listenerWindow;
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

            InitTray();

            // Native Win32 Clipboard Listener Window (0.0% idle CPU)
            listenerWindow = new ClipboardListenerWindow(OnClipboardChanged);

            // Start WebSocket Worker thread
            Task.Run(() => RunWebSocketLoop());
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

            trayMenu.MenuItems.Add(new MenuItem("Server: " + serverUrl) { Enabled = false });
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

            trayMenu.MenuItems.Add("-");
            trayMenu.MenuItems.Add("Settings (config.json)", (s, e) => {
                try { System.Diagnostics.Process.Start("notepad.exe", configPath); } catch { }
            });
            trayMenu.MenuItems.Add("Exit ClipSync", (s, e) => {
                cts.Cancel();
                if (listenerWindow != null) listenerWindow.Cleanup();
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

            string text = null;
            try
            {
                if (Clipboard.ContainsText())
                {
                    text = Clipboard.GetText();
                }
            }
            catch { return; }

            if (string.IsNullOrEmpty(text)) return;

            string hash = ComputeSha256(text);
            if (hash == lastHash) return; // Echo prevention

            lastHash = hash;
            BroadcastClipboard(text);
        }

        private async void BroadcastClipboard(string text)
        {
            if (webSocket == null || webSocket.State != WebSocketState.Open) return;

            try
            {
                string escapedData = EscapeJson(text);
                string json = "{\"type\":\"clipboard\",\"data\":\"" + escapedData + "\",\"device_name\":\"" + deviceName + "\",\"device_type\":\"windows\",\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "}";
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
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

                        // 15-second keepalive ping to prevent idle disconnection
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
                catch (Exception)
                {
                    UpdateStatus("Disconnected (Retrying...)");
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
                lastHash = ComputeSha256(data);
                Thread staThread = new Thread(() => {
                    try
                    {
                        Clipboard.SetText(data);
                    }
                    catch { }
                });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                staThread.Join(1000);
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
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(c);
                }
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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new ClipSyncContext());
        }
    }
}
