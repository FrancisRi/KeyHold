@echo off
setlocal
cd /d "%~dp0"
if not exist "KeyHold.exe" call Build.cmd
if not exist "KeyHold.exe" exit /b 1
start "" "%~dp0KeyHold.exe"
