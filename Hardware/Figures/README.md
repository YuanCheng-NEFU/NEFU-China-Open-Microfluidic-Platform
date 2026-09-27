# Engineering Figures

Original, editable engineering schematics for the NEFU-China Open Microfluidic Platform.

[Project overview](project_overview.png) is an additional original figure selected from the team's iDEC presentation, showing the droplet workflow, instrument architecture, optical design and representative measurements.

| Figure | Content |
| --- | --- |
| `system_architecture` | Photon acquisition, PC sample forwarding, PYNQ gate control, DEP actuation, parallel imaging, and independent fluid delivery. |
| `optical_path` | Functional eGFP optical paths: 488 nm excitation, filtered fluorescence detection, and 700 nm observation with camera attenuation. |
| `wiring_map` | Device ports, host connections, RS485 A/B mapping, PMOD B gate and signal ground, waveform output, and the separate high-voltage connection. |
| `workflow` | Droplet generation, incubation at 30 degrees Celsius for 16 hours, reinjection, detection, gate computation, sorting, and session recording. |

Each figure is supplied as a scalable SVG and a 1600 x 950 PNG. Place figures at the full text width of the manual, approximately 6.3 to 6.5 inches. Use SVG for scalable web and print layouts, and PNG for document editors.

The figures describe signal, fluid, and optical relationships. Their geometry is schematic and is not a dimensional construction drawing. Equipment names identify the platform connections and appear as plain text.

## Rebuild

Use Python 3 with Pillow and either Arial or DejaVu Sans installed:

```sh
python Hardware/Figures/build_figures.py
```

`build_figures.py` generates both file formats from the same coordinates and text. Teal identifies acquisition and control connections; amber identifies fluid delivery or high-voltage actuation. The optical figure uses an explicit legend for excitation, fluorescence, and observation.

## Suggested Captions

1. **System architecture.** Photon counts pass from the H10682 PMT through CH297 and the host PC to PYNQ-Z2. PYNQ drives the external trigger of DG1022Z; ATA-2081 supplies the DEP electrodes. Imaging and syringe-driven fluid delivery run alongside this control path.
2. **Optical paths for eGFP detection.** Excitation is directed to the channel through the objective, with DMLP505R used for the excitation split. Fluorescence returns through the objective and a matched emission filter to the PMT. The reference component set includes FBH520-40; an additional 535 nm element is configuration-dependent. Match the complete installed filter set to the detector before alignment. The 700 nm observation branch reaches the camera through an ND filter.
3. **Electrical connections.** CH297 and the camera connect to the host by USB. The pump uses a USB-RS485 adapter with A-to-A and B-to-B wiring. Ethernet connects the host to PYNQ; PMOD B Pin 1 supplies the 0-3.3 V gate, with a separate signal-ground connection to the waveform generator.
4. **Operating sequence.** Generate water-in-oil droplets, incubate at 30 degrees Celsius for 16 hours, reinject into the sorting chip, detect fluorescence, compute the gate, actuate DEP sorting, and retain a session archive.
