@echo off
setlocal EnableExtensions
for %%I in ("%~dp0.") do set "ROOT=%%~fI"

echo Starting NEFU-China iDEC V7.0 PERF environment...
echo 1. The 64-bit total-control window will open.
echo 2. Start CH297 from the PMT card after selecting COM and gate time.
echo 3. Run pynq_v6_perf_server.py on PYNQ-Z2 separately when needed.
echo.

call "%ROOT%\02_RUN_TOTAL_CONTROL_V7_0.cmd"
exit /b 0