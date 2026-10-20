@echo off
setlocal
cd /d "%~dp0"
set "MACRO_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%MACRO_CSC%" set "MACRO_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%MACRO_CSC%" (
  echo .NET Framework compiler not found. Enable or repair .NET Framework 4.8.
  pause
  exit /b 1
)
"%MACRO_CSC%" /nologo /utf8output /codepage:65001 /target:winexe /platform:anycpu /optimize+ /win32manifest:app.manifest /win32icon:assets\KeyHold.ico /resource:assets\KeyHold.ico,KeyHold.AppIcon /out:KeyHold.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\Feedback.cs src\Localization.cs src\Core.cs src\FluentUi.cs src\Native.cs src\KeyboardWorker.cs src\MacroEditor.cs src\Program.cs
if errorlevel 1 (
  echo Build failed. Close the running program before rebuilding.
  pause
  exit /b 1
)
echo Build succeeded: KeyHold.exe
pause
