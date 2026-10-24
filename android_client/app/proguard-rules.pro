# Proguard rules for minimal APK size
-keep class com.clipsync.app.** { *; }
-dontwarn org.java_websocket.**
-keep class org.java_websocket.** { *; }
-dontwarn okhttp3.**
-keep class okhttp3.** { *; }
