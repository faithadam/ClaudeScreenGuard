@echo off
rem Build ClaudeScreenGuard.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0.."
if not exist bin mkdir bin
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
set WPF=%FW%\WPF
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /win32icon:assets\icon.ico /out:bin\ClaudeScreenGuard.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "/r:%WPF%\UIAutomationClient.dll" "/r:%WPF%\UIAutomationTypes.dll" "/r:%WPF%\WindowsBase.dll" ^
  src\ClaudeScreenGuard.cs
if errorlevel 1 (echo Build failed. & exit /b 1)
echo Built bin\ClaudeScreenGuard.exe
