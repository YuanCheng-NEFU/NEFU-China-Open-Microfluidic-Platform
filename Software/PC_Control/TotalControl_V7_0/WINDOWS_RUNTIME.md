# Windows Runtime

The Windows package contains the x64 V7.0 PERF application and the x86 CH297 bridge.

| Component | Location |
| --- | --- |
| Main application | `NEFU_China_iDEC_TotalControl_V7_0.exe` |
| CH297 bridge | `runtime/ch297/NEFU_CH297_Bridge_V6_3_x86.exe` |
| CH297 vendor files | `runtime/ch297/PMTCount.dll` and the instrument's `para.ini` |
| Camera SDK | OsCam installation directory or `NEFU_OSCAM_DIR` |

Install .NET Framework 4.8 or 4.8.1. Obtain matching drivers and SDK components from the instrument suppliers. Keep the complete SDK dependency set; the project does not relabel vendor software under its MIT license.

Run `04_CHECK_ENVIRONMENT.cmd --no-pause` for a read-only file, architecture and registry check. It does not open ports, activate COM or energize outputs. Exit 0 means the checked prerequisites are present; exit 2 means installation actions remain; exit 1 means inspection failed.

Register the CH297 x86 COM component only through the vendor installation procedure or the explicit administrator helper `05_REGISTER_CH297_DLL_ADMIN.cmd`. Preserve the supplied INI schema and unit calibration. A read-only preflight does not verify calibration or physical instrument behavior.

Launch `02_RUN_TOTAL_CONTROL_V7_0.cmd`. Use `01_RUN_CH297_BRIDGE_X86.cmd` only for a deliberate detector diagnostic; it opens the configured acquisition port.
