@echo off
title ClipSync APK Builder
echo ========================================================
echo         ClipSync - Native Android APK Fast Builder
echo ========================================================
echo.

set SDK_DIR=C:\Users\Rahat\AppData\Local\Android\Sdk
set BUILD_TOOLS=%SDK_DIR%\build-tools\34.0.0
set PLATFORM=%SDK_DIR%\platforms\android-34\android.jar
set JAVAC=C:\Users\Rahat\Development\Java\jdk-21.0.12.1+1\bin\javac.exe
set JAR=C:\Users\Rahat\Development\Java\jdk-21.0.12.1+1\bin\jar.exe
set KEYTOOL=C:\Program Files\Eclipse Adoptium\jre-8.0.504.1-hotspot\bin\keytool.exe

if not exist "%BUILD_TOOLS%\aapt2.exe" (
    echo [ERROR] Android build-tools not found in %BUILD_TOOLS%
    pause
    exit /b 1
)

echo [*] Step 1: Compiling and linking Android resources with aapt2...
if not exist "build" mkdir "build"
if not exist "build\gen" mkdir "build\gen"
if not exist "build\bin" mkdir "build\bin"

"%BUILD_TOOLS%\aapt2.exe" compile --dir app\src\main\res -o build\compiled_res.zip
"%BUILD_TOOLS%\aapt2.exe" link -o build\base.apk -I "%PLATFORM%" --manifest app\src\main\AndroidManifest.xml --java build\gen build\compiled_res.zip --auto-add-overlay

echo [*] Step 2: Compiling Java source files with javac...
del /q build\bin\*.* 2>nul
"%JAVAC%" -g:none -encoding UTF-8 -target 8 -source 8 -cp "%PLATFORM%;libs\Java-WebSocket-1.5.6.jar" -d build\bin build\gen\com\clipsync\app\R.java app\src\main\java\com\clipsync\app\*.java

echo [*] Step 3: Compiling classes to DEX with d8...
call "%BUILD_TOOLS%\d8.bat" --min-api 26 --output build --lib "%PLATFORM%" build\bin\com\clipsync\app\*.class libs\Java-WebSocket-1.5.6.jar

echo [*] Step 4: Packaging, aligning, and signing ClipSync.apk...
cd build
"%JAR%" -uf base.apk classes.dex
"%BUILD_TOOLS%\zipalign.exe" -p -f 4 base.apk ClipSync-unaligned.apk

if not exist "debug.keystore" (
    "%KEYTOOL%" -genkey -v -keystore "debug.keystore" -storepass "android" -alias "androiddebugkey" -keypass "android" -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US"
)

call "%BUILD_TOOLS%\apksigner.bat" sign --ks debug.keystore --ks-pass pass:android --key-pass pass:android --out ..\ClipSync.apk ClipSync-unaligned.apk
cd ..
copy /y ClipSync.apk ..\ClipSync.apk >nul

echo.
echo ========================================================
echo [SUCCESS] ClipSync.apk built and signed successfully!
echo Output: %cd%\ClipSync.apk
echo ========================================================
pause
