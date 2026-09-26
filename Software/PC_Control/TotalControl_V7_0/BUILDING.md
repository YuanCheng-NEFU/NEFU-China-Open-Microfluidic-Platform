# Build Total Control V7.0

The solution builds the x64 camera, PMT, PYNQ, and syringe-pump workstation
together with its x86 CH297 bridge. Source file names retain the V6 module lineage;
the application and build target are **Total Control V7.0 PERF**.

## Requirements

- 64-bit Windows with .NET Framework 4.8 or later.
- For Visual Studio or MSBuild: the **.NET desktop development** workload and
  **.NET Framework 4.8 targeting pack**.
- For the command-script build: the Windows .NET Framework x64 and x86 C# compilers.

Vendor device libraries are runtime dependencies. They are not required to compile
either project.

## Build

Open `NEFU_China_iDEC_V7.sln`, select **Release / Mixed Platforms**, and build the
solution. The solution maps the workstation to x64 and the bridge to x86.

From a Visual Studio Developer Command Prompt:

```bat
msbuild NEFU_China_iDEC_V7.sln /t:Build /p:Configuration=Release /p:Platform="Mixed Platforms"
```

The command-script alternative uses the same source files and entry points:

```bat
00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd --no-pause
```

| Project | Architecture | Entry point | Output |
| --- | --- | --- | --- |
| TotalControl | x64 | V6Program | `NEFU_China_iDEC_TotalControl_V7_0.exe` |
| Ch297Bridge | x86 | Ch297BridgeProgram | `runtime/ch297/NEFU_CH297_Bridge_V6_3_x86.exe` |

Build **Debug / Mixed Platforms** for symbols. Intermediate files stay under
`obj/`. The command scripts and release packages use the executable paths above.

## Install Device Components

**Camera.** Install the vendor camera driver and the x64 SDK containing
`OEApi64.dll`. The workstation searches `NEFU_OSCAM_DIR`, the system
`Program Files/Oscam` directories, and its application directory. Keep the
SDK's dependent libraries together.

**CH297.** Place the vendor's x86 `PMTCount.dll` and the configuration file
`para.ini` supplied for the instrument in `runtime/ch297/`.
Use `05_REGISTER_CH297_DLL_ADMIN.cmd` in an administrator session to register the
x86 COM component. Use the instrument-specific calibration values.

**PYNQ and syringe pump.** Configure the board network endpoint and serial
connection in the workstation before connecting devices. Complete low-voltage
gate and pump commissioning before an integrated run.

## Check The Installation

Run this read-only preflight from the application directory:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Check-Runtime.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/Check-Runtime.ps1 -Json
```

It checks the two application binaries, PE architecture, .NET Framework runtime,
camera SDK location, CH297 configuration presence, and the x86 COM registration
path. It does not load a vendor library, activate a COM object, open a connection,
or operate equipment. Exit codes: **0** = checked prerequisites present;
**2** = installation action required; **1** = checker error.

## Offline Checks

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/test_runtime_preflight.ps1
python -m unittest discover -s tests -p "test_projects.py" -v
```

These checks use source manifests and temporary mock PE files. They do not start
the workstation or bridge and do not require connected devices.

