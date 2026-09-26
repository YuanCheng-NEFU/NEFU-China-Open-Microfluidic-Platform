@echo off
setlocal EnableExtensions
for %%I in ("%~dp0.") do set "ROOT=%%~fI"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "CSC_X86=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
set "EXE=%ROOT%\NEFU_China_iDEC_TotalControl_V7_0.exe"
set "EXE_NEW=%ROOT%\NEFU_China_iDEC_TotalControl_V7_0.new.exe"
set "BRIDGE_SOURCE=%ROOT%\ch297_bridge\Ch297BridgeV63.cs"
set "BRIDGE_EXE=%ROOT%\runtime\ch297\NEFU_CH297_Bridge_V6_3_x86.exe"
set "BRIDGE_NEW=%ROOT%\runtime\ch297\NEFU_CH297_Bridge_V6_3_x86.new.exe"
set "BUILD_STAMP=%ROOT%\BUILD_ID_V7_0_PERF.txt"
set "LOG=%ROOT%\build_total_control_v7_0.log"

echo ============================================================
echo NEFU-CHINA iDEC TOTAL CONTROL V7.0 PERF - BUILD
echo Northeast Forestry University
echo ============================================================
echo.

if not exist "%CSC%" (
    echo [ERROR] 64-bit .NET Framework compiler not found: %CSC%
    if /I not "%~1"=="--no-pause" pause
    exit /b 1
)
if not exist "%CSC_X86%" (
    echo [ERROR] 32-bit .NET Framework compiler not found: %CSC_X86%
    if /I not "%~1"=="--no-pause" pause
    exit /b 1
)
if not exist "%BRIDGE_SOURCE%" (
    echo [ERROR] Missing CH297 x86 bridge source: %BRIDGE_SOURCE%
    if /I not "%~1"=="--no-pause" pause
    exit /b 1
)

for %%F in (
    "%ROOT%\src\CameraWorkstationV4.cs"
    "%ROOT%\src\Lsp02PumpController.cs"
    "%ROOT%\src\PynqV6Client.cs"
    "%ROOT%\src\V6AppSupport.cs"
    "%ROOT%\src\V6WorkspaceControls.cs"
    "%ROOT%\src\V6CameraPmtModules.cs"
    "%ROOT%\src\V6PynqModule.cs"
    "%ROOT%\src\V6PumpModule.cs"
    "%ROOT%\src\TotalControlV6.cs"
) do (
    if not exist "%%~fF" (
        echo [ERROR] Missing source file: %%~fF
        if /I not "%~1"=="--no-pause" pause
        exit /b 1
    )
)

if not exist "%ROOT%\runtime\ch297" mkdir "%ROOT%\runtime\ch297"
if not exist "%ROOT%\runtime\ch297" exit /b 1

del /q "%EXE_NEW%" 2>nul
del /q "%BRIDGE_NEW%" 2>nul

echo [1/2] Building CH297 x86 bridge...
"%CSC_X86%" /nologo /utf8output /codepage:65001 /platform:x86 /target:exe /optimize+ /warn:4 /main:Ch297BridgeProgram /out:"%BRIDGE_NEW%" /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:Microsoft.CSharp.dll "%BRIDGE_SOURCE%" >"%LOG%" 2>&1
if errorlevel 1 (
    echo.
    echo [ERROR] CH297 x86 bridge build failed:
    type "%LOG%"
    if /I not "%~1"=="--no-pause" pause
    exit /b 1
)

echo [2/2] Building total-control x64 UI...
"%CSC%" /nologo /utf8output /codepage:65001 /platform:x64 /target:winexe /optimize+ /warn:4 /main:V6Program /out:"%EXE_NEW%" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "%ROOT%\src\CameraWorkstationV4.cs" "%ROOT%\src\Lsp02PumpController.cs" "%ROOT%\src\PynqV6Client.cs" "%ROOT%\src\V6AppSupport.cs" "%ROOT%\src\V6WorkspaceControls.cs" "%ROOT%\src\V6CameraPmtModules.cs" "%ROOT%\src\V6PynqModule.cs" "%ROOT%\src\V6PumpModule.cs" "%ROOT%\src\TotalControlV6.cs" >>"%LOG%" 2>&1
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed:
    type "%LOG%"
    if /I not "%~1"=="--no-pause" pause
    exit /b 1
)

move /y "%BRIDGE_NEW%" "%BRIDGE_EXE%" >nul
if errorlevel 1 (
    echo [ERROR] Could not replace CH297 bridge. Close the running platform and try again.
    if /I not "%~1"=="--no-pause" pause
    exit /b 1
)
move /y "%EXE_NEW%" "%EXE%" >nul
if errorlevel 1 (
    echo [ERROR] Could not replace main UI. Close the running platform and try again.
    if /I not "%~1"=="--no-pause" pause
    exit /b 1
)
>"%BUILD_STAMP%" echo NEFU-China iDEC Total Control V7.0 PERF

echo [OK] Created:
echo %EXE%
echo %BRIDGE_EXE%
echo.
if /I not "%~1"=="--no-pause" pause
exit /b 0
