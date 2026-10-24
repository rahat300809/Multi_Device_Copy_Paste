@echo off
title ClipSync Exe Builder
echo ========================================================
echo         ClipSync - Windows EXE Builder (PyInstaller)
echo ========================================================
echo.

:: Check Python installation
python --version >nul 2>&1
if %errorlevel% neq 0 (
    echo [ERROR] Python is not found in PATH! Please install Python.
    pause
    exit /b 1
)

echo [*] Installing required dependencies...
python -m pip install -r requirements.txt

echo.
echo [*] Compiling ClipSync into a standalone background .exe...
python -m PyInstaller --onefile --noconsole --name "ClipSync" --clean client.py

if %errorlevel% equ 0 (
    echo.
    echo ========================================================
    echo [SUCCESS] Standalone EXE created successfully!
    echo Location: %cd%\dist\ClipSync.exe
    echo ========================================================
    echo.
    echo You can move ClipSync.exe anywhere or launch it now.
    echo It will run silently in your System Tray and start on Windows boot!
) else (
    echo.
    echo [ERROR] Build failed. Please inspect the output above.
)

pause
