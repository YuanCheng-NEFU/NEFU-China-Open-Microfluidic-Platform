@echo off
setlocal EnableExtensions
for %%I in ("%~dp0.") do set "ROOT=%%~fI"

echo ============================================================
echo CH297 32-bit COM DLL registration - run as Administrator
echo ============================================================
echo.
echo Target: %ROOT%\runtime\ch297\PMTCount.dll
echo This changes Windows COM registration. Continue only if the
echo bridge reports that PMTCount.PMTCounter is not registered.
echo.
choice /C YN /N /M "Register now? [Y/N] "
if errorlevel 2 exit /b 0

if not exist "%ROOT%\runtime\ch297\PMTCount.dll" (
    echo [ERROR] PMTCount.dll not found.
    pause
    exit /b 1
)

"%WINDIR%\SysWOW64\regsvr32.exe" "%ROOT%\runtime\ch297\PMTCount.dll"
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" (
    echo [OK] Registration command completed.
) else (
    echo [ERROR] Registration failed. Confirm Administrator rights.
)
pause
exit /b %RC%
