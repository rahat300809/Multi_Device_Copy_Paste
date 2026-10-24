# 📋 ClipSync — Multi-Device Copy-Paste Sync

<div align="center">

![ClipSync Banner](https://img.shields.io/badge/ClipSync-v1.0.0-blue?style=for-the-badge&logo=clipboard)
![Windows](https://img.shields.io/badge/Windows-EXE-0078D4?style=for-the-badge&logo=windows)
![Android](https://img.shields.io/badge/Android-APK-3DDC84?style=for-the-badge&logo=android)
![Docker](https://img.shields.io/badge/Docker-Server-2496ED?style=for-the-badge&logo=docker)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)

**Copy on one device → instantly pastes on ALL your devices.**  
Works over Wi-Fi, LAN, Tailscale, or any shared network.

</div>

---

## ⬇️ Download

| Platform | File | Size |
|----------|------|------|
| 🪟 **Windows** (EXE) | [ClipSync.exe](https://github.com/rahat300809/Multi_Device_Copy_Paste/releases/latest/download/ClipSync.exe) | ~16 KB |
| 🤖 **Android** (APK) | [ClipSync.apk](https://github.com/rahat300809/Multi_Device_Copy_Paste/releases/latest/download/ClipSync.apk) | ~98 KB |
| 🐳 **Server** (Docker) | `docker-compose up -d` | ~35 MB |

> **Android Note:** Enable "Install from unknown sources" in Settings before installing the APK.

---

## 🚀 How It Works

```
  [Windows PC]  ──────┐
                       ├──▶ [ClipSync Relay Server] ◀──── WebSocket
  [Android Phone] ─────┘                              (ws://YOUR_IP:8765/ws)
```

1. You copy text on **any** device
2. That device sends it to the **relay server** via WebSocket  
3. The server **broadcasts** to all other connected devices  
4. All devices **automatically set their clipboard** to the synced text

### Features
- ⚡ **Ultra-lightweight**: EXE is 16 KB, APK is 98 KB
- 🔄 **Bidirectional**: Any → All devices, instantly
- 🔒 **Deduplication**: SHA-256 hash prevents echo loops
- 📴 **Background**: Auto-starts silently, zero UI
- 🔁 **Auto-reconnect**: Handles network drops gracefully
- 📌 **Boot startup**: EXE registers in Windows startup; APK starts on Android boot
- 🌐 **Network agnostic**: Wi-Fi, LAN, Tailscale, VPN — any shared IP works

---

## 🛠️ Setup

### Step 1: Run the Relay Server

**Option A — Docker (Recommended)**
```bash
git clone https://github.com/rahat300809/Multi_Device_Copy_Paste.git
cd Multi_Device_Copy_Paste/server
docker-compose up -d
```

**Option B — Python directly**
```bash
cd server
pip install -r requirements.txt
python server.py
```

Server starts on port **8765**. Web dashboard: `http://YOUR_IP:8765`

---

### Step 2: Configure Windows Client (EXE)

1. Download [`ClipSync.exe`](https://github.com/rahat300809/Multi_Device_Copy_Paste/releases/latest/download/ClipSync.exe)  
2. Place it anywhere (e.g., `C:\ClipSync\ClipSync.exe`)
3. Edit `config.json` (auto-created next to the EXE on first run):

```json
{
  "server_url": "ws://YOUR_SERVER_IP:8765/ws",
  "device_name": "My Laptop",
  "auto_start": true
}
```

4. Double-click `ClipSync.exe` — it appears in the **system tray** (bottom-right)
5. Right-click the tray icon → **"Start on Windows Boot"** to enable auto-start

---

### Step 3: Configure Android Client (APK)

1. Download [`ClipSync.apk`](https://github.com/rahat300809/Multi_Device_Copy_Paste/releases/latest/download/ClipSync.apk)
2. Install it (allow unknown sources if prompted)
3. Open ClipSync app → enter your **Server IP** (e.g., `192.168.1.100`)
4. Tap **Connect** — grant Accessibility Service permission when asked
5. The service runs silently in the background and starts on boot

---

## 🌐 Network Configurations

| Setup | Server URL |
|-------|------------|
| Local Wi-Fi | `ws://192.168.1.X:8765/ws` |
| Tailscale | `ws://100.X.X.X:8765/ws` |
| Same machine (test) | `ws://127.0.0.1:8765/ws` |
| Custom domain | `ws://yourdomain.com:8765/ws` |

Find your server IP with:
- **Windows**: `ipconfig` → IPv4 Address
- **Linux/Mac**: `hostname -I`
- **Tailscale**: `tailscale ip -4`

---

## 🏗️ Project Structure

```
Multi_Device_Copy_Paste/
├── ClipSync.exe              # Windows client (ready to run)
├── ClipSync.apk              # Android client (ready to install)
├── config.json               # Windows client configuration
│
├── server/                   # Relay server
│   ├── server.py             # AsyncIO WebSocket relay + Web UI
│   ├── requirements.txt      # Python deps (aiohttp only)
│   ├── Dockerfile            # Alpine-based ~35MB image
│   └── docker-compose.yml    # One-command deployment
│
├── windows_client/
│   └── ClipSync.cs           # Native C# source (no external deps)
│
└── android_client/
    ├── build_apk.bat         # One-click APK rebuild script
    └── app/src/main/
        ├── AndroidManifest.xml
        └── java/com/clipsync/app/
            ├── MainActivity.java
            ├── ClipboardSyncService.java
            ├── ClipboardAccessibilityService.java
            ├── SyncWebSocketClient.java
            └── BootReceiver.java
```

---

## 🔧 Rebuild from Source

### Windows EXE (requires Windows, no extra installs):
```cmd
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  /target:winexe /optimize+ /platform:anycpu ^
  /out:ClipSync.exe windows_client\ClipSync.cs
```

### Android APK (requires Android SDK Build-Tools 34):
```cmd
cd android_client
build_apk.bat
```

---

## 🔒 Security Notes

- All traffic is **plaintext** over WebSocket by default — use within a trusted network or VPN (Tailscale provides encrypted tunnels)
- Optional token authentication: set `AUTH_TOKEN` environment variable on the server; clients pass `?token=YOUR_TOKEN`
- No data is stored on disk — clipboard history is in-memory only (cleared on server restart)

---

## 📄 License

MIT License — free to use, modify, and distribute.

---

<div align="center">
Made with ❤️ — copy once, paste everywhere.
</div>
