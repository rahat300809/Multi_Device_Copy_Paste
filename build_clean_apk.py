import os
import subprocess
import zipfile
import shutil

WORKSPACE = r"c:\Users\Rahat\Desktop\Copy_Paste"
ANDROID_DIR = os.path.join(WORKSPACE, "android_client")
BUILD_DIR = os.path.join(ANDROID_DIR, "build")
BUILD_TOOLS = r"C:\Users\Rahat\AppData\Local\Android\Sdk\build-tools\34.0.0"
PLATFORM = r"C:\Users\Rahat\AppData\Local\Android\Sdk\platforms\android-34\android.jar"
JAVAC = r"C:\Users\Rahat\Development\Java\jdk-21.0.12.1+1\bin\javac.exe"
KEYTOOL = r"C:\Program Files\Eclipse Adoptium\jre-8.0.504.1-hotspot\bin\keytool.exe"

print("=" * 60)
print("Building 100% Clean Production Android APK")
print("=" * 60)

# 1. Clean build directory
if os.path.exists(BUILD_DIR):
    shutil.rmtree(BUILD_DIR, ignore_errors=True)
os.makedirs(os.path.join(BUILD_DIR, "gen"), exist_ok=True)
os.makedirs(os.path.join(BUILD_DIR, "bin"), exist_ok=True)

# 2. Compile resources
print("[1/6] Compiling resources with aapt2...")
subprocess.run([
    os.path.join(BUILD_TOOLS, "aapt2.exe"), "compile",
    "--dir", os.path.join(ANDROID_DIR, "app", "src", "main", "res"),
    "-o", os.path.join(BUILD_DIR, "compiled_res.zip")
], check=True)

# 3. Link resources (min-sdk 21 for universal Android support, target-sdk 33 for broad compatibility)
print("[2/6] Linking resources with aapt2...")
manifest = os.path.join(ANDROID_DIR, "app", "src", "main", "AndroidManifest.xml")
subprocess.run([
    os.path.join(BUILD_TOOLS, "aapt2.exe"), "link",
    "-o", os.path.join(BUILD_DIR, "base.apk"),
    "-I", PLATFORM,
    "--manifest", manifest,
    "--min-sdk-version", "21",
    "--target-sdk-version", "33",
    "--version-code", "1",
    "--version-name", "1.0.0",
    "--java", os.path.join(BUILD_DIR, "gen"),
    os.path.join(BUILD_DIR, "compiled_res.zip"),
    "--auto-add-overlay"
], check=True)

# 4. Compile Java sources
print("[3/6] Compiling Java code with javac...")
java_files = [
    os.path.join(BUILD_DIR, "gen", "com", "clipsync", "app", "R.java")
]
src_dir = os.path.join(ANDROID_DIR, "app", "src", "main", "java", "com", "clipsync", "app")
for f in os.listdir(src_dir):
    if f.endswith(".java"):
        java_files.append(os.path.join(src_dir, f))

lib_jar = os.path.join(ANDROID_DIR, "libs", "Java-WebSocket-1.5.6.jar")
cp = f"{PLATFORM};{lib_jar}"
cmd_javac = [
    JAVAC, "-g", "-encoding", "UTF-8", "-target", "8", "-source", "8",
    "-cp", cp,
    "-d", os.path.join(BUILD_DIR, "bin")
] + java_files
subprocess.run(cmd_javac, check=True)

# 5. Compile DEX with d8
print("[4/6] Converting classes to DEX with d8...")
class_files = []
bin_app = os.path.join(BUILD_DIR, "bin", "com", "clipsync", "app")
for f in os.listdir(bin_app):
    if f.endswith(".class"):
        class_files.append(os.path.join(bin_app, f))

cmd_d8 = [
    os.path.join(BUILD_TOOLS, "d8.bat"),
    "--min-api", "21",
    "--output", BUILD_DIR,
    "--lib", PLATFORM
] + class_files + [lib_jar]
subprocess.run(cmd_d8, check=True, shell=True)

# 6. Assemble clean APK without duplicate entries
print("[5/6] Packaging cleanly into APK...")
fresh_apk = os.path.join(BUILD_DIR, "fresh.apk")
with zipfile.ZipFile(os.path.join(BUILD_DIR, "base.apk"), "r") as zin:
    with zipfile.ZipFile(fresh_apk, "w", zipfile.ZIP_DEFLATED) as zout:
        for item in zin.infolist():
            zout.writestr(item, zin.read(item.filename))
        with open(os.path.join(BUILD_DIR, "classes.dex"), "rb") as f:
            zout.writestr("classes.dex", f.read())

# 7. Zipalign (4-byte alignment)
print("[6/6] Aligning and signing APK...")
aligned_apk = os.path.join(BUILD_DIR, "aligned.apk")
subprocess.run([
    os.path.join(BUILD_TOOLS, "zipalign.exe"),
    "-p", "-f", "4", fresh_apk, aligned_apk
], check=True)

# Generate persistent keystore if needed
keystore = os.path.join(ANDROID_DIR, "release.keystore")
if not os.path.exists(keystore):
    subprocess.run([
        KEYTOOL, "-genkeypair", "-v",
        "-keystore", keystore,
        "-storetype", "PKCS12",
        "-storepass", "clipsync123",
        "-alias", "clipsync",
        "-keypass", "clipsync123",
        "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
        "-dname", "CN=ClipSync Open Source, OU=Production, O=ClipSync Project, L=San Francisco, ST=California, C=US"
    ], check=True)

output_apk = os.path.join(WORKSPACE, "ClipSync.apk")
subprocess.run([
    os.path.join(BUILD_TOOLS, "apksigner.bat"),
    "sign",
    "--ks", keystore,
    "--ks-pass", "pass:clipsync123",
    "--key-pass", "pass:clipsync123",
    "--v1-signing-enabled", "true",
    "--v2-signing-enabled", "true",
    "--v3-signing-enabled", "true",
    "--out", output_apk,
    aligned_apk
], check=True, shell=True)

# Also copy to android_client folder
shutil.copy2(output_apk, os.path.join(ANDROID_DIR, "ClipSync.apk"))

# Verification
print("\nVerifying APK...")
subprocess.run([
    os.path.join(BUILD_TOOLS, "apksigner.bat"),
    "verify", "--verbose", output_apk
], check=True, shell=True)

print(f"\n✅ SUCCESS! Clean universal ClipSync.apk built: {output_apk} ({os.path.getsize(output_apk) / 1024:.1f} KB)")
