package com.clipsync.app;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

import org.json.JSONObject;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URI;
import java.net.URL;
import java.security.MessageDigest;

public class ClipboardSyncService extends Service implements ClipboardManager.OnPrimaryClipChangedListener {
    private static final String TAG = "ClipboardSyncService";
    private static final String CHANNEL_ID = "clipsync_foreground_channel";
    private static final int NOTIF_ID = 1001;

    public static final String ACTION_BROADCAST_COPY = "com.clipsync.app.ACTION_BROADCAST_COPY";
    public static final String EXTRA_CLIPBOARD_TEXT = "extra_clipboard_text";
    public static final String ACTION_STATUS_UPDATE = "com.clipsync.app.ACTION_STATUS_UPDATE";
    public static final String EXTRA_STATUS = "extra_status";

    private static ClipboardSyncService sInstance;
    private AppPreferences preferences;
    private ClipboardManager clipboardManager;
    private SyncWebSocketClient webSocketClient;
    private Handler mainHandler;
    private Thread firebaseThread;

    private String lastContentHash = "";
    private boolean isDestroyed = false;

    public static synchronized ClipboardSyncService getInstance() {
        return sInstance;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        sInstance = this;
        preferences = new AppPreferences(this);
        clipboardManager = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        mainHandler = new Handler(Looper.getMainLooper());

        createNotificationChannel();
        startForeground(NOTIF_ID, buildNotification("Starting ClipSync..."));

        if (clipboardManager != null) {
            clipboardManager.addPrimaryClipChangedListener(this);
        }

        connectSync();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_BROADCAST_COPY.equals(intent.getAction())) {
            String text = intent.getStringExtra(EXTRA_CLIPBOARD_TEXT);
            if (text != null && !text.isEmpty()) {
                sendToRemote(text);
            }
        }
        return START_STICKY;
    }

    @Override
    public void onPrimaryClipChanged() {
        if (clipboardManager == null || !clipboardManager.hasPrimaryClip()) return;
        ClipData clip = clipboardManager.getPrimaryClip();
        if (clip != null && clip.getItemCount() > 0) {
            CharSequence text = clip.getItemAt(0).getText();
            if (text != null && text.length() > 0) {
                String str = text.toString();
                String hash = sha256(str);
                if (!hash.equals(lastContentHash)) {
                    sendToRemote(str);
                }
            }
        }
    }

    public synchronized void connectSync() {
        disconnectSync();
        String mode = preferences.getMode();

        if ("websocket".equalsIgnoreCase(mode)) {
            connectWebSocket();
        } else {
            connectFirebase();
        }
    }

    private void connectWebSocket() {
        try {
            String baseUrl = preferences.getServerUrl();
            String deviceName = preferences.getDeviceName();
            String fullUriStr = baseUrl + (baseUrl.contains("?") ? "&" : "?") +
                    "device_name=" + java.net.URLEncoder.encode(deviceName, "UTF-8") +
                    "&device_type=android";

            URI uri = new URI(fullUriStr);
            updateNotification("Connecting to " + baseUrl + "...");

            SyncWebSocketClient client = new SyncWebSocketClient(uri, this);
            webSocketClient = client;
            client.connect();
        } catch (Exception e) {
            scheduleReconnect();
        }
    }

    public void onWebSocketOpen() {
        updateNotification("Connected to " + preferences.getServerUrl());
        broadcastStatus("Connected (Docker / Relay)");
    }

    public void onWebSocketClipboardReceived(String data) {
        setLocalClipboard(data);
    }

    public void onWebSocketClosed() {
        updateNotification("Disconnected. Retrying...");
        broadcastStatus("Disconnected (Retrying...)");
        scheduleReconnect();
    }

    private void connectFirebase() {
        firebaseThread = new Thread(new FirebaseStreamRunnable(this));
        firebaseThread.start();
    }

    private static class FirebaseStreamRunnable implements Runnable {
        private final ClipboardSyncService service;
        public FirebaseStreamRunnable(ClipboardSyncService service) {
            this.service = service;
        }
        @Override
        public void run() {
            while (!service.isDestroyed && "firebase".equalsIgnoreCase(service.preferences.getMode())) {
                try {
                    String urlStr = service.preferences.getServerUrl();
                    if (!urlStr.endsWith(".json")) {
                        urlStr = urlStr.endsWith("/") ? urlStr + "clipboard.json" : urlStr + "/clipboard.json";
                    }
                    URL url = new URL(urlStr);
                    HttpURLConnection conn = (HttpURLConnection) url.openConnection();
                    conn.setRequestProperty("Accept", "text/event-stream");
                    conn.setReadTimeout(0);

                    if (conn.getResponseCode() == 200) {
                        service.broadcastStatus("Connected (Firebase)");
                        BufferedReader reader = new BufferedReader(new InputStreamReader(conn.getInputStream()));
                        String line;
                        while (!service.isDestroyed && (line = reader.readLine()) != null) {
                            if (line.startsWith("data: ")) {
                                String raw = line.substring(6).trim();
                                if (!raw.isEmpty() && !raw.equals("null")) {
                                    try {
                                        JSONObject root = new JSONObject(raw);
                                        JSONObject payload = root.has("data") && root.get("data") instanceof JSONObject
                                                ? root.getJSONObject("data") : root;
                                        String text = payload.optString("data");
                                        String sender = payload.optString("device_name");
                                        if (text != null && !text.isEmpty() && !service.preferences.getDeviceName().equals(sender)) {
                                            service.setLocalClipboard(text);
                                        }
                                    } catch (Exception ignored) {}
                                }
                            }
                        }
                    }
                } catch (Exception e) {
                    try { Thread.sleep(4000); } catch (InterruptedException ignored) {}
                }
            }
        }
    }

    private void scheduleReconnect() {
        if (isDestroyed) return;
        mainHandler.postDelayed(new ReconnectRunnable(this), 3500);
    }

    private static class ReconnectRunnable implements Runnable {
        private final ClipboardSyncService service;
        public ReconnectRunnable(ClipboardSyncService service) {
            this.service = service;
        }
        @Override
        public void run() {
            if (!service.isDestroyed) {
                service.connectSync();
            }
        }
    }

    public synchronized void sendToRemote(String text) {
        if (text == null || text.isEmpty()) return;
        lastContentHash = sha256(text);

        String mode = preferences.getMode();
        if ("websocket".equalsIgnoreCase(mode)) {
            if (webSocketClient != null && webSocketClient.isOpen()) {
                try {
                    JSONObject obj = new JSONObject();
                    obj.put("type", "clipboard");
                    obj.put("device_name", preferences.getDeviceName());
                    obj.put("device_type", "android");
                    obj.put("data", text);
                    obj.put("timestamp", System.currentTimeMillis() / 1000);
                    webSocketClient.send(obj.toString());
                } catch (Exception ignored) {}
            }
        } else {
            new Thread(new FirebasePushRunnable(preferences.getServerUrl(), preferences.getDeviceName(), text, lastContentHash)).start();
        }
    }

    private static class FirebasePushRunnable implements Runnable {
        private final String serverUrl;
        private final String deviceName;
        private final String text;
        private final String hash;

        public FirebasePushRunnable(String serverUrl, String deviceName, String text, String hash) {
            this.serverUrl = serverUrl;
            this.deviceName = deviceName;
            this.text = text;
            this.hash = hash;
        }

        @Override
        public void run() {
            try {
                String urlStr = serverUrl;
                if (!urlStr.endsWith(".json")) {
                    urlStr = urlStr.endsWith("/") ? urlStr + "clipboard.json" : urlStr + "/clipboard.json";
                }
                URL url = new URL(urlStr);
                HttpURLConnection conn = (HttpURLConnection) url.openConnection();
                conn.setRequestMethod("PUT");
                conn.setRequestProperty("Content-Type", "application/json");
                conn.setDoOutput(true);

                JSONObject obj = new JSONObject();
                obj.put("data", text);
                obj.put("device_name", deviceName);
                obj.put("device_type", "android");
                obj.put("timestamp", System.currentTimeMillis() / 1000);
                obj.put("hash", hash);

                try (OutputStream os = conn.getOutputStream()) {
                    os.write(obj.toString().getBytes("UTF-8"));
                }
                conn.getResponseCode();
                conn.disconnect();
            } catch (Exception ignored) {}
        }
    }

    private void setLocalClipboard(String text) {
        lastContentHash = sha256(text);
        mainHandler.post(new SetClipboardRunnable(this, text));
    }

    private static class SetClipboardRunnable implements Runnable {
        private final ClipboardSyncService service;
        private final String text;

        public SetClipboardRunnable(ClipboardSyncService service, String text) {
            this.service = service;
            this.text = text;
        }

        @Override
        public void run() {
            try {
                if (service.clipboardManager != null) {
                    ClipData clip = ClipData.newPlainText("ClipSync", text);
                    service.clipboardManager.setPrimaryClip(clip);
                    service.broadcastStatus("Received: " + (text.length() > 25 ? text.substring(0, 25) + "..." : text));
                }
            } catch (Exception ignored) {}
        }
    }

    private void broadcastStatus(String status) {
        Intent intent = new Intent(ACTION_STATUS_UPDATE);
        intent.putExtra(EXTRA_STATUS, status);
        sendBroadcast(intent);
    }

    public synchronized void disconnectSync() {
        if (webSocketClient != null) {
            try { webSocketClient.close(); } catch (Exception ignored) {}
            webSocketClient = null;
        }
        if (firebaseThread != null) {
            firebaseThread.interrupt();
            firebaseThread = null;
        }
    }

    private void createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "ClipSync Background Service",
                    NotificationManager.IMPORTANCE_LOW
            );
            channel.setDescription("Maintains clipboard synchronization in background");
            NotificationManager manager = getSystemService(NotificationManager.class);
            if (manager != null) {
                manager.createNotificationChannel(channel);
            }
        }
    }

    private Notification buildNotification(String text) {
        Intent notificationIntent = new Intent(this, MainActivity.class);
        PendingIntent pendingIntent = PendingIntent.getActivity(
                this, 0, notificationIntent,
                Build.VERSION.SDK_INT >= 23 ? PendingIntent.FLAG_IMMUTABLE : 0
        );

        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            builder = new Notification.Builder(this, CHANNEL_ID);
        } else {
            builder = new Notification.Builder(this);
        }

        return builder.setContentTitle("ClipSync Active")
                .setContentText(text)
                .setSmallIcon(R.drawable.ic_clipboard)
                .setContentIntent(pendingIntent)
                .setOngoing(true)
                .build();
    }

    private void updateNotification(String text) {
        NotificationManager manager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        if (manager != null) {
            manager.notify(NOTIF_ID, buildNotification(text));
        }
    }

    private String sha256(String base) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            byte[] hash = digest.digest(base.getBytes("UTF-8"));
            StringBuilder hexString = new StringBuilder();
            for (byte b : hash) {
                String hex = Integer.toHexString(0xff & b);
                if (hex.length() == 1) hexString.append('0');
                hexString.append(hex);
            }
            return hexString.toString();
        } catch (Exception ex) {
            return String.valueOf(base.hashCode());
        }
    }

    @Override
    public void onDestroy() {
        isDestroyed = true;
        disconnectSync();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
