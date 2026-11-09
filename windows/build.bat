@echo off
rem Builds AudioBridge.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem No SDK or runtime download needed.
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler not found.
  exit /b 1
)
if not exist out mkdir out
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /warn:4 ^
  /win32icon:app.ico /win32manifest:app.manifest ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:out\AudioBridge.exe *.cs
if errorlevel 1 exit /b 1
echo Built %cd%\out\AudioBridge.exe
