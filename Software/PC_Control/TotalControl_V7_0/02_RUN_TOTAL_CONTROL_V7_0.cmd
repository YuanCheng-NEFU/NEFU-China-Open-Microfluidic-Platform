@echo off
setlocal EnableExtensions
for %%I in ("%~dp0.") do set "ROOT=%%~fI"

echo ============================================================
echo NEFU-CHINA iDEC TOTAL CONTROL PLATFORM V7.0 PERF
echo Northeast Forestry University
echo ============================================================
echo.

set "EXE=%ROOT%\NEFU_China_iDEC_TotalControl_V7_0.exe"
set "BRIDGE_EXE=%ROOT%\runtime\ch297\NEFU_CH297_Bridge_V6_3_x86.exe"
set "BUILD_STAMP=%ROOT%\BUILD_ID_V7_0_PERF.txt"

if not exist "%BUILD_STAMP%" goto :build
if not exist "%EXE%" goto :build
if not exist "%BRIDGE_EXE%" goto :build
goto :run

:build
    echo [INFO] Main UI or CH297 x86 bridge not found. Building now...
    call "%ROOT%\00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd"
    if errorlevel 1 (
        pause
        exit /b 1
    )

:run
if not exist "%ROOT%\config\total_control_v6.json" copy /y "%ROOT%\config\total_control_v6.example.json" "%ROOT%\config\total_control_v6.json" >nul
if not exist "%ROOT%\config\total_control_v6.json" exit /b 1
start "" "%EXE%"
exit /b 0
