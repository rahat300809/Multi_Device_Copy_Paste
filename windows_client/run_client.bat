@echo off
title ClipSync Background Client
echo Starting ClipSync Windows Client in background...
python -m pip install -r requirements.txt >nul 2>&1
start "" pythonw client.py
echo ClipSync is now running in your System Tray!
timeout /t 2 >nul
exit
