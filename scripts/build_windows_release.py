"""Build a Windows distribution with source and two project-owned executables."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import zipfile

from build_release import (CONTENTS_NAME, PROJECT, ReleaseError, checked_source,
                           hash_file, build_release, zip_info)

APP = "Software/PC_Control/TotalControl_V7_0"
BINARY_SPECS = {
    f"{APP}/NEFU_China_iDEC_TotalControl_V7_0.exe": 0x8664,
    f"{APP}/runtime/ch297/NEFU_CH297_Bridge_V6_3_x86.exe": 0x14C,
}


def validate_pe(path: Path, expected_machine: int) -> None:
    """Check the architecture of a managed executable without loading it."""
    data = path.read_bytes()
    try:
        if data[:2] != b"MZ":
            raise ValueError("DOS header")
        pe = struct.unpack_from("<I", data, 0x3C)[0]
        if data[pe:pe + 4] != b"PE\0\0":
            raise ValueError("PE signature")
        machine, sections = struct.unpack_from("<HH", data, pe + 4)
        optional_size = struct.unpack_from("<H", data, pe + 20)[0]
        optional = pe + 24
        magic = struct.unpack_from("<H", data, optional)[0]
        directory = optional + {0x10B: 96, 0x20B: 112}[magic]
        clr_rva, clr_size = struct.unpack_from("<II", data, directory + 14 * 8)
        if machine != expected_machine or not clr_rva or clr_size < 20:
            raise ValueError("architecture or managed header")
        clr_offset = None
        for index in range(sections):
            section = optional + optional_size + index * 40
            virtual_size, rva, raw_size, raw_offset = struct.unpack_from("<IIII", data, section + 8)
            if rva <= clr_rva < rva + max(virtual_size, raw_size):
                clr_offset = raw_offset + clr_rva - rva
                break
        if clr_offset is None:
            raise ValueError("managed header section")
        flags = struct.unpack_from("<I", data, clr_offset + 16)[0]
        if expected_machine == 0x14C and not flags & 2:
            raise ValueError("bridge must require 32-bit execution")
    except (ValueError, KeyError, struct.error) as exc:
        raise ReleaseError(f"Invalid managed PE architecture: {path.name} ({exc})") from exc


def package_windows(root: Path, output: Path | None = None) -> Path:
    root = root.resolve(strict=True)
    source = build_release(root, check_only=True)
    if source.pending_review:
        raise ReleaseError("Resolve selected-source review items before packaging Windows binaries.")
    entries = [(entry.name, entry.path, entry.size, entry.sha256) for entry in source.files]
    binary_records = []
    for name, machine in BINARY_SPECS.items():
        path = checked_source(root, name)
        validate_pe(path, machine)
        size, digest = hash_file(path)
        entries.append((name, path, size, digest))
        binary_records.append({"path": name, "architecture": "x64" if machine == 0x8664 else "x86"})
    prefix = f"{PROJECT}-{source.version}-windows-x64"
    destination = (output if output is not None else root / "dist").resolve()
    archive_path = destination / (prefix + ".zip")
    checksum_path = destination / (prefix + ".zip.sha256")
    selected_paths = {path.resolve() for _, path, _, _ in entries}
    for target in (destination, archive_path, checksum_path):
        if target in selected_paths:
            raise ReleaseError(f"Output collides with an input: {target}")
    for target in (archive_path, checksum_path):
        if target.exists() or target.is_symlink():
            raise ReleaseError(f"Refusing to overwrite an existing output: {target}")
    destination.mkdir(parents=True, exist_ok=True)
    inventory = {
        "schema_version": 1,
        "project": PROJECT,
        "version": source.version,
        "distribution": "windows-x64",
        "entry_point": f"{APP}/02_RUN_TOTAL_CONTROL_V7_0.cmd",
        "dependency_check": f"{APP}/04_CHECK_ENVIRONMENT.cmd",
        "project_binaries": binary_records,
        "external_dependencies": [
            {"component": "Camera", "files": ["OEApi64.dll"],
             "installation": "Install the matching vendor camera SDK and driver."},
            {"component": "CH297", "files": ["PMTCount.dll", "para.ini"],
             "installation": "Install the x86 COM component and detector-specific calibration in runtime/ch297."},
        ],
        "files": [{"path": name, "size": size, "sha256": digest}
                  for name, _, size, digest in sorted(entries)],
    }
    created = []
    try:
        with archive_path.open("xb") as stream:
            created.append(archive_path)
            with zipfile.ZipFile(stream, "w") as archive:
                for name, path, size, digest in sorted(entries):
                    data = checked_source(root, name).read_bytes()
                    if len(data) != size or hashlib.sha256(data).hexdigest() != digest:
                        raise ReleaseError(f"Input changed during packaging: {name}")
                    archive.writestr(zip_info(f"{prefix}/{name}"), data)
                archive.writestr(zip_info(f"{prefix}/{CONTENTS_NAME}"),
                                 json.dumps(inventory, indent=2) + "\n")
        _, digest = hash_file(archive_path)
        with checksum_path.open("x", encoding="ascii", newline="\n") as stream:
            created.append(checksum_path)
            stream.write(f"{digest}  {archive_path.name}\n")
    except BaseException:
        for path in reversed(created):
            path.unlink(missing_ok=True)
        raise
    return archive_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    if os.name != "nt":
        parser.error("Build the Windows distribution on Windows with .NET Framework installed.")
    try:
        application = root / APP
        subprocess.run([os.environ.get("COMSPEC", "cmd.exe"), "/d", "/c",
                        str(application / "00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd"), "--no-pause"],
                       cwd=application, check=True)
        subprocess.run([sys.executable, str(application / "tests/test_projects.py")],
                       cwd=application, check=True)
        archive = package_windows(root, args.output)
    except (ReleaseError, OSError, subprocess.CalledProcessError) as exc:
        print(f"Windows packaging failed: {exc}", file=sys.stderr)
        return 1
    print(f"Windows archive: {archive}")
    print("Includes the V7 workstation, x86 bridge and selected source distribution.")
    print("Install the vendor runtime dependencies before connecting camera or CH297 hardware.")
    print("No files have been uploaded.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
