@echo off
setlocal
cd /d "%~dp0"
if not exist ".build\winui\KeyHold.exe" goto build
if not exist ".build\winui\keyhold_worker.dll" goto build
goto run
:build
call Build.cmd
if errorlevel 1 exit /b 1
:run
start "" "%~dp0.build\winui\KeyHold.exe"
