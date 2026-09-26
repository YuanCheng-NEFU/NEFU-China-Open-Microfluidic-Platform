"""Check the intended public project surface before creating a release."""

import json
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[1]


class PublicDistributionTests(unittest.TestCase):
    def setUp(self):
        self.manifest = json.loads((ROOT / "release_manifest.json").read_text(encoding="utf-8"))
        self.files = set(self.manifest["files"])

    def test_internal_experiment_records_are_not_distributed(self):
        self.assertFalse(any(name.startswith("Examples/Data/") for name in self.files))
        self.assertFalse(any(name.lower().endswith(".avi") for name in self.files))
        self.assertIn("Examples/Data", {item["path"] for item in self.manifest["excluded"]})

    def test_selected_video_has_content(self):
        videos = {name for name in self.files if name.lower().endswith((".mp4", ".mov", ".avi"))}
        self.assertEqual(videos, {"Examples/Video/Droplet_Microscopy.mp4"})
        video = ROOT / next(iter(videos))
        self.assertGreater(video.stat().st_size, 1024 * 1024)
        with video.open("rb") as stream:
            self.assertIn(b"ftyp", stream.read(32))
        self.assertIn("!/Examples/Video/Droplet_Microscopy.mp4",
                      (ROOT / ".gitignore").read_text(encoding="utf-8").splitlines())

    def test_build_and_operation_materials_are_selected(self):
        app = "Software/PC_Control/TotalControl_V7_0/"
        required = {
            app + "NEFU_China_iDEC_V7.sln",
            app + "TotalControl.csproj", app + "Ch297Bridge.csproj",
            app + "src/TotalControlV6.cs", app + "ch297_bridge/Ch297BridgeV63.cs",
            app + "config/total_control_v6.example.json",
            "Software/PYNQ/pynq_v6_perf_server.py", "Hardware/BOM.xlsx",
            "Manual/Build_and_Operation_Manual.pdf", "RELEASING.md", "LICENSE",
        }
        self.assertFalse(required - self.files, required - self.files)
        for name in required:
            self.assertTrue((ROOT / name).is_file(), name)


if __name__ == "__main__":
    unittest.main()
