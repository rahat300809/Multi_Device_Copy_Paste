package com.clipsync.app;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.os.Build;
import android.util.Log;

public class BootReceiver extends BroadcastReceiver {
    private static final String TAG = "ClipSyncBoot";

    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent != null ? intent.getAction() : "";
        if (Intent.ACTION_BOOT_COMPLETED.equals(action) ||
            "android.intent.action.QUICKBOOT_POWERON".equals(action) ||
            Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)) {

            Log.i(TAG, "Device booted or app updated. Starting ClipboardSyncService in background...");
            try {
                AppPreferences prefs = new AppPreferences(context);
                if (prefs.isServiceActive()) {
                    Intent serviceIntent = new Intent(context, ClipboardSyncService.class);
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                        try {
                            context.startForegroundService(serviceIntent);
                        } catch (Throwable t) {
                            context.startService(serviceIntent);
                        }
                    } else {
                        context.startService(serviceIntent);
                    }
                }
            } catch (Throwable t) {
                Log.e(TAG, "Failed to start service on boot", t);
            }
        }
    }
}
