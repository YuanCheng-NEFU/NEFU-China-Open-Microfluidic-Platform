@echo off
chcp 65001 >nul
setlocal EnableExtensions
for %%I in ("%~dp0.") do set "ROOT=%%~fI"

title NEFU-China iDEC V7.0 PERF First Run

echo ============================================================
echo NEFU-CHINA iDEC TOTAL CONTROL V7.0 PERF - FIRST RUN
echo ============================================================
echo.
echo This will build the x64 main UI and the x86 CH297 bridge,
echo then open one total-control window.
echo.

call "%ROOT%\00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd"
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    pause
    exit /b 1
)

call "%ROOT%\02_RUN_TOTAL_CONTROL_V7_0.cmd"
exit /b 0