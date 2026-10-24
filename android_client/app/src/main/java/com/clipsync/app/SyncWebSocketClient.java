package com.clipsync.app;

import android.util.Log;
import org.java_websocket.client.WebSocketClient;
import org.java_websocket.handshake.ServerHandshake;
import org.json.JSONObject;
import java.net.URI;

public class SyncWebSocketClient extends WebSocketClient {
    private static final String TAG = "SyncWebSocketClient";
    private final ClipboardSyncService service;

    public SyncWebSocketClient(URI serverUri, ClipboardSyncService service) {
        super(serverUri);
        this.service = service;
    }

    @Override
    public void onOpen(ServerHandshake handshakedata) {
        Log.i(TAG, "WebSocket connected");
        if (service != null) {
            service.onWebSocketOpen();
        }
    }

    @Override
    public void onMessage(String message) {
        if (service != null) {
            try {
                JSONObject json = new JSONObject(message);
                if ("clipboard".equals(json.optString("type"))) {
                    String data = json.optString("data");
                    if (data != null && !data.isEmpty()) {
                        service.onWebSocketClipboardReceived(data);
                    }
                }
            } catch (Exception ignored) {}
        }
    }

    @Override
    public void onClose(int code, String reason, boolean remote) {
        Log.w(TAG, "WebSocket closed: " + reason);
        if (service != null) {
            service.onWebSocketClosed();
        }
    }

    @Override
    public void onError(Exception ex) {
        Log.e(TAG, "WebSocket error: " + ex);
    }
}
