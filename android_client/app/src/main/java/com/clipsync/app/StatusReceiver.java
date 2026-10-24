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
        if (ClipboardSyncService.ACTION_STATUS_UPDATE.equals(intent.getAction())) {
            String status = intent.getStringExtra(ClipboardSyncService.EXTRA_STATUS);
            if (activity != null) {
                activity.updateStatusFromReceiver(status);
            }
        }
    }
}
