import os
import sys
import json
import time
import socket
import hashlib
import threading
import winreg
import ctypes
from ctypes import wintypes
import urllib.request
import urllib.parse
import asyncio

# Attempt to import websockets (installed)
try:
    import websockets
except ImportError:
    websockets = None

# Windows Win32 API Constants
WM_USER = 0x0400
WM_TRAYICON = WM_USER + 20
WM_CLIPBOARDUPDATE = 0x031D
WM_COMMAND = 0x0111
WM_RBUTTONUP = 0x0205
WM_LBUTTONDBLCLK = 0x0203

NIF_MESSAGE = 0x0001
NIF_ICON = 0x0002
NIF_TIP = 0x0004
NIM_ADD = 0x0000
NIM_MODIFY = 0x0001
NIM_DELETE = 0x0002

MF_STRING = 0x0000
MF_CHECKED = 0x0008
MF_UNCHECKED = 0x0000
MF_GRAYED = 0x0001
MF_SEPARATOR = 0x0800
TPM_RIGHTBUTTON = 0x0002

CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
shell32 = ctypes.windll.shell32

CONFIG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "config.json")
APP_NAME = "ClipSync"
REG_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"

# Struct for Windows Tray Icon (NOTIFYICONDATAW)
class NOTIFYICONDATAW(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("hWnd", wintypes.HWND),
        ("uID", wintypes.UINT),
        ("uFlags", wintypes.UINT),
        ("uCallbackMessage", wintypes.UINT),
        ("hIcon", wintypes.HICON),
        ("szTip", wintypes.WCHAR * 128),
        ("dwState", wintypes.DWORD),
        ("dwStateMask", wintypes.DWORD),
        ("szInfo", wintypes.WCHAR * 256),
        ("uTimeoutOrVersion", wintypes.UINT),
        ("szInfoTitle", wintypes.WCHAR * 64),
        ("dwInfoFlags", wintypes.DWORD),
    ]

# Menu Command IDs
CMD_STATUS = 1001
CMD_MODE = 1002
CMD_PAUSE = 1003
CMD_AUTOSTART = 1004
CMD_COPY_LAST = 1005
CMD_SETTINGS = 1006
CMD_EXIT = 1007

class ClipSyncClient:
    def __init__(self):
        self.load_config()
        self.last_hash = None
        self.is_paused = False
        self.is_connected = False
        self.status_text = "Connecting..."
        self.last_received_text = ""
        self.stop_event = threading.Event()
        self.hwnd = None
        self.nid = None
        self.ws_loop = None
        self.active_ws = None

    def load_config(self):
        default = {
            "mode": "websocket",  # "websocket" (Docker / LAN / Tailscale) or "firebase"
            "server_url": "ws://127.0.0.1:8765/ws",
            "firebase_url": "https://YOUR-APP.firebaseio.com/clipboard.json",
            "firebase_auth": "",
            "device_name": socket.gethostname(),
            "auto_start": True
        }
        if os.path.exists(CONFIG_FILE):
            try:
                with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                    self.config = json.load(f)
            except Exception:
                self.config = default
        else:
            self.config = default
            self.save_config()

    def save_config(self):
        try:
            with open(CONFIG_FILE, "w", encoding="utf-8") as f:
                json.dump(self.config, f, indent=2)
        except Exception as e:
            print(f"[!] Error saving config: {e}")

    # --- Windows Registry Auto-Startup ---
    def is_auto_start_enabled(self):
        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, REG_PATH, 0, winreg.KEY_READ)
            val, _ = winreg.QueryValueEx(key, APP_NAME)
            winreg.CloseKey(key)
            return bool(val)
        except Exception:
            return False

    def set_auto_start(self, enable=True):
        if getattr(sys, 'frozen', False):
            exe_path = f'"{sys.executable}"'
        else:
            # Running as Python script: Use pythonw.exe to prevent any console window popup
            pythonw = os.path.join(os.path.dirname(sys.executable), "pythonw.exe")
            if not os.path.exists(pythonw):
                pythonw = sys.executable
            exe_path = f'"{pythonw}" "{os.path.abspath(__file__)}"'

        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, REG_PATH, 0, winreg.KEY_SET_VALUE)
            if enable:
                winreg.SetValueEx(key, APP_NAME, 0, winreg.REG_SZ, exe_path)
                print(f"[+] Auto-startup configured: {exe_path}")
            else:
                try:
                    winreg.DeleteValue(key, APP_NAME)
                    print("[-] Auto-startup removed.")
                except FileNotFoundError:
                    pass
            winreg.CloseKey(key)
            self.config["auto_start"] = enable
            self.save_config()
        except Exception as e:
            print(f"[!] Registry auto-start error: {e}")

    # --- Native Win32 Clipboard Operations (0% CPU) ---
    def get_clipboard_text(self):
        for _ in range(5):
            if user32.OpenClipboard(None):
                try:
                    h_data = user32.GetClipboardData(CF_UNICODETEXT)
                    if h_data:
                        kernel32.GlobalLock.restype = ctypes.c_wchar_p
                        p_data = kernel32.GlobalLock(h_data)
                        if p_data:
                            text = str(p_data)
                            kernel32.GlobalUnlock(h_data)
                            return text
                    return None
                finally:
                    user32.CloseClipboard()
            time.sleep(0.015)
        return None

    def set_clipboard_text(self, text):
        if not text:
            return
        self.last_hash = hashlib.sha256(text.encode("utf-8")).hexdigest()
        self.last_received_text = text

        for _ in range(5):
            if user32.OpenClipboard(None):
                try:
                    user32.EmptyClipboard()
                    encoded = text.encode("utf-16le") + b"\x00\x00"
                    h_mem = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(encoded))
                    if h_mem:
                        kernel32.GlobalLock.restype = ctypes.c_void_p
                        p_mem = kernel32.GlobalLock(h_mem)
                        if p_mem:
                            ctypes.memmove(p_mem, encoded, len(encoded))
                            kernel32.GlobalUnlock(h_mem)
                            user32.SetClipboardData(CF_UNICODETEXT, h_mem)
                            print(f"[+] Received & Copied to Windows Clipboard: {text[:40]}...")
                            return True
                finally:
                    user32.CloseClipboard()
            time.sleep(0.015)
        return False

    def on_clipboard_changed(self):
        if self.is_paused:
            return
        text = self.get_clipboard_text()
        if not text:
            return
        h = hashlib.sha256(text.encode("utf-8")).hexdigest()
        if h == self.last_hash:
            return  # Echo cancellation

        self.last_hash = h
        print(f"[>] Local copy event captured: {text[:40]}... Broadcasting to network...")
        self.broadcast_clipboard(text)

    # --- Network Broadcasting ---
    def broadcast_clipboard(self, text):
        mode = self.config.get("mode", "websocket")
        if mode == "websocket":
            if self.active_ws and self.ws_loop:
                payload = json.dumps({
                    "type": "clipboard",
                    "device_name": self.config.get("device_name", "Windows PC"),
                    "device_type": "windows",
                    "data": text,
                    "timestamp": int(time.time())
                })
                asyncio.run_coroutine_threadsafe(self._ws_send(payload), self.ws_loop)
        else:
            threading.Thread(target=self._firebase_send, args=(text,), daemon=True).start()

    async def _ws_send(self, payload):
        try:
            if self.active_ws:
                await self.active_ws.send(payload)
        except Exception as e:
            print(f"[!] WS send error: {e}")

    def _firebase_send(self, text):
        url = self.config.get("firebase_url")
        if not url:
            return
        try:
            data = json.dumps({
                "data": text,
                "device_name": self.config.get("device_name", "Windows PC"),
                "device_type": "windows",
                "timestamp": int(time.time()),
                "hash": hashlib.sha256(text.encode("utf-8")).hexdigest()
            }).encode("utf-8")
            req = urllib.request.Request(url, data=data, method="PUT")
            req.add_header("Content-Type", "application/json")
            with urllib.request.urlopen(req, timeout=5) as resp:
                pass
        except Exception as e:
            print(f"[!] Firebase send error: {e}")

    # --- WebSocket Worker Thread ---
    def run_websocket_loop(self):
        self.ws_loop = asyncio.new_event_loop()
        asyncio.set_event_loop(self.ws_loop)

        async def worker():
            while not self.stop_event.is_set():
                url = self.config.get("server_url", "ws://127.0.0.1:8765/ws")
                device = urllib.parse.quote(self.config.get("device_name", "Windows PC"))
                full_url = f"{url}?device_name={device}&device_type=windows"

                self.status_text = "Connecting..."
                self.update_tray_tooltip()
                print(f"[*] Connecting to {full_url}")

                try:
                    async with websockets.connect(full_url, ping_interval=20, ping_timeout=10) as ws:
                        self.active_ws = ws
                        self.is_connected = True
                        self.status_text = f"Connected ({url})"
                        self.update_tray_tooltip()
                        print(f"[+] ClipSync Relay Connected to {url}")

                        async for msg_str in ws:
                            if self.stop_event.is_set():
                                break
                            try:
                                msg = json.loads(msg_str)
                                if msg.get("type") == "clipboard":
                                    data = msg.get("data")
                                    if data:
                                        self.set_clipboard_text(data)
                            except Exception:
                                pass
                except Exception as e:
                    print(f"[-] Connection lost: {e}")
                finally:
                    self.active_ws = None
                    self.is_connected = False
                    self.status_text = "Disconnected (Retrying...)"
                    self.update_tray_tooltip()

                if not self.stop_event.is_set():
                    await asyncio.sleep(3)

        self.ws_loop.run_until_complete(worker())

    # --- Firebase SSE Worker Thread ---
    def run_firebase_loop(self):
        while not self.stop_event.is_set():
            url = self.config.get("firebase_url")
            if not url:
                time.sleep(4)
                continue
            self.status_text = "Firebase: Connecting..."
            self.update_tray_tooltip()
            try:
                req = urllib.request.Request(url, headers={"Accept": "text/event-stream"})
                with urllib.request.urlopen(req, timeout=60) as response:
                    self.is_connected = True
                    self.status_text = "Firebase: Live"
                    self.update_tray_tooltip()
                    print("[+] Connected to Firebase Live Stream")

                    for line in response:
                        if self.stop_event.is_set():
                            break
                        decoded = line.decode('utf-8').strip()
                        if decoded.startswith("data:"):
                            raw = decoded[5:].strip()
                            if raw and raw != "null":
                                try:
                                    payload = json.loads(raw)
                                    if isinstance(payload, dict):
                                        content = payload.get("data", payload)
                                        if isinstance(content, dict):
                                            txt = content.get("data")
                                            sender = content.get("device_name")
                                        else:
                                            txt = str(content)
                                            sender = ""
                                        if txt and sender != self.config.get("device_name"):
                                            self.set_clipboard_text(txt)
                                except Exception:
                                    pass
            except Exception as e:
                self.is_connected = False
                self.status_text = "Firebase: Error"
                self.update_tray_tooltip()
                time.sleep(4)

    # --- Pure Win32 System Tray & Window Message Pump ---
    def update_tray_tooltip(self):
        if self.nid and self.hwnd:
            tip = f"ClipSync: {self.status_text}"[:127]
            self.nid.szTip = tip
            self.nid.uFlags = NIF_TIP
            shell32.Shell_NotifyIconW(NIM_MODIFY, ctypes.byref(self.nid))

    def show_context_menu(self):
        h_menu = user32.CreatePopupMenu()

        mode_name = self.config.get("mode", "websocket").upper()
        user32.AppendMenuW(h_menu, MF_STRING | MF_GRAYED, CMD_STATUS, f"Status: {self.status_text}")
        user32.AppendMenuW(h_menu, MF_STRING | MF_GRAYED, CMD_MODE, f"Mode: {mode_name}")
        user32.AppendMenuW(h_menu, MF_SEPARATOR, 0, None)

        pause_flags = MF_STRING | (MF_CHECKED if self.is_paused else MF_UNCHECKED)
        user32.AppendMenuW(h_menu, pause_flags, CMD_PAUSE, "Pause Sync")

        autostart_flags = MF_STRING | (MF_CHECKED if self.is_auto_start_enabled() else MF_UNCHECKED)
        user32.AppendMenuW(h_menu, autostart_flags, CMD_AUTOSTART, "Start on Windows Boot")

        if self.last_received_text:
            user32.AppendMenuW(h_menu, MF_STRING, CMD_COPY_LAST, "Re-Copy Last Received")

        user32.AppendMenuW(h_menu, MF_SEPARATOR, 0, None)
        user32.AppendMenuW(h_menu, MF_STRING, CMD_SETTINGS, "Open config.json")
        user32.AppendMenuW(h_menu, MF_STRING, CMD_EXIT, "Exit ClipSync")

        pt = wintypes.POINT()
        user32.GetCursorPos(ctypes.byref(pt))
        user32.SetForegroundWindow(self.hwnd)
        user32.TrackPopupMenu(h_menu, TPM_RIGHTBUTTON, pt.x, pt.y, 0, self.hwnd, None)
        user32.DestroyMenu(h_menu)

    def run(self):
        if self.config.get("auto_start", True) and not self.is_auto_start_enabled():
            self.set_auto_start(True)

        # 1. Start Network Worker Thread
        if self.config.get("mode", "websocket") == "websocket" and websockets:
            t = threading.Thread(target=self.run_websocket_loop, daemon=True)
        else:
            t = threading.Thread(target=self.run_firebase_loop, daemon=True)
        t.start()

        # 2. Setup Native Win32 Message Window
        WNDPROCTYPE = ctypes.WINFUNCTYPE(ctypes.c_long, wintypes.HWND, ctypes.c_uint, wintypes.WPARAM, wintypes.LPARAM)

        def wnd_proc(hwnd, msg, wparam, lparam):
            if msg == WM_CLIPBOARDUPDATE:
                self.on_clipboard_changed()
                return 0
            elif msg == WM_TRAYICON:
                if lparam == WM_RBUTTONUP:
                    self.show_context_menu()
                elif lparam == WM_LBUTTONDBLCLK:
                    if self.last_received_text:
                        self.set_clipboard_text(self.last_received_text)
                return 0
            elif msg == WM_COMMAND:
                cmd_id = wparam & 0xFFFF
                if cmd_id == CMD_PAUSE:
                    self.is_paused = not self.is_paused
                    self.update_tray_tooltip()
                elif cmd_id == CMD_AUTOSTART:
                    self.set_auto_start(not self.is_auto_start_enabled())
                elif cmd_id == CMD_COPY_LAST:
                    if self.last_received_text:
                        self.set_clipboard_text(self.last_received_text)
                elif cmd_id == CMD_SETTINGS:
                    os.system(f'notepad.exe "{CONFIG_FILE}"')
                elif cmd_id == CMD_EXIT:
                    self.stop_event.set()
                    if self.nid:
                        shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(self.nid))
                    user32.DestroyWindow(hwnd)
                    user32.PostQuitMessage(0)
                    os._exit(0)
                return 0
            return user32.DefWindowProcW(hwnd, msg, wparam, lparam)

        wc = wintypes.WNDCLASS()
        wc.lpfnWndProc = WNDPROCTYPE(wnd_proc)
        wc.lpszClassName = "ClipSyncTrayWindowClass"
        wc.hInstance = kernel32.GetModuleHandleW(None)
        user32.RegisterClassW(ctypes.byref(wc))

        self.hwnd = user32.CreateWindowExW(0, wc.lpszClassName, "ClipSync", 0, 0, 0, 0, 0, None, None, wc.hInstance, None)
        user32.AddClipboardFormatListener(self.hwnd)

        # 3. Add Native Tray Icon
        h_icon = user32.LoadIconW(0, 32512) # Default application icon IDI_APPLICATION
        self.nid = NOTIFYICONDATAW()
        self.nid.cbSize = ctypes.sizeof(NOTIFYICONDATAW)
        self.nid.hWnd = self.hwnd
        self.nid.uID = 1
        self.nid.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP
        self.nid.uCallbackMessage = WM_TRAYICON
        self.nid.hIcon = h_icon
        self.nid.szTip = "ClipSync: Starting..."
        shell32.Shell_NotifyIconW(NIM_ADD, ctypes.byref(self.nid))

        print("[+] ClipSync Windows Client started in background (System Tray).")

        # 4. Windows Message Loop
        msg = wintypes.MSG()
        while user32.GetMessageW(ctypes.byref(msg), 0, 0, 0) > 0:
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))

        user32.RemoveClipboardFormatListener(self.hwnd)
        shell32.Shell_NotifyIconW(NIM_DELETE, ctypes.byref(self.nid))

if __name__ == "__main__":
    client = ClipSyncClient()
    client.run()
