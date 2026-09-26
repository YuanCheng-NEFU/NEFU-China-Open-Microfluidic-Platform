# Contributing

Contribute focused, reproducible improvements to the platform's assembly, control and experiment workflows.

## Make a change

1. Identify the affected component and the intended operating behavior.
2. Keep device protocols and existing file formats stable, or document an explicit migration.
3. Add or update focused tests.
4. Update the English operating instructions and diagrams when the bench workflow changes.
5. Run the relevant checks in [VALIDATION.md](VALIDATION.md).
6. Describe the change, test command and result in the pull request.

## Device control

Preserve explicit LOW output on the gate-control stop paths. Treat every command that can move a syringe or energize an output as a bench operation with stated preconditions.

Test hardware adapters with mocks before live commissioning. Separate command acknowledgement from observed movement or measured voltage in reports. Keep original acquisitions unchanged and write derived products beside them.

## Documentation

Write direct English instructions with named controls, units and expected observations. Use editable figure sources and update the Word, Markdown and PDF manual editions together.

## Source distribution

Add only deliberate public files to `release_manifest.json`. Keep credentials, local endpoint files, generated binaries, vendor components and raw laboratory recordings outside the archive. Run the release validator and packaging tests before proposing distribution changes.

Submit only material you have permission to contribute and identify any third-party license.
