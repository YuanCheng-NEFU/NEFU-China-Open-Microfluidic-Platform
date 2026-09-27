# Microfluidic Platform Build Guide

Edition 0.8.2 covers assembly and operation with the existing V7.0 PERF workstation.

- [PDF](Build_Guide.pdf)
- [Editable Word](Build_Guide.docx)
- [Markdown](Build_Guide.md)

Follow the staged instructions for parts selection, mechanical and fluidic assembly, optics, electrical connections, Windows installation, PYNQ deployment, device setup and the first integrated run. Troubleshooting and shutdown procedures complete the guide. Equipment specifications remain in Hardware and Software.

To regenerate the Word, Markdown and PDF editions, install Python 3.10+, python-docx, Pillow and reportlab, then run `python scripts/build_manual.py`. On Windows with Word installed, `scripts/export_manual.ps1` provides an alternative PDF export. Review the exported pages after changes.
