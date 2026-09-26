@echo off
chcp 65001 >nul
setlocal EnableExtensions
for %%I in ("%~dp0.") do set "ROOT=%%~fI"

title NEFU-China iDEC CH297 Bridge - CSharp x86

set "RUNTIME=%ROOT%\runtime\ch297"
set "EXE=%RUNTIME%\NEFU_CH297_Bridge_V6_3_x86.exe"

if not defined NEFU_CH297_COM set "NEFU_CH297_COM=COM10"
if not defined NEFU_CH297_PORT_SETTING set "NEFU_CH297_PORT_SETTING=19200,n,8,1"
if not defined NEFU_CH297_GATE_MS set "NEFU_CH297_GATE_MS=10"
if not defined NEFU_CH297_DATA_PORT set "NEFU_CH297_DATA_PORT=5101"
if not defined NEFU_CH297_CONTROL_PORT set "NEFU_CH297_CONTROL_PORT=5102"
if not defined NEFU_CH297_THRESHOLD set "NEFU_CH297_THRESHOLD=600"
if not defined NEFU_CH297_HYSTERESIS set "NEFU_CH297_HYSTERESIS=10"
set "NEFU_CH297_RUNTIME=%RUNTIME%"

if not exist "%EXE%" (
    echo [INFO] CH297 x86 bridge not found. Building now...
    call "%ROOT%\00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)

echo COM: %NEFU_CH297_COM%
echo Gate: %NEFU_CH297_GATE_MS% ms
echo Diagnostic launcher keeps the console visible.
echo.

pushd "%RUNTIME%"
"%EXE%"
set "RC=%ERRORLEVEL%"
popd

echo.
if not "%RC%"=="0" (
    echo [ERROR] CH297 bridge exited with code %RC%.
    echo Check:
    echo %ROOT%\ch297_logs\CH297_bridge_latest.log
    echo %RUNTIME%\CH297_bridge_startup_error.log
) else (
    echo [INFO] CH297 bridge closed normally.
)
pause
exit /b %RC%