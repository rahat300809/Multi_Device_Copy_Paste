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
        setConnectionLostTimeout(10);
    }

    @Override
    public void onOpen(ServerHandshake handshakedata) {
        Log.i(TAG, "WebSocket connected");
        if (service != null) {
            try {
                service.onWebSocketOpen();
                JSONObject req = new JSONObject();
                req.put("type", "pair_req");
                req.put("from_name", service.getPreferences().getDeviceName());
                req.put("from_ip", "android");
                send(req.toString());
            } catch (Throwable t) {
                Log.w(TAG, "onOpen note: " + t.getMessage());
            }
        }
    }

    @Override
    public void onMessage(String message) {
        if (service != null) {
            try {
                JSONObject json = new JSONObject(message);
                String type = json.optString("type");
                if ("pair_resp".equals(type)) {
                    String status = json.optString("status");
                    boolean accepted = "accepted".equalsIgnoreCase(status) || "allow".equalsIgnoreCase(status);
                    service.onPairResponse(accepted);
                } else if ("clipboard".equals(type)) {
                    String data = json.optString("data");
                    if (data != null && !data.isEmpty()) {
                        service.onWebSocketClipboardReceived(data);
                    }
                }
            } catch (Throwable t) {
                Log.w(TAG, "onMessage note: " + t.getMessage());
            }
        }
    }

    @Override
    public void onClose(int code, String reason, boolean remote) {
        Log.w(TAG, "WebSocket closed: " + reason);
        if (service != null) {
            try {
                service.onWebSocketClosed();
            } catch (Throwable t) {
                Log.w(TAG, "onClose note: " + t.getMessage());
            }
        }
    }

    @Override
    public void onError(Exception ex) {
        Log.w(TAG, "WebSocket error: " + ex);
    }
}
