package com.clipsync.app;

import android.app.Activity;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.PowerManager;
import android.provider.Settings;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.RadioButton;
import android.widget.RadioGroup;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

public class MainActivity extends Activity implements View.OnClickListener {
    private AppPreferences preferences;
    private Switch switchService;
    private RadioGroup rgMode;
    private RadioButton rbWebSocket;
    private RadioButton rbFirebase;
    private EditText etServerUrl;
    private EditText etDeviceName;
    private TextView tvConnectionStatus;
    private TextView tvLatestClipboard;

    private BroadcastReceiver statusReceiver;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        preferences = new AppPreferences(this);
        initViews();
        loadSavedSettings();

        statusReceiver = new StatusReceiver(this);
        IntentFilter filter = new IntentFilter(ClipboardSyncService.ACTION_STATUS_UPDATE);
        if (Build.VERSION.SDK_INT >= 33) {
            registerReceiver(statusReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(statusReceiver, filter);
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

        Button btnSaveConfig = (Button) findViewById(R.id.btnSaveConfig);
        Button btnAccessibility = (Button) findViewById(R.id.btnAccessibility);
        Button btnBattery = (Button) findViewById(R.id.btnBattery);
        Button btnTestPush = (Button) findViewById(R.id.btnTestPush);

        switchService.setOnClickListener(this);
        btnSaveConfig.setOnClickListener(this);
        btnAccessibility.setOnClickListener(this);
        btnBattery.setOnClickListener(this);
        btnTestPush.setOnClickListener(this);
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
        } else if (id == R.id.btnAccessibility) {
            Intent intent = new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS);
            startActivity(intent);
            Toast.makeText(this, "Find 'ClipSync' and turn it ON to allow background copy sync", Toast.LENGTH_LONG).show();
        } else if (id == R.id.btnBattery) {
            requestBatteryOptimizationExemption();
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
        Intent intent = new Intent(this, ClipboardSyncService.class);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent);
        } else {
            startService(intent);
        }
        tvConnectionStatus.setText("Status: Service Running");
    }

    private void stopSyncService() {
        Intent intent = new Intent(this, ClipboardSyncService.class);
        stopService(intent);
        tvConnectionStatus.setText("Status: Stopped");
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
