# NEFU China Open Microfluidic Platform

## Build and Operation Guide

Edition 0.8.1 | September 2026

## 01 One integrated workstation

![System architecture. Camera observation and fluid delivery accompany the photon-counting control chain.](../Hardware/Figures/system_architecture.png)

Total Control V7.0 PERF unites imaging, photon counting, PYNQ gating and syringe-pump control in one Windows workspace. Independent device modules support focused setup and coordinated operation.

### From assembly to operation

Assemble the fluidic and optical paths. Commission the low-voltage gate, then the DEP drive. Connect the device modules, establish the operating settings and start acquisition from the Windows workspace.

The source package includes the C# workstation, x86 CH297 bridge, matching PYNQ PERF service, build projects and offline tests. The Windows package adds the two compiled executables.

## 02 Prepare the bench

| Subsystem | Reference equipment | Connection |
| --- | --- | --- |
| Fluid delivery | LSP02-3B dual syringe pump | RS485 adapter |
| Detection | H10682 and CH297 | PMT coaxial signal; USB serial |
| Observation | MUS40M-G | USB and x64 camera SDK |
| Control | PYNQ-Z2 | Ethernet; PMOD B |
| DEP drive | DG1022Z and ATA-2081 | External trigger and rated HV cable |
| Verification | Oscilloscope and suitable probes | Low-voltage gate and rated HV measurement |

### Prime the fluid path

1. Secure the chip and fit compatible syringes, tubing and connectors. Enter the syringe's correct inner diameter on the pump.
2. Prime each phase without visible air, then inspect every junction for leakage.
3. Start flow from the pump front panel and establish stable droplets under the camera.
4. For reinjection, prefill with compatible fluorinated oil, load the emulsion gently and avoid abrupt pressure changes.

| Reference setting | Starting value |
| --- | --- |
| Aqueous flow | 1.5 uL/min |
| Oil flow | Approximately 17.5 uL/min |
| Droplet size target | Approximately 30 um; measure on the installed chip |
| Incubation example | 30 C for 16 h; use the assay-specific conditions |

Use Hardware/BOM.xlsx for the component register. Record the chip, fluid formulation and syringe dimensions with the experiment. Enable remote pump motion only after checking the selected channel against observed syringe movement.

## 03 Align the optics

![Functional optical layout. Select the emission filter for the installed fluorophore and detector.](../Hardware/Figures/optical_path.png)

1. With the PMT shielded and the laser disabled, secure the excitation, objective, dichroic, emission-filter and detector mounts.
2. Align at the lowest practical illumination using the laboratory laser procedure and suitable eye protection. Focus on the interrogation region.
3. Restore detection gradually. Record a dark trace, blank-flow baseline and fluorescent reference before setting the trigger threshold.

The reference eGFP path uses 488 nm excitation, a DMLP505R dichroic and an FBH520-40 emission component. Match the full filter stack to the installed configuration; the drawing specifies optical functions, not mechanical dimensions.

Protect the PMT from bright illumination. Tune camera exposure and illumination for clear droplet boundaries without saturating the detection channel.

## 04 Wire and verify the outputs

![Main connections. Keep the wet bench, low-voltage logic and high-voltage output physically separated.](../Hardware/Figures/wiring_map.png)

| PYNQ signal | PMOD B position | Purpose |
| --- | --- | --- |
| GATE | Pin 1 | 0 to 3.3 V trigger output |
| RX_MARK | Pin 2 | V7 PERF timing marker |
| Ground | Pin 5 or pin 11 | Shared low-voltage reference |

Keep the amplifier disabled while checking idle LOW, Force LOW and a manual test pulse on the oscilloscope. RX_MARK begins after JSON and parameter processing; its interval to GATE is not the complete detector-to-electrode latency.

The reference DEP carrier is 8 kHz. Commission amplitude gradually within the chip and instrument ratings; 300 to 500 Vpp is a reference operating range, not a universal setting. Use rated probes and guarded connections. Disable and discharge the drive before touching the chip or electrodes.

## 05 Start the V7 platform

1. Install .NET Framework 4.8 or 4.8.1 on 64-bit Windows.
2. Install the camera SDK and driver. Place the vendor CH297 runtime and matching para.ini in the application's runtime/ch297 directory, then complete the supported x86 COM registration.
3. Open Software/PC_Control/TotalControl_V7_0 and run 04_CHECK_ENVIRONMENT.cmd. Resolve the relevant installation actions.
4. Launch 02_RUN_TOTAL_CONTROL_V7_0.cmd. Select the actual detector port, pump settings and board address.

| Project executable | Architecture |
| --- | --- |
| NEFU_China_iDEC_TotalControl_V7_0.exe | x64 workstation |
| runtime/ch297/NEFU_CH297_Bridge_V6_3_x86.exe | x86 acquisition bridge |

### Deploy the matching board service

Copy Software/PYNQ/pynq_v6_perf_server.py to the PYNQ board. Stop any previous listener on port 5000 through its normal shutdown procedure, then start:

```bash
cd /home/xilinx/jupyter_notebooks/NEFU_iDEC
python3 pynq_v6_perf_server.py
```

Use a PYNQ image with BaseOverlay and base.bit. The reference address is 192.168.2.99:5000; configure the installed address. This release uses PYNQ_PERF_V1 samples and PYNQ_ACK_V1 acknowledgements.

### Build from source

```bat
00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd --no-pause
```

The supplied Visual Studio solution builds the same x64 workstation and x86 bridge. Install the vendor SDKs using their supplied setup tools.

## 06 Run from the workspace

![V7.0 PERF workspace in standby mode.](../Manual/images/control_workspace.png)

1. Set the experiment identifier, recording options and actual device endpoints. Connect each module individually.
2. Start the camera and CH297 acquisition. Confirm visible droplets, a continuous count stream and the selected acquisition window.
3. Apply thresholds from the reference traces. Verify GATE with the DEP drive disabled, then enable AUTO under the commissioned operating conditions.
4. Record the run. Use Force LOW before changing connections, then stop acquisition and finalize the session.

The distributed example retains the baseline's 100 ms window, 15000-count threshold, 100-count hysteresis and 2400-baud pump setting. Use the device's actual settings; select a 10 ms acquisition window for the 100 Hz PERF test.

## 07 Monitor acquisition

V7 PERF records each transmitted sample and its acknowledgement. Use the sequence and timing fields to inspect the acquisition path at the operating settings.

| Measure | Read it as |
| --- | --- |
| Sent and acknowledged | Compare totals over the same measured interval |
| Missing | Observed gaps in the sequence high-water mark |
| Duplicates and out of order | Repeated or late sequence numbers |
| RX to GPIO | Board-side measured processing interval |
| Send to ACK RTT | Host-to-board round-trip measurement |
| Median P95 P99 Max | Timing distribution at the selected operating settings |

### Commission the acquisition path

1. Set CH297 to 10 ms. Keep the high-voltage drive disabled during timing checks.
2. Run for 60 seconds and compare acquisition duration with sample totals; nominal 100 Hz corresponds to approximately 6000 slots.
3. Inspect sequence continuity and timing distributions. Save the waveform capture and logs.
4. Extend to 10 and 30 minutes after the short test passes.

| Output | Location |
| --- | --- |
| Source samples and totals | ch297_logs/ |
| Workstation receive stream | sync_logs/ |
| Per-sample ACK records | perf_logs/pynq_ack_<session>.csv |
| Communication summary | perf_logs/communication_summary_<session>.txt |
| Board-side acknowledgements | PYNQ-side pynq_ack_<session>.csv |

AUTO sample timeout is 1 second and client timeout is 3 seconds. Verify physical LOW on timeout and disconnect with an oscilloscope during commissioning.

## 08 Finish the run

![Microfluidic droplet observation. The included MP4 shows the channel under the microscope.](../Manual/images/microfluidic_preview.png)

Open Examples/Video/Droplet_Microscopy.mp4 for a microscopy view of the fluidic channel and droplets.

Before ending acquisition, confirm that the selected recordings have finished writing. Keep the operating settings with the files you choose to retain.

### Finish in a controlled state

1. Set Force LOW, disable the amplifier and discharge the drive as required.
2. Stop the pump and detector acquisition, finish recordings and confirm the output files.
3. Disable the laser, shield the PMT and clean the fluid path using the chip-compatible procedure.
4. Return the workstation and instruments to standby for the next run.

Software is MIT licensed. Documentation and project media follow LICENSE-DOCS. Use the Visual Studio solution to extend the workstation and RELEASING.md to publish a source or Windows release.
