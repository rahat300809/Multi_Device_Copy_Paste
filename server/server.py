import os
import sys
import json
import time
import hashlib
import asyncio
from typing import Dict, Set
from aiohttp import web

# Configuration from Environment Variables
PORT = int(os.environ.get("PORT", "8765"))
AUTH_TOKEN = os.environ.get("AUTH_TOKEN", "")  # Optional shared secret token
MAX_HISTORY = int(os.environ.get("MAX_HISTORY", "50"))

# State
connected_clients: Set[web.WebSocketResponse] = set()
client_metadata: Dict[web.WebSocketResponse, dict] = {}
clipboard_history = []
last_clipboard_hash = None

HTML_PAGE = """<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>ClipSync Relay Hub</title>
    <link href="https://fonts.googleapis.com/css2?family=Plus+Jakarta+Sans:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet">
    <style>
        :root {
            --bg-base: #0a0e17;
            --bg-card: rgba(23, 32, 54, 0.7);
            --border-card: rgba(255, 255, 255, 0.08);
            --primary: #3b82f6;
            --primary-glow: rgba(59, 130, 246, 0.3);
            --accent: #10b981;
            --text-main: #f1f5f9;
            --text-muted: #94a3b8;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: 'Plus Jakarta Sans', sans-serif;
            background: radial-gradient(circle at 50% 0%, #1e293b 0%, #0a0e17 75%);
            color: var(--text-main);
            min-height: 100vh;
            padding: 24px;
            display: flex;
            flex-direction: column;
            align-items: center;
        }
        .container {
            width: 100%;
            max-width: 860px;
        }
        header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 24px;
            padding-bottom: 16px;
            border-bottom: 1px solid var(--border-card);
        }
        .logo-group {
            display: flex;
            align-items: center;
            gap: 12px;
        }
        .logo-icon {
            width: 40px;
            height: 40px;
            background: linear-gradient(135deg, #3b82f6, #8b5cf6);
            border-radius: 12px;
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 20px;
            box-shadow: 0 4px 14px var(--primary-glow);
        }
        h1 { font-size: 22px; font-weight: 700; }
        .status-badge {
            display: inline-flex;
            align-items: center;
            gap: 8px;
            padding: 6px 14px;
            border-radius: 9999px;
            font-size: 13px;
            font-weight: 600;
            background: rgba(16, 185, 129, 0.12);
            color: #34d399;
            border: 1px solid rgba(16, 185, 129, 0.25);
        }
        .status-dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background: #10b981;
            box-shadow: 0 0 10px #10b981;
            animation: pulse 2s infinite;
        }
        @keyframes pulse {
            0%, 100% { opacity: 1; transform: scale(1); }
            50% { opacity: 0.5; transform: scale(0.85); }
        }
        .grid-2 {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 16px;
            margin-bottom: 24px;
        }
        @media (max-width: 640px) {
            .grid-2 { grid-template-columns: 1fr; }
        }
        .card {
            background: var(--bg-card);
            border: 1px solid var(--border-card);
            border-radius: 16px;
            padding: 20px;
            backdrop-filter: blur(12px);
        }
        .card h2 {
            font-size: 15px;
            font-weight: 600;
            color: var(--text-muted);
            margin-bottom: 14px;
            text-transform: uppercase;
            letter-spacing: 0.5px;
            display: flex;
            align-items: center;
            justify-content: space-between;
        }
        .stat-value {
            font-size: 26px;
            font-weight: 700;
            color: #fff;
        }
        .send-box {
            display: flex;
            flex-direction: column;
            gap: 12px;
            margin-bottom: 24px;
        }
        textarea {
            width: 100%;
            height: 90px;
            background: rgba(15, 23, 42, 0.8);
            border: 1px solid var(--border-card);
            border-radius: 12px;
            padding: 14px;
            color: #fff;
            font-family: inherit;
            font-size: 14px;
            resize: vertical;
            outline: none;
            transition: border-color 0.2s;
        }
        textarea:focus {
            border-color: var(--primary);
            box-shadow: 0 0 0 2px var(--primary-glow);
        }
        .btn-row {
            display: flex;
            justify-content: flex-end;
            gap: 10px;
        }
        button {
            padding: 10px 20px;
            border-radius: 10px;
            border: none;
            background: linear-gradient(135deg, #3b82f6, #2563eb);
            color: white;
            font-weight: 600;
            font-size: 14px;
            cursor: pointer;
            transition: all 0.2s;
            display: inline-flex;
            align-items: center;
            gap: 8px;
        }
        button:hover {
            transform: translateY(-1px);
            box-shadow: 0 4px 12px var(--primary-glow);
        }
        .feed-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 16px;
        }
        .feed-title {
            font-size: 16px;
            font-weight: 600;
        }
        .history-list {
            display: flex;
            flex-direction: column;
            gap: 12px;
        }
        .history-item {
            background: var(--bg-card);
            border: 1px solid var(--border-card);
            border-radius: 14px;
            padding: 16px;
            transition: border-color 0.2s;
            position: relative;
        }
        .history-item:hover {
            border-color: rgba(59, 130, 246, 0.4);
        }
        .history-top {
            display: flex;
            justify-content: space-between;
            font-size: 12px;
            color: var(--text-muted);
            margin-bottom: 10px;
        }
        .device-tag {
            background: rgba(59, 130, 246, 0.15);
            color: #60a5fa;
            padding: 2px 8px;
            border-radius: 6px;
            font-weight: 600;
        }
        .history-content {
            font-family: 'JetBrains Mono', monospace;
            font-size: 13px;
            line-height: 1.5;
            white-space: pre-wrap;
            word-break: break-all;
            background: rgba(10, 14, 23, 0.5);
            padding: 12px;
            border-radius: 8px;
            max-height: 180px;
            overflow-y: auto;
        }
        .copy-btn {
            position: absolute;
            top: 14px;
            right: 14px;
            padding: 4px 10px;
            font-size: 12px;
            border-radius: 6px;
            background: rgba(255, 255, 255, 0.1);
        }
        .copy-btn:hover {
            background: rgba(255, 255, 255, 0.2);
            transform: none;
            box-shadow: none;
        }
    </style>
</head>
<body>
    <div class="container">
        <header>
            <div class="logo-group">
                <div class="logo-icon">📋</div>
                <div>
                    <h1>ClipSync Server</h1>
                    <p style="font-size: 12px; color: var(--text-muted);">Real-time Multi-Device Clipboard Relay</p>
                </div>
            </div>
            <div class="status-badge">
                <span class="status-dot"></span>
                <span id="conn-text">Active Relay</span>
            </div>
        </header>

        <div class="grid-2">
            <div class="card">
                <h2>Connected Devices <span id="client-count" class="stat-value" style="font-size: 16px;">0</span></h2>
                <div id="device-list" style="font-size: 13px; color: var(--text-muted); line-height: 1.6;">
                    No devices connected yet
                </div>
            </div>
            <div class="card">
                <h2>Server Info</h2>
                <div style="font-size: 13px; color: var(--text-muted); line-height: 1.8;">
                    <div>Port: <strong>8765</strong> (WebSocket / HTTP)</div>
                    <div>Protocol: <strong>JSON over WebSocket</strong></div>
                    <div>Endpoint: <code style="color: #60a5fa;">ws://&lt;server-ip&gt;:8765/ws</code></div>
                </div>
            </div>
        </div>

        <div class="card send-box">
            <h2>Broadcast to All Devices</h2>
            <textarea id="manual-text" placeholder="Type or paste anything here to immediately push to Windows PC & Android Phone..."></textarea>
            <div class="btn-row">
                <button onclick="sendBroadcast()">
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><line x1="22" y1="2" x2="11" y2="13"></line><polygon points="22 2 15 22 11 13 2 9 22 2"></polygon></svg>
                    Send to All Clipboards
                </button>
            </div>
        </div>

        <div class="feed-header">
            <div class="feed-title">Real-Time Clipboard Stream</div>
            <span id="history-count" style="font-size: 13px; color: var(--text-muted);">0 items</span>
        </div>

        <div id="history-container" class="history-list"></div>
    </div>

    <script>
        let ws;
        const historyContainer = document.getElementById('history-container');
        const deviceList = document.getElementById('device-list');
        const clientCount = document.getElementById('client-count');
        const historyCount = document.getElementById('history-count');

        function connectWS() {
            const loc = window.location;
            const wsUri = (loc.protocol === "https:" ? "wss:" : "ws:") + "//" + loc.host + "/ws?device_name=WebDashboard&device_type=web";
            ws = new WebSocket(wsUri);

            ws.onopen = () => {
                document.getElementById('conn-text').textContent = "Relay Connected";
            };

            ws.onclose = () => {
                document.getElementById('conn-text').textContent = "Disconnected (Reconnecting...)";
                setTimeout(connectWS, 2000);
            };

            ws.onmessage = (event) => {
                try {
                    const msg = JSON.parse(event.data);
                    if (msg.type === "clipboard") {
                        addHistoryItem(msg);
                    } else if (msg.type === "initial_state") {
                        renderInitialState(msg);
                    } else if (msg.type === "client_update") {
                        updateClients(msg.clients);
                    }
                } catch (e) {
                    console.error("WS parse error", e);
                }
            };
        }

        function renderInitialState(state) {
            historyContainer.innerHTML = '';
            if (state.history && state.history.length > 0) {
                state.history.forEach(item => addHistoryItem(item, false));
                historyCount.textContent = `${state.history.length} items`;
            }
            if (state.clients) {
                updateClients(state.clients);
            }
        }

        function updateClients(clients) {
            clientCount.textContent = clients.length;
            if (clients.length === 0) {
                deviceList.innerHTML = 'No devices connected yet';
                return;
            }
            deviceList.innerHTML = clients.map(c => 
                `<div>• <strong>${c.device_name || 'Client'}</strong> <span style="font-size: 11px; opacity: 0.7;">(${c.device_type || 'Unknown'} - ${c.ip})</span></div>`
            ).join('');
        }

        function addHistoryItem(item, prepend = true) {
            const date = new Date(item.timestamp * 1000).toLocaleTimeString();
            const div = document.createElement('div');
            div.className = 'history-item';
            div.innerHTML = `
                <div class="history-top">
                    <div>From: <span class="device-tag">${item.device_name || 'Unknown'}</span></div>
                    <div>${date}</div>
                </div>
                <button class="copy-btn" onclick="copyText(this)">Copy</button>
                <div class="history-content"></div>
            `;
            div.querySelector('.history-content').textContent = item.data;
            if (prepend && historyContainer.firstChild) {
                historyContainer.insertBefore(div, historyContainer.firstChild);
            } else {
                historyContainer.appendChild(div);
            }
        }

        function copyText(btn) {
            const content = btn.parentElement.querySelector('.history-content').textContent;
            navigator.clipboard.writeText(content).then(() => {
                btn.textContent = "Copied!";
                setTimeout(() => btn.textContent = "Copy", 1500);
            });
        }

        function sendBroadcast() {
            const text = document.getElementById('manual-text').value;
            if (!text || !ws || ws.readyState !== WebSocket.OPEN) return;
            const payload = {
                type: "clipboard",
                device_name: "Web Dashboard",
                device_type: "web",
                data: text,
                timestamp: Math.floor(Date.now() / 1000)
            };
            ws.send(JSON.stringify(payload));
            document.getElementById('manual-text').value = '';
        }

        window.onload = connectWS;
    </script>
</body>
</html>
"""

async def index_handler(request: web.Request) -> web.Response:
    return web.Response(text=HTML_PAGE, content_type="text/html")

async def health_handler(request: web.Request) -> web.Response:
    return web.json_response({
        "status": "ok",
        "clients_connected": len(connected_clients),
        "history_count": len(clipboard_history)
    })

def get_client_list():
    return [
        {
            "device_name": meta.get("device_name", "Unknown"),
            "device_type": meta.get("device_type", "unknown"),
            "ip": meta.get("ip", "unknown")
        }
        for meta in client_metadata.values()
    ]

async def broadcast_client_update():
    if not connected_clients:
        return
    msg = json.dumps({"type": "client_update", "clients": get_client_list()})
    for ws in list(connected_clients):
        try:
            await ws.send_str(msg)
        except Exception:
            pass

async def websocket_handler(request: web.Request) -> web.WebSocketResponse:
    # Optional token auth check
    if AUTH_TOKEN:
        token = request.query.get("token") or request.headers.get("Authorization", "").replace("Bearer ", "")
        if token != AUTH_TOKEN:
            return web.Response(status=401, text="Unauthorized: Invalid token")

    ws = web.WebSocketResponse(heartbeat=None, autoping=True)
    await ws.prepare(request)

    peername = request.transport.get_extra_info('peername')
    client_ip = peername[0] if peername else "Unknown"
    device_name = request.query.get("device_name", f"Device-{len(connected_clients)+1}")
    device_type = request.query.get("device_type", "client")

    connected_clients.add(ws)
    client_metadata[ws] = {
        "device_name": device_name,
        "device_type": device_type,
        "ip": client_ip,
        "connected_at": time.time()
    }

    print(f"[+] Client connected: {device_name} ({device_type}) from {client_ip}. Total active: {len(connected_clients)}")

    # Send initial state (history & clients)
    try:
        await ws.send_str(json.dumps({
            "type": "initial_state",
            "history": clipboard_history[-20:],
            "clients": get_client_list()
        }))
    except Exception as e:
        print(f"Error sending initial state: {e}")

    await broadcast_client_update()

    global last_clipboard_hash
    try:
        async for msg in ws:
            if msg.type == web.WSMsgType.TEXT:
                try:
                    payload = json.loads(msg.data)
                    msg_type = payload.get("type")

                    if msg_type == "clipboard":
                        text_data = payload.get("data", "")
                        if not text_data:
                            continue

                        # Deduplicate using SHA256 of text
                        content_hash = hashlib.sha256(text_data.encode("utf-8")).hexdigest()
                        if content_hash == last_clipboard_hash:
                            continue

                        last_clipboard_hash = content_hash
                        sender_name = payload.get("device_name", client_metadata[ws]["device_name"])
                        sender_type = payload.get("device_type", client_metadata[ws]["device_type"])

                        entry = {
                            "type": "clipboard",
                            "data": text_data,
                            "hash": content_hash,
                            "device_name": sender_name,
                            "device_type": sender_type,
                            "timestamp": payload.get("timestamp", int(time.time()))
                        }

                        clipboard_history.append(entry)
                        if len(clipboard_history) > MAX_HISTORY:
                            clipboard_history.pop(0)

                        broadcast_json = json.dumps(entry)
                        print(f"📋 New clipboard from {sender_name}: {text_data[:40]}... (Broadcasting to {len(connected_clients)-1} other devices)")

                        # Broadcast to all OTHER clients
                        for other_ws in list(connected_clients):
                            if other_ws != ws:
                                try:
                                    await other_ws.send_str(broadcast_json)
                                except Exception:
                                    pass

                    elif msg_type == "ping":
                        await ws.send_str(json.dumps({"type": "pong"}))

                except json.JSONDecodeError:
                    pass
            elif msg.type == web.WSMsgType.ERROR:
                print(f"WebSocket connection closed with error {ws.exception()}")
            elif msg.type in (web.WSMsgType.CLOSE, web.WSMsgType.CLOSING, web.WSMsgType.CLOSED):
                print(f"WebSocket close msg received: type={msg.type}, data={msg.data}, extra={msg.extra}")
        print(f"DEBUG: async for ws completed. ws.closed={ws.closed}, close_code={ws.close_code}")
    except Exception as e:
        print(f"DEBUG: Exception in ws loop: {e}")
    finally:
        connected_clients.discard(ws)
        client_metadata.pop(ws, None)
        print(f"[-] Client disconnected: {device_name}. Remaining: {len(connected_clients)}")
        await broadcast_client_update()

    return ws

def init_app() -> web.Application:
    app = web.Application()
    app.router.add_get("/", index_handler)
    app.router.add_get("/health", health_handler)
    app.router.add_get("/ws", websocket_handler)
    return app

if __name__ == "__main__":
    app = init_app()
    print(f"🚀 ClipSync Relay Hub starting on port {PORT}...")
    print(f"🌐 Web UI available at: http://0.0.0.0:{PORT}")
    print(f"📡 WebSocket endpoint: ws://0.0.0.0:{PORT}/ws")
    web.run_app(app, host="0.0.0.0", port=PORT)
