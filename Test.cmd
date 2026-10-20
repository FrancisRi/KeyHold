@echo off
setlocal
cd /d "%~dp0"
set "MACRO_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%MACRO_CSC%" set "MACRO_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%MACRO_CSC%" (
  echo .NET Framework compiler not found.
  pause
  exit /b 1
)
"%MACRO_CSC%" /nologo /utf8output /codepage:65001 /target:exe /out:tests\CoreTests.exe /reference:System.Web.Extensions.dll src\Feedback.cs src\Localization.cs src\Core.cs tests\FeedbackTests.cs tests\SequenceTests.cs tests\CoreTests.cs
if errorlevel 1 (
  pause
  exit /b 1
)
tests\CoreTests.exe
if errorlevel 1 (
  pause
  exit /b 1
)
"%MACRO_CSC%" /nologo /utf8output /codepage:65001 /target:exe /out:tests\WorkerTests.exe /reference:System.Web.Extensions.dll src\Feedback.cs src\Localization.cs src\Core.cs src\KeyboardWorker.cs tests\WorkerTests.cs
if errorlevel 1 (
  pause
  exit /b 1
)
tests\WorkerTests.exe
if errorlevel 1 (
  pause
  exit /b 1
)
"%MACRO_CSC%" /nologo /utf8output /codepage:65001 /target:exe /out:tests\UiTests.exe /main:UiTests /win32manifest:app.manifest /win32icon:assets\KeyHold.ico /resource:assets\KeyHold.ico,KeyHold.AppIcon /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll src\Feedback.cs src\Localization.cs src\Core.cs src\FluentUi.cs src\Native.cs src\KeyboardWorker.cs src\MacroEditor.cs src\Program.cs tests\UiTests.cs
if errorlevel 1 (
  pause
  exit /b 1
)
tests\UiTests.exe
if errorlevel 1 (
  pause
  exit /b 1
)
pause
