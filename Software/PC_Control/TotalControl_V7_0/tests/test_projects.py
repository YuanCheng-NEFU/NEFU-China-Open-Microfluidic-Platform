"""Offline checks for the V7 solution and runtime inspection contract."""

from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
NS = {"ms": "http://schemas.microsoft.com/developer/msbuild/2003"}
MAIN_SOURCES = {
    "src/CameraWorkstationV4.cs",
    "src/Lsp02PumpController.cs",
    "src/PynqV6Client.cs",
    "src/V6AppSupport.cs",
    "src/V6WorkspaceControls.cs",
    "src/V6CameraPmtModules.cs",
    "src/V6PynqModule.cs",
    "src/V6PumpModule.cs",
    "src/TotalControlV6.cs",
}


class ProjectTests(unittest.TestCase):
    def project(self, name):
        return ET.parse(ROOT / name).getroot()

    def value(self, project, name):
        return project.find("ms:PropertyGroup/ms:" + name, NS).text

    def test_total_control_contract(self):
        project = self.project("TotalControl.csproj")
        self.assertEqual(self.value(project, "TargetFrameworkVersion"), "v4.8")
        self.assertEqual(self.value(project, "PlatformTarget"), "x64")
        self.assertEqual(self.value(project, "StartupObject"), "V6Program")
        self.assertEqual(self.value(project, "AssemblyName"),
                         "NEFU_China_iDEC_TotalControl_V7_0")
        self.assertEqual(self.value(project, "OutputType"), "WinExe")

    def test_total_control_source_list(self):
        project = self.project("TotalControl.csproj")
        actual = {node.attrib["Include"].replace("\\", "/")
                  for node in project.findall("ms:ItemGroup/ms:Compile", NS)}
        self.assertEqual(actual, MAIN_SOURCES)
        for path in actual:
            self.assertTrue((ROOT / path).is_file(), path)

    def test_bridge_contract(self):
        project = self.project("Ch297Bridge.csproj")
        self.assertEqual(self.value(project, "TargetFrameworkVersion"), "v4.8")
        self.assertEqual(self.value(project, "PlatformTarget"), "x86")
        self.assertEqual(self.value(project, "StartupObject"), "Ch297BridgeProgram")
        self.assertEqual(self.value(project, "OutputPath"), "runtime\\ch297\\")
        sources = project.findall("ms:ItemGroup/ms:Compile", NS)
        self.assertEqual(len(sources), 1)
        self.assertEqual(sources[0].attrib["Include"], "ch297_bridge\\Ch297BridgeV63.cs")
        self.assertTrue((ROOT / sources[0].attrib["Include"].replace("\\", "/")).is_file())

    def test_solution_contains_exact_two_projects(self):
        solution = (ROOT / "NEFU_China_iDEC_V7.sln").read_text(encoding="utf-8")
        names = re.findall(r'^Project\("[^"]+"\) = "([^"]+)"', solution, re.MULTILINE)
        self.assertEqual(names, ["TotalControl", "Ch297Bridge"])
        for config in ("Debug", "Release"):
            self.assertIn(config + "|Mixed Platforms = " + config + "|Mixed Platforms", solution)
        for name, platform in (("TotalControl.csproj", "x64"), ("Ch297Bridge.csproj", "x86")):
            guid = self.value(self.project(name), "ProjectGuid")
            for config in ("Debug", "Release"):
                self.assertIn(guid + "." + config + "|Mixed Platforms.Build.0 = "
                              + config + "|" + platform, solution)

    def test_project_and_command_script_sources_match(self):
        script = (ROOT / "00_BUILD_TOTAL_CONTROL_V7_0_PERF.cmd").read_text(encoding="utf-8-sig")
        actual = {"src/" + name for name in re.findall(r'%ROOT%\\src\\([^"\r\n]+\.cs)', script)}
        self.assertEqual(actual, MAIN_SOURCES)

    def test_runtime_checker_targets_v7_only(self):
        checker = (ROOT / "tools/Check-Runtime.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("NEFU_China_iDEC_TotalControl_V7_0.exe", checker)
        self.assertIn("NEFU_CH297_Bridge_V6_3_x86.exe", checker)
        self.assertNotIn("FourInOne_R2.exe", checker)
        self.assertNotIn("ProtocolSelfTest.exe", checker)
        self.assertNotIn("TriggerTrace.exe", checker)

    def test_runtime_checker_does_not_activate_or_connect(self):
        checker = (ROOT / "tools/Check-Runtime.ps1").read_text(encoding="utf-8-sig")
        for operation in ("-ComObject", "Activator", "TcpClient", "SerialPort",
                          "Start-Process", "regsvr32", "Set-ItemProperty",
                          "CreateSubKey", "LoadLibrary", "Assembly]::Load"):
            self.assertNotIn(operation, checker)
        self.assertIn("Registry32", checker)
        self.assertIn("InprocServer32", checker)
        self.assertIn("NEFU_OSCAM_DIR", checker)
        self.assertIn("AllPrerequisitesPresent", checker)
        self.assertIn("exit 2", checker)


if __name__ == "__main__":
    unittest.main()

