@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-android.ps1"
if errorlevel 1 (
  echo.
  echo Build failed. Check you have Android Studio, SDK Platform 35 and JDK 17.
  pause
  exit /b 1
)
pause
