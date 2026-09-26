import hashlib
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
import build_windows_release as release


def pe_bytes(machine):
    data = bytearray(1024)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    optional_size = 224 if machine == 0x14C else 240
    struct.pack_into("<HH", data, 0x84, machine, 1)
    struct.pack_into("<H", data, 0x94, optional_size)
    struct.pack_into("<H", data, 0x98, 0x10B if machine == 0x14C else 0x20B)
    directory = 0x98 + (96 if machine == 0x14C else 112)
    struct.pack_into("<II", data, directory + 14 * 8, 0x2000, 72)
    section = 0x98 + optional_size
    struct.pack_into("<IIII", data, section + 8, 512, 0x2000, 512, 512)
    struct.pack_into("<I", data, 528, 3 if machine == 0x14C else 1)
    return bytes(data)


class WindowsReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "README.md").write_text("English source\n", encoding="utf-8")
        (self.root / "release_manifest.json").write_text(json.dumps({
            "version": "0.7.1", "status": "source-distribution", "files": ["README.md"],
            "pending_review": [], "excluded": []}), encoding="utf-8")
        for name, machine in release.BINARY_SPECS.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(pe_bytes(machine))

    def test_only_two_owned_binaries_with_hashes(self):
        vendor = self.root / release.APP / "runtime/ch297/PMTCount.dll"
        vendor.write_bytes(b"not selected")
        archive = release.package_windows(self.root)
        with zipfile.ZipFile(archive) as z:
            prefix = z.namelist()[0].split("/")[0] + "/"
            manifest = json.loads(z.read(prefix + "RELEASE_CONTENTS.json"))
            self.assertEqual(len(manifest["project_binaries"]), 2)
            self.assertFalse(any(name.endswith(".dll") for name in z.namelist()))
            for record in manifest["files"]:
                data = z.read(prefix + record["path"])
                self.assertEqual(len(data), record["size"])
                self.assertEqual(hashlib.sha256(data).hexdigest(), record["sha256"])
        checksum = archive.with_name(archive.name + ".sha256").read_text().split()[0]
        self.assertEqual(hashlib.sha256(archive.read_bytes()).hexdigest(), checksum)

    def test_missing_binary_fails(self):
        (self.root / next(iter(release.BINARY_SPECS))).unlink()
        with self.assertRaises(release.ReleaseError):
            release.package_windows(self.root)

    def test_wrong_architecture_fails(self):
        (self.root / next(iter(release.BINARY_SPECS))).write_bytes(pe_bytes(0x14C))
        with self.assertRaises(release.ReleaseError):
            release.package_windows(self.root)

    def test_bridge_must_require_x86(self):
        name = next(n for n, machine in release.BINARY_SPECS.items() if machine == 0x14C)
        data = bytearray(pe_bytes(0x14C))
        struct.pack_into("<I", data, 528, 1)
        (self.root / name).write_bytes(data)
        with self.assertRaises(release.ReleaseError):
            release.package_windows(self.root)

    def test_existing_archive_is_preserved(self):
        archive = release.package_windows(self.root)
        before = archive.read_bytes()
        with self.assertRaises(release.ReleaseError):
            release.package_windows(self.root)
        self.assertEqual(archive.read_bytes(), before)

    def test_existing_checksum_is_preserved(self):
        output = self.root / "dist"
        output.mkdir()
        checksum = output / (release.PROJECT + "-0.7.1-windows-x64.zip.sha256")
        checksum.write_text("keep")
        with self.assertRaises(release.ReleaseError):
            release.package_windows(self.root)
        self.assertEqual(checksum.read_text(), "keep")
        self.assertFalse(list(output.glob("*.zip")))

    def test_byte_reproducible_archives(self):
        first = release.package_windows(self.root, self.root / "first")
        second = release.package_windows(self.root, self.root / "second")
        self.assertEqual(first.read_bytes(), second.read_bytes())

    def test_invalid_pe_header_fails(self):
        (self.root / next(iter(release.BINARY_SPECS))).write_bytes(b"not an executable")
        with self.assertRaises(release.ReleaseError):
            release.package_windows(self.root)

    def test_runtime_guide_exception_is_exact(self):
        import build_release as source
        for name in source.RUNTIME_GUIDES:
            source.validate_selected_name(name)
        for name in [f"{release.APP}/runtime/ch297/PMTCount.dll",
                     f"{release.APP}/runtime/ch297/para.ini",
                     f"{release.APP}/runtime/camera/other.md"]:
            with self.assertRaises(release.ReleaseError):
                source.validate_selected_name(name)


if __name__ == "__main__":
    unittest.main()
