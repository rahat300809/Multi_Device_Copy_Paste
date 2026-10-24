package com.clipsync.app;

import android.accessibilityservice.AccessibilityService;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.content.Intent;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.accessibility.AccessibilityEvent;

public class ClipboardAccessibilityService extends AccessibilityService implements Runnable {
    private static final String TAG = "ClipAccessibility";
    private static ClipboardAccessibilityService sInstance;

    private ClipboardManager clipboardManager;
    private Handler handler;
    private String lastText = "";

    public static synchronized ClipboardAccessibilityService getInstance() {
        return sInstance;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        sInstance = this;
        handler = new Handler(Looper.getMainLooper());
    }

    @Override
    public void onServiceConnected() {
        super.onServiceConnected();
        sInstance = this;
        clipboardManager = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        Log.i(TAG, "Accessibility Service connected and listening for global copy events.");
        checkAndSyncClipboard();
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        checkAndSyncClipboard();
        if (handler != null) {
            handler.removeCallbacks(this);
            handler.postDelayed(this, 200);
        }
    }

    @Override
    public void run() {
        checkAndSyncClipboard();
    }

    public void checkAndSyncClipboard() {
        try {
            if (clipboardManager == null) {
                clipboardManager = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            }
            if (clipboardManager != null && clipboardManager.hasPrimaryClip()) {
                ClipData clip = clipboardManager.getPrimaryClip();
                if (clip != null && clip.getItemCount() > 0) {
                    CharSequence cs = clip.getItemAt(0).getText();
                    if (cs != null && cs.length() > 0) {
                        String current = cs.toString();
                        if (!current.equals(lastText)) {
                            lastText = current;
                            Log.d(TAG, "Global copy detected: " + (current.length() > 30 ? current.substring(0, 30) + "..." : current));

                            ClipboardSyncService syncService = ClipboardSyncService.getInstance();
                            if (syncService != null) {
                                syncService.sendToRemote(current);
                            }

                            try {
                                Intent intent = new Intent(this, ClipboardSyncService.class);
                                intent.setAction(ClipboardSyncService.ACTION_BROADCAST_COPY);
                                intent.putExtra(ClipboardSyncService.EXTRA_CLIPBOARD_TEXT, current);
                                if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
                                    startForegroundService(intent);
                                } else {
                                    startService(intent);
                                }
                            } catch (Exception ignored) {}
                        }
                    }
                }
            }
        } catch (Exception e) {
            Log.w(TAG, "Error checking clipboard: " + e.getMessage());
        }
    }

    public void setClipboardText(String text) {
        lastText = text;
        try {
            if (clipboardManager == null) {
                clipboardManager = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
            }
            if (clipboardManager != null) {
                ClipData clip = ClipData.newPlainText("ClipSync", text);
                clipboardManager.setPrimaryClip(clip);
            }
        } catch (Exception e) {
            Log.w(TAG, "Failed to set clipboard in accessibility service: " + e.getMessage());
        }
    }

    @Override
    public void onInterrupt() {
        Log.w(TAG, "Accessibility Service interrupted");
    }

    @Override
    public void onDestroy() {
        sInstance = null;
        super.onDestroy();
    }
}
