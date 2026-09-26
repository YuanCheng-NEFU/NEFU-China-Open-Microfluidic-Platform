# Engineering Checks

## Offline checks

From the project root:

```bash
python -m unittest discover -s tests -v
python Software/PC_Control/TotalControl_V7_0/tests/test_projects.py
python -m unittest discover -s Software/PYNQ/tests -p test_perf_server.py -v
python scripts/check_distribution.py
python scripts/build_release.py --check-only
```

On Windows:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Software/PC_Control/TotalControl_V7_0/tests/test_runtime_preflight.ps1
```

Build both Release and Debug configurations using the supplied solution. The application targets x64 and the acquisition bridge x86.

## Commissioning

With the high-voltage drive disabled, verify idle LOW, forced LOW, manual pulse timing, invalid-input handling, disconnect and shutdown on an oscilloscope.

For the PERF sequence, set CH297 to 10 ms and compare sent and acknowledged sample totals over the measured acquisition interval. Record sequence gaps, duplicates, out-of-order values and timing percentiles. Begin with 60 seconds, then extend to 10 and 30 minutes.

RX_MARK is asserted after JSON and parameter processing. Measure its timing to GATE as that defined interval, not as the complete detector-to-electrode latency.

Complete pump calibration and verify device-driver operation before integrated runs.
