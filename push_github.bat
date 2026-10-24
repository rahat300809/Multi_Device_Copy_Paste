@echo off
git init
git remote remove origin 2>nul
git remote add origin https://github.com/rahat300809/Multi_Device_Copy_Paste.git
git config user.email "rahat300809@github.com"
git config user.name "rahat300809"
git add -A
git commit -m "feat: ClipSync v1.0.0 - Multi-device clipboard sync (EXE + APK + Docker)"
git branch -M main
git push -u origin main --force
echo PUSH_DONE
