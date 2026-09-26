@echo off
chcp 65001 >nul
setlocal EnableExtensions

title NEFU-China iDEC - Serial Port Identification

echo ============================================================
echo SERIAL PORTS REPORTED BY WINDOWS
echo ============================================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_SerialPort -ErrorAction SilentlyContinue | Select-Object DeviceID,Name,PNPDeviceID | Sort-Object DeviceID | Format-Table -AutoSize"

echo.
echo Close the CH297 vendor application before using the same COM port
echo in the total-control platform.
echo.
pause
exit /b 0
