package com.clipsync.app;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public class StatusReceiver extends BroadcastReceiver {
    private final MainActivity activity;

    public StatusReceiver(MainActivity activity) {
        this.activity = activity;
    }

    @Override
    public void onReceive(Context context, Intent intent) {
        if (activity == null || intent == null) return;
        String action = intent.getAction();
        if (ClipboardSyncService.ACTION_STATUS_UPDATE.equals(action)) {
            String status = intent.getStringExtra(ClipboardSyncService.EXTRA_STATUS);
            activity.updateStatusFromReceiver(status);
        } else if (ClipboardSyncService.ACTION_DEVICE_DISCOVERED.equals(action)) {
            String name = intent.getStringExtra(ClipboardSyncService.EXTRA_DEV_NAME);
            String type = intent.getStringExtra(ClipboardSyncService.EXTRA_DEV_TYPE);
            String ip = intent.getStringExtra(ClipboardSyncService.EXTRA_DEV_IP);
            String port = intent.getStringExtra(ClipboardSyncService.EXTRA_DEV_PORT);
            activity.onDeviceDiscovered(name, type, ip, port);
        } else if (ClipboardSyncService.ACTION_PAIR_REQUEST.equals(action)) {
            String name = intent.getStringExtra(ClipboardSyncService.EXTRA_DEV_NAME);
            String ip = intent.getStringExtra(ClipboardSyncService.EXTRA_DEV_IP);
            String port = intent.getStringExtra(ClipboardSyncService.EXTRA_DEV_PORT);
            activity.onPairRequestReceived(name, ip, port);
        } else if (ClipboardSyncService.ACTION_PAIR_RESPONSE.equals(action)) {
            String status = intent.getStringExtra(ClipboardSyncService.EXTRA_PAIR_STATUS);
            String name = intent.getStringExtra(ClipboardSyncService.EXTRA_DEV_NAME);
            activity.onPairResponseReceived(status, name);
        }
    }
}
