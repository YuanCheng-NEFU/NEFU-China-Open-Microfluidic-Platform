# Build and Operation Guide

Edition 0.8.1 documents the V7.0 PERF workstation.

- [PDF](Build_and_Operation_Manual.pdf)
- [Editable Word](Build_and_Operation_Manual.docx)
- [Markdown](Build_and_Operation_Manual.md)

The guide covers assembly, wiring, startup, acquisition and shutdown in a short illustrated sequence. Detailed specifications remain in Hardware and Software.

To regenerate the Word, Markdown and PDF editions, install Python 3.10+, python-docx, Pillow and reportlab, then run `python scripts/build_manual.py`. On Windows with Word installed, `scripts/export_manual.ps1` provides an alternative PDF export. Review the exported pages after changes.
