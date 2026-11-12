@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build.ps1" -Test
set "KEYHOLD_EXIT=%ERRORLEVEL%"
pause
exit /b %KEYHOLD_EXIT%
