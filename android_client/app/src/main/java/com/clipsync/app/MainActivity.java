package com.clipsync.app;

import android.app.Activity;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.util.HashSet;
import java.util.Set;

public class MainActivity extends Activity implements View.OnClickListener, android.content.DialogInterface.OnClickListener {
    private AppPreferences preferences;
    private Switch switchService;
    private RadioGroup rgMode;
    private RadioButton rbWebSocket;
    private RadioButton rbFirebase;
    private EditText etServerUrl;
    private EditText etDeviceName;
    private TextView tvConnectionStatus;
    private TextView tvLatestClipboard;

    private TextView tvScanStatus;
    private LinearLayout containerDevices;
    private Button btnScanDevices;
    private EditText etDirectIp;
    private Button btnDirectConnect;

    private BroadcastReceiver statusReceiver;
    private Handler mainHandler;
    private final Set<String> discoveredKeys = new HashSet<String>();

    private static class SafeCrashHandler implements Thread.UncaughtExceptionHandler {
        @Override
        public void uncaughtException(Thread t, Throwable e) {
            Log.e("ClipSyncCrash", "Uncaught exception in " + (t != null ? t.getName() : "unknown"), e);
        }
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Thread.setDefaultUncaughtExceptionHandler(new SafeCrashHandler());

        setContentView(R.layout.activity_main);

        mainHandler = new Handler(Looper.getMainLooper());
        preferences = new AppPreferences(this);
        initViews();

        // Android 13+ Notification permission first
        if (Build.VERSION.SDK_INT >= 33) {
            try {
                if (checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED) {
                    requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS}, 1001);
                }
            } catch (Throwable ignored) {}
        }

        loadSavedSettings();

        try {
            statusReceiver = new StatusReceiver(this);
            IntentFilter filter = new IntentFilter();
            filter.addAction(ClipboardSyncService.ACTION_STATUS_UPDATE);
            filter.addAction(ClipboardSyncService.ACTION_DEVICE_DISCOVERED);
            filter.addAction(ClipboardSyncService.ACTION_PAIR_REQUEST);
            filter.addAction(ClipboardSyncService.ACTION_PAIR_RESPONSE);

            if (Build.VERSION.SDK_INT >= 33) {
                registerReceiver(statusReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
            } else {
                registerReceiver(statusReceiver, filter);
            }
        } catch (Throwable t) {
            Log.e("MainActivity", "Error registering receiver", t);
        }
    }

    @Override
    public void onClick(android.content.DialogInterface dialog, int which) {}

    @Override
    protected void onResume() {
        super.onResume();
        loadCachedDiscoveredDevices();
    }

    private void loadCachedDiscoveredDevices() {
        ClipboardSyncService service = ClipboardSyncService.getInstance();
        if (service != null) {
            for (ClipboardSyncService.DiscoveredDeviceItem item : service.getDiscoveredDevices()) {
                onDeviceDiscovered(item.name, item.type, item.ip, item.port);
            }
        }
    }

    private void initViews() {
        switchService = (Switch) findViewById(R.id.switchService);
        rgMode = (RadioGroup) findViewById(R.id.rgMode);
        rbWebSocket = (RadioButton) findViewById(R.id.rbWebSocket);
        rbFirebase = (RadioButton) findViewById(R.id.rbFirebase);
        etServerUrl = (EditText) findViewById(R.id.etServerUrl);
        etDeviceName = (EditText) findViewById(R.id.etDeviceName);
        tvConnectionStatus = (TextView) findViewById(R.id.tvConnectionStatus);
        tvLatestClipboard = (TextView) findViewById(R.id.tvLatestClipboard);

        tvScanStatus = (TextView) findViewById(R.id.tvScanStatus);
        containerDevices = (LinearLayout) findViewById(R.id.containerDevices);
        btnScanDevices = (Button) findViewById(R.id.btnScanDevices);
        etDirectIp = (EditText) findViewById(R.id.etDirectIp);
        btnDirectConnect = (Button) findViewById(R.id.btnDirectConnect);

        Button btnSaveConfig = (Button) findViewById(R.id.btnSaveConfig);
        Button btnBattery = (Button) findViewById(R.id.btnBattery);
        Button btnTestPush = (Button) findViewById(R.id.btnTestPush);

        switchService.setOnClickListener(this);
        btnSaveConfig.setOnClickListener(this);
        btnBattery.setOnClickListener(this);
        btnTestPush.setOnClickListener(this);
        if (btnScanDevices != null) btnScanDevices.setOnClickListener(this);
        if (btnDirectConnect != null) btnDirectConnect.setOnClickListener(this);
    }

    @Override
    public void onClick(View v) {
        int id = v.getId();
        if (id == R.id.switchService) {
            boolean isChecked = switchService.isChecked();
            preferences.setServiceActive(isChecked);
            if (isChecked) {
                startSyncService();
            } else {
                stopSyncService();
            }
        } else if (id == R.id.btnSaveConfig) {
            saveCurrentSettings();
            Toast.makeText(this, "Settings saved. Reconnecting...", Toast.LENGTH_SHORT).show();
            if (switchService.isChecked()) {
                startSyncService();
            }
        } else if (id == R.id.btnBattery) {
            requestBatteryOptimizationExemption();
        } else if (id == R.id.btnDirectConnect) {
            String ip = etDirectIp != null ? etDirectIp.getText().toString().trim() : "";
            if (ip.isEmpty()) {
                Toast.makeText(this, "Please enter PC IP address (e.g. 192.168.0.108)", Toast.LENGTH_SHORT).show();
            } else {
                String targetWs = "ws://" + ip + ":8765/ws";
                preferences.setServerUrl(targetWs);
                preferences.setPairedDeviceName("PC (" + ip + ")");
                etServerUrl.setText(targetWs);
                saveCurrentSettings();
                startSyncService();
                Toast.makeText(this, "Connecting directly to " + ip + "...", Toast.LENGTH_SHORT).show();
            }
        } else if (id == R.id.btnScanDevices) {
            discoveredKeys.clear();
            if (containerDevices != null) containerDevices.removeAllViews();
            if (tvScanStatus != null) tvScanStatus.setText("Scanning Wi-Fi for laptops and other devices...");
            ClipboardSyncService service = ClipboardSyncService.getInstance();
            if (service != null) service.sendDiscoveryBroadcast();
        } else if (id == R.id.btnTestPush) {
            ClipboardSyncService service = ClipboardSyncService.getInstance();
            if (service != null) {
                String testMsg = "Hello from Android (" + preferences.getDeviceName() + ") at " + new java.util.Date();
                service.sendToRemote(testMsg);
                tvLatestClipboard.setText(testMsg);
                Toast.makeText(this, "Sent test message to PC!", Toast.LENGTH_SHORT).show();
            } else {
                Toast.makeText(this, "Service not running. Please turn ON switch above.", Toast.LENGTH_SHORT).show();
            }
        }
    }

    public void onDeviceDiscovered(String name, String type, String ip, String port) {
        mainHandler.post(new AddDeviceViewRunnable(this, name, type, ip, port));
    }

    public void onPairRequestReceived(String name, String ip, String port) {
        mainHandler.post(new ShowPairReqDialogRunnable(this, name, ip, port));
    }

    public void onPairResponseReceived(String status, String name) {
        mainHandler.post(new ShowPairRespToastRunnable(this, status, name));
    }

    public void sendUdpResponse(String targetIp, String status) {
        new Thread(new SendUdpRespRunnable(targetIp, status, preferences.getDeviceName())).start();
    }

    private static class SendUdpRespRunnable implements Runnable {
        private final String targetIp;
        private final String status;
        private final String myName;

        public SendUdpRespRunnable(String targetIp, String status, String myName) {
            this.targetIp = targetIp;
            this.status = status;
            this.myName = myName;
        }

        @Override
        public void run() {
            try (DatagramSocket s = new DatagramSocket()) {
                s.setBroadcast(true);
                String msg = "CLIPSYNC_PAIR_RESP:" + status + ":" + myName;
                byte[] data = msg.getBytes("UTF-8");
                DatagramPacket packet = new DatagramPacket(data, data.length, InetAddress.getByName(targetIp), 8766);
                s.send(packet);
            } catch (Exception ignored) {}
        }
    }

    private static class ShowPairReqDialogRunnable implements Runnable {
        private final MainActivity act;
        private final String fromName;
        private final String fromIp;
        private final String fromPort;

        public ShowPairReqDialogRunnable(MainActivity act, String fromName, String fromIp, String fromPort) {
            this.act = act;
            this.fromName = fromName;
            this.fromIp = fromIp;
            this.fromPort = fromPort;
        }

        @Override
        public void run() {
            if (act.isFinishing()) return;
            new android.app.AlertDialog.Builder(act)
                    .setTitle("ClipSync Connection Request")
                    .setMessage("💻 Device: " + fromName + " (" + fromIp + ")\n\nwants to connect and sync clipboard with this phone.\n\nDo you want to ALLOW this device?")
                    .setPositiveButton("ALLOW", new AllowPairClickListener(act, fromName, fromIp, fromPort))
                    .setNegativeButton("DENY", new DenyPairClickListener(act, fromName, fromIp))
                    .setCancelable(false)
                    .show();
        }
    }

    private static class AllowPairClickListener implements android.content.DialogInterface.OnClickListener {
        private final MainActivity act;
        private final String fromName;
        private final String fromIp;
        private final String fromPort;

        public AllowPairClickListener(MainActivity act, String fromName, String fromIp, String fromPort) {
            this.act = act;
            this.fromName = fromName;
            this.fromIp = fromIp;
            this.fromPort = fromPort;
        }

        @Override
        public void onClick(android.content.DialogInterface dialog, int which) {
            String targetWs = "ws://" + fromIp + ":" + fromPort + "/ws";
            act.etServerUrl.setText(targetWs);
            act.saveCurrentSettings();
            act.startSyncService();
            act.sendUdpResponse(fromIp, "allow");
            Toast.makeText(act, "✅ Connected to " + fromName + "! Clipboard sync active.", Toast.LENGTH_SHORT).show();
        }
    }

    private static class DenyPairClickListener implements android.content.DialogInterface.OnClickListener {
        private final MainActivity act;
        private final String fromName;
        private final String fromIp;

        public DenyPairClickListener(MainActivity act, String fromName, String fromIp) {
            this.act = act;
            this.fromName = fromName;
            this.fromIp = fromIp;
        }

        @Override
        public void onClick(android.content.DialogInterface dialog, int which) {
            act.sendUdpResponse(fromIp, "deny");
            Toast.makeText(act, "Declined connection from " + fromName, Toast.LENGTH_SHORT).show();
        }
    }

    private static class ShowPairRespToastRunnable implements Runnable {
        private final MainActivity act;
        private final String status;
        private final String fromName;

        public ShowPairRespToastRunnable(MainActivity act, String status, String fromName) {
            this.act = act;
            this.status = status;
            this.fromName = fromName;
        }

        @Override
        public void run() {
            if ("allow".equalsIgnoreCase(status) || "accepted".equalsIgnoreCase(status)) {
                Toast.makeText(act, "✅ " + fromName + " ALLOWED connection! Clipboard sync active.", Toast.LENGTH_LONG).show();
            } else {
                Toast.makeText(act, "❌ " + fromName + " DECLINED connection.", Toast.LENGTH_LONG).show();
            }
        }
    }

    private static class AddDeviceViewRunnable implements Runnable {
        private final MainActivity act;
        private final String name;
        private final String type;
        private final String ip;
        private final String port;

        public AddDeviceViewRunnable(MainActivity act, String name, String type, String ip, String port) {
            this.act = act;
            this.name = name;
            this.type = type;
            this.ip = ip;
            this.port = port;
        }

        @Override
        public void run() {
            String key = ip + ":" + port;
            if (act.discoveredKeys.contains(key)) return;
            act.discoveredKeys.add(key);

            if (act.tvScanStatus != null) {
                act.tvScanStatus.setText("Found " + act.discoveredKeys.size() + " device(s) on Wi-Fi (Tap to connect):");
            }

            if (act.containerDevices != null) {
                Button btn = new Button(act);
                String icon = "android".equals(type) ? "📱 " : "💻 ";
                btn.setText(icon + name + " (" + ip + ")\nTap to Connect & Sync");
                btn.setTextColor(0xFFFFFFFF);
                btn.setTextSize(13);
                btn.setBackgroundResource(R.drawable.btn_outline);
                btn.setPadding(20, 20, 20, 20);

                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                        LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT);
                lp.setMargins(0, 8, 0, 8);
                btn.setLayoutParams(lp);

                btn.setOnClickListener(new DeviceConnectClickListener(act, name, ip, port));
                act.containerDevices.addView(btn);
            }
        }
    }

    private static class DeviceConnectClickListener implements View.OnClickListener {
        private final MainActivity act;
        private final String name;
        private final String ip;
        private final String port;

        public DeviceConnectClickListener(MainActivity act, String name, String ip, String port) {
            this.act = act;
            this.name = name;
            this.ip = ip;
            this.port = port;
        }

        @Override
        public void onClick(View v) {
            String targetWs = "ws://" + ip + ":" + port + "/ws";
            act.preferences.setPairedDeviceName(name);
            act.etServerUrl.setText(targetWs);
            act.saveCurrentSettings();
            act.startSyncService();
            Toast.makeText(act, "Sending connection request to " + name + "...\nPlease click 'ALLOW' on " + name, Toast.LENGTH_LONG).show();
        }
    }

    public void updateStatusFromReceiver(String status) {
        if (tvConnectionStatus != null) {
            tvConnectionStatus.setText("Status: " + status);
        }
        if (status != null && status.startsWith("Received: ") && tvLatestClipboard != null) {
            tvLatestClipboard.setText(status.substring(10));
        }
    }

    private void loadSavedSettings() {
        String mode = preferences.getMode();
        if ("firebase".equalsIgnoreCase(mode)) {
            rbFirebase.setChecked(true);
        } else {
            rbWebSocket.setChecked(true);
        }

        etServerUrl.setText(preferences.getServerUrl());
        etDeviceName.setText(preferences.getDeviceName());

        boolean active = preferences.isServiceActive();
        switchService.setChecked(active);
        if (active) {
            startSyncService();
        }
    }

    private void saveCurrentSettings() {
        preferences.setMode(rbFirebase.isChecked() ? "firebase" : "websocket");
        preferences.setServerUrl(etServerUrl.getText().toString().trim());
        preferences.setDeviceName(etDeviceName.getText().toString().trim());

        ClipboardSyncService service = ClipboardSyncService.getInstance();
        if (service != null) {
            service.connectSync();
        }
    }

    private void startSyncService() {
        try {
            Intent intent = new Intent(this, ClipboardSyncService.class);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                try {
                    startForegroundService(intent);
                } catch (Throwable t) {
                    startService(intent);
                }
            } else {
                startService(intent);
            }
            tvConnectionStatus.setText("Status: Service Running");
        } catch (Throwable t) {
            Log.e("MainActivity", "Failed to start sync service", t);
            tvConnectionStatus.setText("Status: Stopped (Permission required)");
            Toast.makeText(this, "Notification permission needed for background sync", Toast.LENGTH_SHORT).show();
        }
    }

    private void stopSyncService() {
        try {
            Intent intent = new Intent(this, ClipboardSyncService.class);
            stopService(intent);
            tvConnectionStatus.setText("Status: Stopped");
        } catch (Throwable ignored) {}
    }

    private void requestBatteryOptimizationExemption() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null && !pm.isIgnoringBatteryOptimizations(getPackageName())) {
                try {
                    Intent intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
                    intent.setData(Uri.parse("package:" + getPackageName()));
                    startActivity(intent);
                } catch (Exception e) {
                    Intent intent = new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS);
                    startActivity(intent);
                }
            } else {
                Toast.makeText(this, "Battery optimizations already disabled! App won't be killed.", Toast.LENGTH_SHORT).show();
            }
        }
    }

    @Override
    protected void onDestroy() {
        if (statusReceiver != null) {
            try {
                unregisterReceiver(statusReceiver);
            } catch (Exception ignored) {}
        }
        super.onDestroy();
    }
}
