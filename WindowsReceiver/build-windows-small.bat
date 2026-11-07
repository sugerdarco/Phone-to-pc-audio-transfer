@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo Install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
rem Smaller output: requires .NET 10 Desktop Runtime on the target PC.
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=false
if errorlevel 1 exit /b 1
echo.
echo Your smaller app is in:
echo %cd%\bin\Release\net10.0-windows\win-x64\publish\
pause
