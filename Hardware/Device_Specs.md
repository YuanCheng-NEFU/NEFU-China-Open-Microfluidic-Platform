# Equipment Specifications

This reference collects the equipment parameters used to assemble and configure the platform. Match each component's model label and supplier documentation to the installed system.

## Supplier Documents

| Equipment | Integration document |
| --- | --- |
| Hamamatsu H10682 | H10682 / H17712 series data sheet |
| CH297 | Software manual V1.0.01, DLL manual V1.0.00, `PMTCount.dll` and example V1.0.39 |
| PYNQ-Z2 | TUL board specification and user manual |
| LSP02-3B | User manual, communication protocol, and LSP control software |
| MUS40M-G | Camera manual, OsCam manual, and SDK |
| DG1022Z | RIGOL DG1000Z series data sheet |
| ATA-2081 | Amplifier specification and operating manual |

Obtain vendor drivers and SDKs directly from the equipment suppliers. Keep the installed SDK versions with the experiment configuration.

## H10682 Photon-Counting Head

The H10682 integrates a metal-package PMT, photon-counting circuit, and high-voltage supply in a compact +5 V head.

| Parameter | H10682-110 | H10682-210 |
| --- | --- | --- |
| Effective area | 8 mm diameter | 8 mm diameter |
| Spectral response | 380-700 nm | 230-700 nm |
| Photocathode | Super bialkali | Ultra bialkali |
| Window | Borosilicate glass | UV glass |
| Dark counts, typical / maximum | 50 / 100 s^-1 | 600 / 1000 s^-1 |

| Shared parameter | Value |
| --- | --- |
| Supply | +4.75 to +5.25 V; absolute maximum +6 V |
| Maximum current | 40 mA |
| Count linearity | 5 x 10^6 s^-1 at 10% count loss |
| Pulse-pair resolution | 20 ns |
| Output pulse width | 10 ns |
| Output | Positive logic; typical +2.2 V into 50 ohm |
| Overlight output | +4 V under overlight; 0 V normally |
| Operating temperature | +5 to +40 deg C |
| Mass | 47 g |
| Signal lead | RG-174/U coaxial cable; connector according to the installed head |

Both suffixes cover the visible emission bands used for eGFP and mCherry. Match the head suffix and correction parameters to the installed detector. Shield the head before applying power, and protect the optical window from mechanical loading.

## CH297 Photon Counter

| Parameter | Value |
| --- | --- |
| Acquisition gate | 10-655350 ms, in multiples of 10 ms |
| Default serial settings | `19200,n,8,1` |
| Configurable acquisition | Gate, measurement duration, stabilization time, pulse-pair resolution, repeat mode, automatic saving |
| Vendor export | Excel or `.zat` text |

Place the vendor `PMTCount.dll` and its `para.ini` in the directory required by the supplied driver package. Match the detector correction parameter `P` to the installed PMT and counter. Retain the detector calibration record when reporting calibrated absolute counts.

| API member | Function |
| --- | --- |
| `ComPort` / `PortSetting` | Serial port and settings |
| `StartCountinueCount(msGatedTime)` | Start continuous acquisition; 0 success, -1 communication failure, -4 gate-setting failure |
| `GetRLU()` | Corrected count at values >=0; -1 checksum error; -2 no count received |
| `SingleCount(msGatedTime)` / `RepeatSingleCount()` | Single measurement and repeat |
| `GetPMTName()` | Read counter model |
| `SetPulsePara(nsPulse)` / `GetPulsePara()` | Set or read pulse-pair resolution in ns |

For timing-sensitive acquisition, use a restrained display update rate and disable the vendor software's dot-matrix view when appropriate.

## PYNQ-Z2

| Parameter | Value |
| --- | --- |
| Device | Zynq XC7Z020-1CLG400C; dual-core Cortex-A9 at 650 MHz |
| Memory | 512 MB DDR3; 16 MB Quad-SPI flash; microSD slot |
| Programmable logic | Approximately 13,300 logic slices, 630 KB block RAM, 220 DSP slices |
| Network | Gigabit Ethernet PHY |
| Expansion | Two Pmod connectors, Arduino and Raspberry Pi interfaces |
| Controls | Four push buttons, two switches, four LEDs, two RGB LEDs |
| Power | USB or 7-15 V external input |
| Dimensions | 87 x 140 mm |

The platform gate uses PMOD B Pin 1 at 0-3.3 V. LED0 mirrors the gate state for a local visual indication. Verify the output on an oscilloscope during commissioning.

## LSP02-3B Syringe Pump

| Parameter | Value |
| --- | --- |
| Modes | Infusion, withdrawal, infusion then withdrawal, withdrawal then infusion, continuous |
| Flow range | 0.764 nL/min to 52.95 mL/min |
| Dispensed volume | 1 nL to 99.99 mL |
| Syringe capacity | 1-60 mL syringes; 5-1000 uL microsyringes; one or two installed |
| Maximum stroke / resolution | 140 mm / 0.156 um |
| Stroke accuracy | Within +/-0.5% for stroke >=30% of maximum |
| Rated linear force | >90 N |
| Communication | RS485 |
| Supply | AC 90-260 V; 70 W |

The communication connector uses pin 3 for signal ground, pin 4 for RS485(B), and pin 5 for RS485(A). Device address 1 is independent of channel selection. The pump stops on a detected stall; resolve the obstruction before restarting. Use this equipment for laboratory research.

## DG1022Z Function Generator

| Parameter | Value |
| --- | --- |
| Channels | Two equivalent channels |
| Maximum frequency | Sine 25 MHz; square 25 MHz; pulse 15 MHz |
| Sampling / vertical resolution | 200 MSa/s / 14 bit |
| Output amplitude into 50 ohm | 1 mVpp to 10 Vpp at <=10 MHz |
| Output impedance | 50 ohm typical |
| Offset range | +/-5 Vpk, AC + DC |
| External trigger | TTL compatible; selectable edge; pulse width >100 ns |
| Burst trigger response | <300 ns typical |
| Burst cycles | 1-1,000,000 or infinite |
| Internal burst period | 1 us to 500 s |
| Interfaces | USB host/device and LAN |

Use PYNQ as the external gate/trigger source and configure an 8 kHz sinusoidal carrier for the reference DEP workflow. Verify the selected trigger mode and output waveform before connecting the amplifier.

## ATA-2081 High-Voltage Amplifier

| Parameter | Value |
| --- | --- |
| Channels | One |
| Maximum output | 800 Vpp (+/-400 V peak) |
| Maximum current | 20 mA peak at DC-50 Hz; 40 mA peak above 50 Hz |
| Bandwidth, -3 dB | DC-200 kHz |
| Slew rate | >=356 V/us |
| Gain | x0 to x120; coarse step 1, fine step 0.1 |
| Input | BNC; 50 ohm / 5 kohm selectable; 0-10 Vpp |
| Output | 4 mm banana terminals; 100 ohm / 5 kohm output resistance |
| Voltage monitor | 10 mV/V, BNC |
| Current monitor | 20 V/A, BNC |
| Maximum output power | 16 W peak |
| Harmonic distortion | <=0.1% at 1 kHz and 100 Vpp |
| Voltage error | Within +/-3% full scale at 1 kHz |
| Supply | AC 220 V +/-10%, 50 Hz |
| Dimensions | 366 x 164 x 369 mm |

The reference drive is 300-500 Vpp at 8 kHz. With a measured 10 Vpp input, gain x30-x50 gives that nominal output range. Confirm the waveform and amplitude using the voltage monitor: its output is one hundredth of the amplifier output. Use the monitor's documented load and grounding conditions.

## MUS40M-G Camera

The MUS40M-G is a monochrome, global-shutter scientific camera. OsCam and the vendor SDK provide exposure, gain, frame-rate, ROI, and external-trigger controls. Set exposure to resolve droplet motion, then adjust illumination and gain while maintaining visible channel boundaries and electrode landmarks.

## Optical Base Procurement

The historical quotation dated 2026-04-21 contains 55 listed line items, 95 units, and a total of CNY 22,851. [BOM.xlsx](BOM.xlsx) preserves every item and calculates line totals from quantity and unit price. The Optical Components sheet adds the cage, filter, adapter, and enclosure items used in the platform.
