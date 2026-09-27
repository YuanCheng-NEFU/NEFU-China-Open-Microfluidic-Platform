# NEFU-China Open Microfluidic Platform

An integrated platform for droplet imaging, fluorescence photon counting, programmable gating and fluid delivery.

[**Download Windows Package**](https://github.com/Astrid-Xu/NEFU-China-Open-Microfluidic-Platform/releases/download/v0.8.2/NEFU-China-Open-Microfluidic-Platform-0.8.2-windows-x64.zip) | [Build Guide](Manual/Build_Guide.pdf) | [Project Showcase](https://astrid-xu.github.io/NEFU-China-Open-Microfluidic-Platform/) | [Source & Releases](https://github.com/Astrid-Xu/NEFU-China-Open-Microfluidic-Platform/releases/tag/v0.8.2)

![Platform overview: droplet workflow, instrument architecture, optical design and representative project measurements](Hardware/Figures/project_overview.png)

*From droplet preparation to fluorescence-triggered actuation. Original project overview from the NEFU-China iDEC presentation.*

## What We Built

**Total Control V7.0 PERF** brings the camera, CH297 photon counter, PYNQ-Z2 gate controller and syringe pump into one Windows workspace. The open project pairs this workstation with its source, hardware references and a step-by-step build guide.

| Resource | Included |
| --- | --- |
| **Software** | Prebuilt x64 workstation and x86 CH297 bridge; C# source, Visual Studio solution and Python PYNQ service |
| **Hardware** | Editable BOM, device specifications, signal wiring, optical diagrams and assembly photographs |
| **Build Guide** | English PDF, editable Word and Markdown editions |
| **Examples** | Droplet microscopy video, camera observation and representative photon-count results |

## System Overview

Photon counts pass from the PMT through CH297 to the workstation and PYNQ-Z2. The controller drives the external gate of the DEP actuation chain, while camera observation and syringe-driven fluid delivery share the same workspace.

<table>
<tr>
<td width="50%"><img src="Hardware/Photos/optical_bench_side.png" alt="Side view of the imaging and fluorescence detection assembly" width="100%"></td>
<td width="50%"><img src="Hardware/Photos/optical_bench_top.png" alt="Top view of the optical breadboard and detection branches" width="100%"></td>
</tr>
<tr><td>Imaging and fluorescence detection assembly</td><td>Optical breadboard and detection branches</td></tr>
</table>

[Hardware reference](Hardware/README.md) | [Bill of materials](Hardware/BOM.xlsx) | [Wiring](Hardware/Wiring_Diagram.md)

## Demonstration

[![Droplets observed at the microfluidic junction](Examples/Images/camera_observation.png)](Examples/Video/Droplet_Microscopy.mp4)

**[Watch droplet microscopy](https://astrid-xu.github.io/NEFU-China-Open-Microfluidic-Platform/#demonstration)** | [Download MP4](https://raw.githubusercontent.com/Astrid-Xu/NEFU-China-Open-Microfluidic-Platform/main/Examples/Video/Droplet_Microscopy.mp4) | 42.8 s / 728 x 544 / 10 fps

![Representative eGFP and uninduced photon-count traces and net photon counts](Examples/Images/photon_counting.png)

*Representative eGFP and uninduced measurements from the project presentation. The original plotted comparisons are retained.*

## Key Features

- **One instrument workspace.** Camera observation, photon counting, PYNQ gate control and syringe-pump settings in Total Control V7.0 PERF.
- **Programmable gating.** Threshold and hysteresis control, manual output commands and automatic LOW on data or connection timeout.
- **Coordinated operation.** Integrated start and stop, camera recording and CH1 pump operation.
- **Open engineering resources.** Buildable source, an editable component inventory and documented optical, electrical and fluidic connections.

![Total Control V7.0 PERF workspace](Manual/images/control_workspace.png)

## Build It Yourself

### 1. Get the Project

| Download | Contents |
| --- | --- |
| [**Windows package 0.8.2**](https://github.com/Astrid-Xu/NEFU-China-Open-Microfluidic-Platform/releases/download/v0.8.2/NEFU-China-Open-Microfluidic-Platform-0.8.2-windows-x64.zip) | Workstation EXE, bridge EXE, all source, guide, hardware references and example media |
| [Source package 0.8.2](https://github.com/Astrid-Xu/NEFU-China-Open-Microfluidic-Platform/releases/download/v0.8.2/NEFU-China-Open-Microfluidic-Platform-0.8.2.zip) | The same project resources, ready to build |
| [Build Guide PDF](Manual/Build_Guide.pdf) | Assembly, installation, first operation and troubleshooting |

### 2. Assemble and Connect

Follow the [Build Guide](Manual/Build_Guide.pdf) for optical assembly, chip and tubing setup, instrument wiring and digital commissioning. Install .NET Framework 4.8+ and the camera/CH297 vendor components using [Windows Setup](Software/PC_Control/TotalControl_V7_0/WINDOWS_RUNTIME.md). Deploy the matching [PYNQ service](Software/PYNQ/README.md).

Keep the DEP amplifier physically disabled during wiring and digital commissioning.

### 3. Start the Workstation

Extract the Windows package and open `Software/PC_Control/TotalControl_V7_0`:

```bat
04_CHECK_ENVIRONMENT.cmd --no-pause
02_RUN_TOTAL_CONTROL_V7_0.cmd
```

To compile from source, run `00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd --no-pause` or open [NEFU_China_iDEC_V7.sln](Software/PC_Control/TotalControl_V7_0/NEFU_China_iDEC_V7.sln). See [Build Instructions](Software/PC_Control/TotalControl_V7_0/BUILDING.md).

### Project Structure

| Directory | Start Here |
| --- | --- |
| [Manual](Manual/README.md) | Build Guide in PDF, Word and Markdown |
| [Software](Software/PC_Control/README.md) | Windows control platform and [PYNQ service](Software/PYNQ/README.md) |
| [Hardware](Hardware/README.md) | BOM, diagrams, wiring and photographs |
| [Examples](Examples/README.md) | Selected video and project images |

[Release notes](RELEASE_NOTES.md) | [Packaging](RELEASING.md) | [Validation](VALIDATION.md)

## License

Software: [MIT](LICENSE). Project documentation, original diagrams and example media: [CC BY 4.0](LICENSE-DOCS). Vendor components are installed under their own licenses.

**NEFU-China iDEC Experimental Group** | Northeast Forestry University
