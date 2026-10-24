package com.clipsync.app;

import android.content.Context;
import android.content.SharedPreferences;

public class AppPreferences {
    private static final String PREF_NAME = "clipsync_prefs";
    private static final String KEY_MODE = "sync_mode"; // "websocket" or "firebase"
    private static final String KEY_SERVER_URL = "server_url";
    private static final String KEY_DEVICE_NAME = "device_name";
    private static final String KEY_SERVICE_ACTIVE = "service_active";

    private final SharedPreferences prefs;

    public AppPreferences(Context context) {
        this.prefs = context.getSharedPreferences(PREF_NAME, Context.MODE_PRIVATE);
    }

    public String getMode() {
        return prefs.getString(KEY_MODE, "websocket");
    }

    public void setMode(String mode) {
        prefs.edit().putString(KEY_MODE, mode).apply();
    }

    public String getServerUrl() {
        return prefs.getString(KEY_SERVER_URL, "ws://192.168.1.100:8765/ws");
    }

    public void setServerUrl(String url) {
        prefs.edit().putString(KEY_SERVER_URL, url).apply();
    }

    public String getDeviceName() {
        return prefs.getString(KEY_DEVICE_NAME, android.os.Build.MODEL);
    }

    public void setDeviceName(String name) {
        prefs.edit().putString(KEY_DEVICE_NAME, name).apply();
    }

    public boolean isServiceActive() {
        return prefs.getBoolean(KEY_SERVICE_ACTIVE, true);
    }

    public void setServiceActive(boolean active) {
        prefs.edit().putBoolean(KEY_SERVICE_ACTIVE, active).apply();
    }
}
