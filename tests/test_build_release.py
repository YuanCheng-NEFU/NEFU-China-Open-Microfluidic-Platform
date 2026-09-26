"""Offline regression tests for the release allowlist and archive integrity."""

from __future__ import annotations

from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile


SCRIPT_PATH = Path(__file__).resolve().parents[1] / "scripts" / "build_release.py"
SPEC = importlib.util.spec_from_file_location("build_release", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
release = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = release
SPEC.loader.exec_module(release)


class ReleaseBuilderTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.manifest = {
            "version": "0.6.0-rc1",
            "status": "local-review-candidate",
            "files": ["README.md", "Software/control.py", "Manual/guide.pdf", "overlay/PYNQ.bit"],
            "pending_review": [{"id": "manual", "detail": "Review the guide.", "paths": ["Manual/guide.pdf"]}],
            "excluded": [{"path": "vendor.dll", "reason": "Vendor redistribution is not authorized."}],
        }
        for name, payload in {
            "README.md": b"Project\n",
            "Software/control.py": b"print('offline')\n",
            "Manual/guide.pdf": b"%PDF-local-review-fixture\n",
            "overlay/PYNQ.bit": b"\x00\x01\x02\xff",
            "vendor.dll": b"do not publish",
            "logs/session.log": b"private runtime output",
            ".git/config": b"private repository data",
        }.items():
            self.write_file(name, payload)
        self.write_manifest()

    def write_file(self, name: str, payload: bytes) -> Path:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payload)
        return path

    def write_manifest(self) -> None:
        (self.root / release.MANIFEST_NAME).write_text(
            json.dumps(self.manifest, indent=2), encoding="utf-8"
        )

    def test_archive_contains_only_selected_files_and_correct_hashes(self) -> None:
        result = release.build_release(self.root)
        prefix = f"{release.PROJECT}-0.6.0-rc1"
        self.assertEqual(result.archive.name, f"{prefix}.zip")
        expected = set(self.manifest["files"]) | {release.MANIFEST_NAME, release.CONTENTS_NAME}
        with zipfile.ZipFile(result.archive) as archive:
            self.assertEqual(set(archive.namelist()), {f"{prefix}/{name}" for name in expected})
            self.assertIsNone(archive.testzip())
            contents = json.loads(archive.read(f"{prefix}/{release.CONTENTS_NAME}"))
            self.assertEqual(contents["version"], "0.6.0-rc1")
            self.assertEqual(contents["status"], "local-review-candidate")
            self.assertTrue(contents["review_required"])
            self.assertEqual(contents["pending_review"], self.manifest["pending_review"])
            self.assertEqual({row["path"] for row in contents["files"]}, expected - {release.CONTENTS_NAME})
            for row in contents["files"]:
                payload = archive.read(f"{prefix}/{row['path']}")
                self.assertEqual(row["size"], len(payload))
                self.assertEqual(row["sha256"], hashlib.sha256(payload).hexdigest())
                self.assertEqual(payload, (self.root / row["path"]).read_bytes())
        archive_hash = hashlib.sha256(result.archive.read_bytes()).hexdigest()
        self.assertEqual(result.checksum.read_text(), f"{archive_hash}  {result.archive.name}\n")

    def test_check_only_does_not_create_output(self) -> None:
        result = release.build_release(self.root, self.root / "custom", check_only=True)
        self.assertIsNone(result.archive)
        self.assertFalse((self.root / "custom").exists())
        self.assertFalse((self.root / "dist").exists())

    def test_missing_file_is_rejected_before_output(self) -> None:
        self.manifest["files"].append("missing.py")
        self.write_manifest()
        with self.assertRaisesRegex(release.ReleaseError, "missing"):
            release.build_release(self.root)
        self.assertFalse((self.root / "dist").exists())

    def test_unsafe_and_duplicate_paths_are_rejected(self) -> None:
        for name in ("../outside.txt", "/outside.txt", "C:/outside.txt", "Software/../../outside.txt",
                     "Software\\control.py", "README.md:secret", "./README.md", "Software//control.py",
                     "CON", "nul.txt", "Software/*.py", "README.md", "readme.md",
                     release.MANIFEST_NAME, release.CONTENTS_NAME):
            with self.subTest(name=name):
                self.manifest["files"] = ["README.md", name]
                self.write_manifest()
                with self.assertRaises(release.ReleaseError):
                    release.build_release(self.root, check_only=True)

    def test_forbidden_material_cannot_be_allowlisted(self) -> None:
        for name in ("vendor.dll", "vendor.EXE", "vendor.lib", "vendor.ocx", "vendor.msi", "private.pem",
                     "private.pfx", "private.key", ".env", "secrets.json", "id_ed25519", "run.log",
                     "events.jsonl", ".git/config", ".build/build.py", "__pycache__/module.pyc",
                     "recordings/frame.csv", "logs/session.csv", "runs/metadata.json",
                     "pump_logs/session.csv", "nested/PYNQ_LOGS/metadata.json",
                     "config/total_control_v6.json", "Software/config/total_control_v6.json",
                     "settings.bak", "recording.avi", "recording.mp4", "recording.mov",
                     "old-release.zip", "installer.7z", "installer.rar"):
            with self.subTest(name=name):
                self.write_file(name, b"should not be published")
                self.manifest["files"] = [name]
                self.write_manifest()
                with self.assertRaisesRegex(release.ReleaseError, "forbidden"):
                    release.build_release(self.root, check_only=True)

    def test_example_config_is_allowed(self) -> None:
        self.write_file("config/total_control_v6.example.json", b"{}\n")
        self.manifest["files"].append("config/total_control_v6.example.json")
        self.write_manifest()
        result = release.build_release(self.root, check_only=True)
        self.assertIn("config/total_control_v6.example.json", {entry.name for entry in result.files})

    def test_only_selected_public_microscopy_video_is_allowed(self) -> None:
        name = "Examples/Video/Droplet_Microscopy.mp4"
        self.write_file(name, b"selected microscopy fixture")
        self.manifest["files"].append(name)
        self.write_manifest()
        result = release.build_release(self.root, check_only=True)
        self.assertIn(name, {entry.name for entry in result.files})
        for other in ("Examples/Video/other.mp4", "recordings/Droplet_Microscopy.mp4"):
            with self.subTest(name=other), self.assertRaises(release.ReleaseError):
                release.validate_selected_name(other)

    def test_excluded_paths_cannot_overlap_archive_entries(self) -> None:
        for name in ("README.md", "readme.md", "Software", "Software/", "software/",
                     "Software/control.py", release.MANIFEST_NAME, release.CONTENTS_NAME):
            with self.subTest(name=name):
                self.manifest["excluded"] = [{"path": name, "reason": "Intended exclusion."}]
                self.write_manifest()
                with self.assertRaisesRegex(release.ReleaseError, "Excluded path overlaps"):
                    release.build_release(self.root, check_only=True)

    def test_excluded_paths_match_whole_path_components(self) -> None:
        self.manifest["excluded"] = [
            {"path": "Soft/", "reason": "A different directory."},
            {"path": "Software/control.py.bak", "reason": "A different file."},
        ]
        self.write_manifest()
        result = release.build_release(self.root, check_only=True)
        self.assertIn("Software/control.py", {entry.name for entry in result.files})

    def test_directories_are_not_file_entries(self) -> None:
        self.manifest["files"] = ["Software"]
        self.write_manifest()
        with self.assertRaisesRegex(release.ReleaseError, "regular file"):
            release.build_release(self.root, check_only=True)

    def test_symlink_file_is_rejected(self) -> None:
        link = self.root / "linked.md"
        try:
            link.symlink_to(self.root / "README.md")
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"Symbolic link creation is unavailable: {exc}")
        self.manifest["files"] = ["linked.md"]
        self.write_manifest()
        with self.assertRaisesRegex(release.ReleaseError, "links"):
            release.build_release(self.root, check_only=True)

    def test_symlink_parent_is_rejected(self) -> None:
        link = self.root / "linked"
        try:
            link.symlink_to(self.root / "Software", target_is_directory=True)
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"Symbolic link creation is unavailable: {exc}")
        self.manifest["files"] = ["linked/control.py"]
        self.write_manifest()
        with self.assertRaisesRegex(release.ReleaseError, "links"):
            release.build_release(self.root, check_only=True)

    def test_link_detection_does_not_require_link_creation_privileges(self) -> None:
        original_lstat = Path.lstat
        for name, mode in (("README.md", stat.S_IFLNK), ("Software", stat.S_IFLNK)):
            with self.subTest(name=name):
                target = self.root / name

                def link_stat(path: Path, *args, **kwargs):
                    if path == target:
                        return SimpleNamespace(st_mode=mode)
                    return original_lstat(path, *args, **kwargs)

                with patch.object(Path, "lstat", link_stat):
                    with self.assertRaisesRegex(release.ReleaseError, "links"):
                        release.build_release(self.root, check_only=True)

    @unittest.skipUnless(os.name == "nt", "Windows directory junction check")
    def test_windows_junction_parent_is_rejected(self) -> None:
        link = self.root / "junction"
        created = subprocess.run(
            ["cmd", "/c", "mklink", "/J", str(link), str(self.root / "Software")],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
        )
        self.assertEqual(created.returncode, 0, created.stderr)
        self.addCleanup(link.rmdir)
        self.manifest["files"] = ["junction/control.py"]
        self.write_manifest()
        with self.assertRaisesRegex(release.ReleaseError, "reparse points"):
            release.build_release(self.root, check_only=True)

    def test_existing_outputs_are_never_overwritten(self) -> None:
        result = release.build_release(self.root)
        archive_bytes = result.archive.read_bytes()
        checksum_bytes = result.checksum.read_bytes()
        with self.assertRaisesRegex(release.ReleaseError, "overwrite"):
            release.build_release(self.root)
        self.assertEqual(result.archive.read_bytes(), archive_bytes)
        self.assertEqual(result.checksum.read_bytes(), checksum_bytes)

    def test_existing_checksum_prevents_archive_creation(self) -> None:
        output = self.root / "dist"
        output.mkdir()
        archive = output / f"{release.PROJECT}-0.6.0-rc1.zip"
        checksum = output / f"{archive.name}.sha256"
        checksum.write_text("preserve this", encoding="ascii")
        with self.assertRaisesRegex(release.ReleaseError, "overwrite"):
            release.build_release(self.root)
        self.assertFalse(archive.exists())
        self.assertEqual(checksum.read_text(), "preserve this")

    def test_output_cannot_be_a_selected_source_file(self) -> None:
        with self.assertRaisesRegex(release.ReleaseError, "collides"):
            release.build_release(self.root, self.root / "README.md")
        self.assertEqual((self.root / "README.md").read_bytes(), b"Project\n")

    def test_archives_are_reproducible(self) -> None:
        first = release.build_release(self.root, self.root / "first")
        second = release.build_release(self.root, self.root / "second")
        self.assertEqual(first.archive.read_bytes(), second.archive.read_bytes())

    def test_changed_source_removes_partial_archive(self) -> None:
        original_check = release.checked_source
        call_count = 0

        def mutate_before_archiving(root: Path, name: str) -> Path:
            nonlocal call_count
            if name == "Software/control.py":
                call_count += 1
                if call_count == 2:
                    self.write_file(name, b"changed after validation")
            return original_check(root, name)

        with patch.object(release, "checked_source", side_effect=mutate_before_archiving):
            with self.assertRaisesRegex(release.ReleaseError, "changed during packaging"):
                release.build_release(self.root)
        self.assertEqual(list((self.root / "dist").iterdir()), [])

    def test_cli_discloses_pending_review(self) -> None:
        output = io.StringIO()
        with patch.object(release, "__file__", str(self.root / "scripts" / "build_release.py")):
            with redirect_stdout(output):
                self.assertEqual(release.main(["--check-only"]), 0)
        self.assertIn("LOCAL REVIEW CANDIDATE", output.getvalue())
        self.assertIn("not a completed public release", output.getvalue())
        self.assertIn("[manual] Review the guide.", output.getvalue())


if __name__ == "__main__":
    unittest.main()
