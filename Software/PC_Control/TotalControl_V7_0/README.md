# Total Control V7.0 PERF

A single Windows workspace for camera observation, CH297 photon counting, PYNQ gating and syringe-pump control. Per-sample acknowledgements and sequence statistics make the acquisition path inspectable.

## Run

1. Install .NET Framework 4.8 or 4.8.1 on 64-bit Windows.
2. Install the camera SDK and CH297 runtime described in [WINDOWS_RUNTIME.md](WINDOWS_RUNTIME.md).
3. Run `04_CHECK_ENVIRONMENT.cmd`.
4. Open `02_RUN_TOTAL_CONTROL_V7_0.cmd`. The Windows distribution already includes the two executables.
5. Select the actual COM ports and board address. Connect modules individually before acquisition.

The first-run launcher is `00_FIRST_RUN_BUILD_AND_START.cmd`. For source-only builds, run `00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd --no-pause` or open `NEFU_China_iDEC_V7.sln`.

## PYNQ

Deploy [pynq_v6_perf_server.py](../../PYNQ/pynq_v6_perf_server.py) with the [board guide](../../PYNQ/README.md). The V7 client uses `PYNQ_PERF_V1` samples and `PYNQ_ACK_V1` acknowledgements.

GATE uses PMOD B pin 1. RX_MARK uses pin 2 for oscilloscope timing checks. Keep the DEP amplifier disabled during digital commissioning.

## Acquire and verify

For the 100 Hz performance sequence, set the CH297 gate to 10 ms. Choose the threshold and hysteresis from measured reference traces. Start CH297, confirm current samples, connect PYNQ and enable AUTO only after checking the physical output.

Inspect sent/acknowledged counts, sequence continuity, missing/duplicate/out-of-order counters and timing percentiles. Compare counts with actual elapsed acquisition time. Test at 60 seconds before extending the run.

`Force LOW` stops gate actuation. Stop CH297 and finalize recordings before disconnecting devices. Pump remote operation follows the commissioned channel settings; the release does not unlock additional motion commands.

## Records

| Folder | Evidence |
| --- | --- |
| `ch297_logs/` | Original acquisition and source summaries |
| `sync_logs/` | Bridge-to-workstation samples |
| `perf_logs/` | Per-sample ACKs and communication summaries |
| `recordings/` | Original video, frame index and recording parameters |

The distributed configuration preserves the selected baseline's 100 ms acquisition default, 15000-count threshold, 100-count hysteresis and 2400-baud pump setting. These are starting settings; use the actual device configuration and assay calibration.
