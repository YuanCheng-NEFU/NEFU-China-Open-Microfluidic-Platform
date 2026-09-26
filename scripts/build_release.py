"""Build a source distribution from an explicit release allowlist."""

from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat
import sys
from typing import Any
import zipfile


PROJECT = "NEFU-China-Open-Microfluidic-Platform"
MANIFEST_NAME = "release_manifest.json"
CONTENTS_NAME = "RELEASE_CONTENTS.json"
BLOCKED_PARTS = frozenset({
    ".git", ".build", "__pycache__", ".venv", "venv", "node_modules",
    ".ssh", ".aws", ".azure", "logs", "recordings", "runs", "runtime",
    "dist", "bin", "obj",
})
BLOCKED_SUFFIXES = frozenset({
    ".dll", ".exe", ".lib", ".ocx", ".msi", ".pdb", ".so", ".dylib",
    ".pem", ".pfx", ".p12", ".key", ".jks", ".keystore", ".log",
    ".jsonl", ".pyc", ".pyo", ".dmp", ".bak", ".avi", ".mp4", ".mov",
    ".zip", ".7z", ".rar",
})
BLOCKED_NAMES = frozenset({
    "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "credentials",
    "credentials.json", "secrets.json", "secrets.yaml", "secrets.yml",
})
WINDOWS_DEVICE_NAMES = frozenset({
    "con", "prn", "aux", "nul", *(f"com{number}" for number in range(1, 10)),
    *(f"lpt{number}" for number in range(1, 10)),
})
CHUNK_SIZE = 1024 * 1024
RUNTIME_GUIDES = frozenset({
    "Software/PC_Control/TotalControl_V7_0/runtime/ch297/README.md",
    "Software/PC_Control/TotalControl_V7_0/runtime/camera/README.md",
    "Software/PC_Control/FourInOne_R2/runtime/ch297/README.md",
    "Software/PC_Control/FourInOne_R2/runtime/camera/README.md",
})
PUBLIC_MEDIA = frozenset({"Examples/Video/Droplet_Microscopy.mp4"})


class ReleaseError(ValueError):
    """A release manifest or output destination is unsafe or invalid."""


@dataclass(frozen=True)
class ReleaseFile:
    name: str
    path: Path
    size: int
    sha256: str

    def record(self) -> dict[str, Any]:
        return {"path": self.name, "size": self.size, "sha256": self.sha256}


@dataclass(frozen=True)
class ReleaseResult:
    version: str
    status: str
    files: tuple[ReleaseFile, ...]
    pending_review: tuple[dict[str, Any], ...]
    archive: Path | None = None
    checksum: Path | None = None


def validate_relative_path(value: Any) -> PurePosixPath:
    if not isinstance(value, str) or not value:
        raise ReleaseError("File paths must be nonempty POSIX relative strings.")
    path = PurePosixPath(value)
    if (
        path.is_absolute()
        or "\\" in value
        or ":" in value
        or any(char in value for char in '<>"|?*')
        or any(ord(char) < 32 for char in value)
        or any(part in {".", "..", ""} for part in value.split("/"))
        or str(path) != value
        or any(part.endswith((".", " ")) for part in path.parts)
        or any(part.split(".", 1)[0].casefold() in WINDOWS_DEVICE_NAMES for part in path.parts)
    ):
        raise ReleaseError(f"Unsafe file path: {value!r}")
    return path


def validate_selected_name(name: str) -> None:
    path = validate_relative_path(name)
    if name in RUNTIME_GUIDES or name in PUBLIC_MEDIA:
        return
    parts = tuple(part.casefold() for part in path.parts)
    filename = parts[-1]
    if any(part in BLOCKED_PARTS for part in parts) or any(
        part.endswith("_logs") for part in parts[:-1]
    ):
        raise ReleaseError(f"Generated, private, or runtime path is forbidden: {name}")
    if (
        path.suffix.casefold() in BLOCKED_SUFFIXES
        or filename in BLOCKED_NAMES
        or filename == ".env"
        or filename.startswith(".env.")
        or parts[-2:] == ("config", "total_control_v6.json")
    ):
        raise ReleaseError(f"Binary, credential, or runtime file is forbidden: {name}")


def checked_source(root: Path, name: str) -> Path:
    relative = validate_relative_path(name)
    current = root
    for part in relative.parts:
        current = current / part
        try:
            info = current.lstat()
        except FileNotFoundError as exc:
            raise ReleaseError(f"Selected file or parent is missing: {name}") from exc
        if stat.S_ISLNK(info.st_mode) or (
            getattr(info, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        ):
            raise ReleaseError(f"Symbolic links and reparse points are forbidden: {name}")
    if not current.is_file():
        raise ReleaseError(f"Selected path is not a regular file: {name}")
    if not current.resolve().is_relative_to(root):
        raise ReleaseError(f"Selected path escapes the project: {name}")
    return current


def hash_file(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        while chunk := stream.read(CHUNK_SIZE):
            size += len(chunk)
            digest.update(chunk)
    return size, digest.hexdigest()


def load_manifest(root: Path) -> tuple[dict[str, Any], bytes]:
    manifest_path = checked_source(root, MANIFEST_NAME)
    raw = manifest_path.read_bytes()
    try:
        manifest = json.loads(raw.decode("utf-8-sig"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise ReleaseError(f"Invalid UTF-8 JSON manifest: {exc}") from exc
    if not isinstance(manifest, dict):
        raise ReleaseError("The manifest must be a JSON object.")
    version = manifest.get("version")
    if not isinstance(version, str) or not re.fullmatch(
        r"[0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?", version
    ):
        raise ReleaseError("Manifest version must be a safe version such as 0.7.0.")
    if not isinstance(manifest.get("status"), str) or not manifest["status"].strip():
        raise ReleaseError("Manifest status must be a nonempty string.")
    if not isinstance(manifest.get("files"), list) or not manifest["files"]:
        raise ReleaseError("Manifest files must be a nonempty explicit file list.")
    pending = manifest.get("pending_review")
    if not isinstance(pending, list):
        raise ReleaseError("Manifest pending_review must be a list.")
    review_ids: set[str] = set()
    for item in pending:
        if not isinstance(item, dict) or any(
            not isinstance(item.get(field), str) or not item[field].strip()
            for field in ("id", "detail")
        ):
            raise ReleaseError("Every pending review needs an id and detail.")
        if item["id"] in review_ids:
            raise ReleaseError(f"Duplicate pending review id: {item['id']}")
        review_ids.add(item["id"])
        if not isinstance(item.get("paths"), list):
            raise ReleaseError("Every pending review needs a paths list.")
        for name in item["paths"]:
            validate_relative_path(name)
    excluded = manifest.get("excluded")
    if not isinstance(excluded, list) or any(
        not isinstance(item, dict)
        or any(not isinstance(item.get(key), str) or not item[key].strip()
               for key in ("path", "reason"))
        for item in excluded
    ):
        raise ReleaseError("Manifest excluded must list paths and reasons.")
    return manifest, raw


def zip_info(name: str) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    info.create_system = 3
    info.external_attr = (stat.S_IFREG | 0o644) << 16
    return info


def build_release(
    project_root: Path,
    output_dir: Path | None = None,
    *,
    check_only: bool = False,
) -> ReleaseResult:
    root = Path(project_root).resolve(strict=True)
    manifest, manifest_bytes = load_manifest(root)
    names = [MANIFEST_NAME]
    seen = {MANIFEST_NAME.casefold(), CONTENTS_NAME.casefold()}
    for name in manifest["files"]:
        validate_selected_name(name)
        if name.casefold() in seen:
            raise ReleaseError(f"Duplicate or reserved archive path: {name}")
        seen.add(name.casefold())
        names.append(name)

    for item in manifest["excluded"]:
        excluded_path = item["path"].removesuffix("/")
        excluded_name = str(validate_relative_path(excluded_path)).casefold()
        for name in (*names, CONTENTS_NAME):
            if name.casefold() == excluded_name or name.casefold().startswith(excluded_name + "/"):
                raise ReleaseError(f"Excluded path overlaps an archive entry: {item['path']} -> {name}")

    entries: list[ReleaseFile] = []
    for name in sorted(names):
        source = checked_source(root, name)
        if name == MANIFEST_NAME:
            size = len(manifest_bytes)
            sha256 = hashlib.sha256(manifest_bytes).hexdigest()
        else:
            size, sha256 = hash_file(source)
        entries.append(ReleaseFile(name, source, size, sha256))

    version = manifest["version"]
    status = manifest["status"]
    pending = tuple(manifest["pending_review"])
    if check_only:
        return ReleaseResult(version, status, tuple(entries), pending)

    destination = Path(output_dir) if output_dir is not None else root / "dist"
    destination = destination.resolve()
    prefix = f"{PROJECT}-{version}"
    archive_path = destination / f"{prefix}.zip"
    checksum_path = destination / f"{archive_path.name}.sha256"
    selected_paths = {entry.path.resolve() for entry in entries}
    for target in (destination, archive_path, checksum_path):
        if target in selected_paths:
            raise ReleaseError(f"Output collides with a selected source file: {target}")
    for target in (archive_path, checksum_path):
        if target.exists() or target.is_symlink():
            raise ReleaseError(f"Refusing to overwrite an existing output: {target}")
    if destination.exists() and not destination.is_dir():
        raise ReleaseError(f"Output directory is not a directory: {destination}")
    destination.mkdir(parents=True, exist_ok=True)

    contents = {
        "schema_version": 1,
        "project": PROJECT,
        "version": version,
        "status": status,
        "review_required": bool(pending),
        "pending_review": list(pending),
        "files": [entry.record() for entry in entries],
    }
    contents_bytes = (json.dumps(contents, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
    created_outputs: list[Path] = []
    try:
        # Exclusive creation also prevents a concurrent build from replacing outputs.
        with archive_path.open("xb") as archive_stream:
            created_outputs.append(archive_path)
            with zipfile.ZipFile(archive_stream, "w", compression=zipfile.ZIP_DEFLATED) as archive:
                for entry in entries:
                    source = checked_source(root, entry.name)
                    if entry.name == MANIFEST_NAME:
                        if source.read_bytes() != manifest_bytes:
                            raise ReleaseError("The release manifest changed during packaging.")
                        archive.writestr(zip_info(f"{prefix}/{entry.name}"), manifest_bytes)
                        continue
                    digest = hashlib.sha256()
                    size = 0
                    with source.open("rb") as incoming, archive.open(
                        zip_info(f"{prefix}/{entry.name}"), "w", force_zip64=True
                    ) as outgoing:
                        while chunk := incoming.read(CHUNK_SIZE):
                            outgoing.write(chunk)
                            digest.update(chunk)
                            size += len(chunk)
                    if size != entry.size or digest.hexdigest() != entry.sha256:
                        raise ReleaseError(f"Selected file changed during packaging: {entry.name}")
                archive.writestr(zip_info(f"{prefix}/{CONTENTS_NAME}"), contents_bytes)
        _, archive_hash = hash_file(archive_path)
        with checksum_path.open("x", encoding="ascii", newline="\n") as stream:
            created_outputs.append(checksum_path)
            stream.write(f"{archive_hash}  {archive_path.name}\n")
    except BaseException:
        for target in reversed(created_outputs):
            target.unlink(missing_ok=True)
        raise
    return ReleaseResult(version, status, tuple(entries), pending, archive_path, checksum_path)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="Output directory (default: project-root/dist).")
    parser.add_argument("--check-only", action="store_true", help="Validate without creating an archive.")
    args = parser.parse_args(argv)
    try:
        result = build_release(
            Path(__file__).resolve().parent.parent,
            args.output,
            check_only=args.check_only,
        )
    except (ReleaseError, OSError, zipfile.BadZipFile) as exc:
        print(f"Release validation failed: {exc}", file=sys.stderr)
        return 1
    print(f"Validated {len(result.files)} source files for {result.version} ({result.status}).")
    if result.pending_review:
        print(f"LOCAL REVIEW CANDIDATE: {len(result.pending_review)} pending review item(s).")
        print("Review is incomplete. This archive is not a completed public release.")
        for item in result.pending_review:
            print(f"  [{item['id']}] {item['detail']}")
    else:
        print("Local validation complete. No publication has been performed.")
    if result.archive is not None:
        print(f"Archive: {result.archive}")
        print(f"SHA-256: {result.checksum}")
    else:
        print("Check only: no output files were created.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
