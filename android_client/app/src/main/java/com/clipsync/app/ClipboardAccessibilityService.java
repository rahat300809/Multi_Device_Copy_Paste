package com.clipsync.app;

import android.accessibilityservice.AccessibilityService;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.util.Log;
import android.view.accessibility.AccessibilityEvent;

public class ClipboardAccessibilityService extends AccessibilityService {
    private static final String TAG = "ClipAccessibility";
    private ClipboardManager clipboardManager;
    private String lastText = "";

    @Override
    public void onServiceConnected() {
        super.onServiceConnected();
        clipboardManager = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        Log.i(TAG, "Accessibility Service connected and listening for global copy events.");
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        // Intercept text selection, focus changes, or window state changes
        int eventType = event.getEventType();
        if (eventType == AccessibilityEvent.TYPE_VIEW_TEXT_SELECTION_CHANGED ||
            eventType == AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED ||
            eventType == AccessibilityEvent.TYPE_VIEW_CLICKED) {

            checkAndSyncClipboard();
        }
    }

    private void checkAndSyncClipboard() {
        if (clipboardManager == null) {
            clipboardManager = (ClipboardManager) getSystemService(Context.CLIPBOARD_SERVICE);
        }
        if (clipboardManager != null && clipboardManager.hasPrimaryClip()) {
            try {
                ClipData clip = clipboardManager.getPrimaryClip();
                if (clip != null && clip.getItemCount() > 0) {
                    CharSequence cs = clip.getItemAt(0).getText();
                    if (cs != null && cs.length() > 0) {
                        String current = cs.toString();
                        if (!current.equals(lastText)) {
                            lastText = current;
                            Log.d(TAG, "Global copy detected by Accessibility Service: " + current);

                            ClipboardSyncService service = ClipboardSyncService.getInstance();
                            if (service != null) {
                                service.sendToRemote(current);
                            }
                        }
                    }
                }
            } catch (Exception e) {
                Log.w(TAG, "Could not access clipboard from accessibility service", e);
            }
        }
    }

    @Override
    public void onInterrupt() {
        Log.w(TAG, "Accessibility Service interrupted");
    }
}
