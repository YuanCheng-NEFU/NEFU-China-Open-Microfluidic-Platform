@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Check-Runtime.ps1"
set "RC=%ERRORLEVEL%"
if /I not "%~1"=="--no-pause" pause
exit /b %RC%
