"""Validate the English source distribution and local Markdown links."""

from pathlib import Path
import json
import re
import sys
from urllib.parse import unquote
import zipfile
from xml.etree import ElementTree

ROOT = Path(__file__).resolve().parents[1]
TEXT_SUFFIXES = {".md", ".txt", ".py", ".cs", ".json", ".csv", ".cmd",
                 ".ps1", ".sh", ".ini", ".svg", ".ipynb", ".gitignore",
                 ".csproj", ".sln", ".html"}
CJK = re.compile(r"[\u3400-\u4dbf\u4e00-\u9fff]")
LINK = re.compile(r"!?" + r"\[[^\]]*\]\(([^)]+)\)")


def main():
    manifest = json.loads((ROOT / "release_manifest.json").read_text("utf-8"))
    selected = set(manifest["files"]) | {"release_manifest.json"}
    issues = []
    for name in sorted(selected):
        path = ROOT / name
        if not path.is_file():
            issues.append(f"Missing selected file: {name}")
            continue
        if CJK.search(name):
            issues.append(f"Non-English filename: {name}")
        if path.suffix in TEXT_SUFFIXES or path.name.startswith("LICENSE"):
            value = path.read_text("utf-8-sig")
            if CJK.search(value):
                issues.append(f"CJK text in {name}")
            if path.suffix == ".md":
                for target in LINK.findall(value):
                    target = unquote(target.split("#", 1)[0].strip("<>"))
                    if not target or re.match(r"[a-zA-Z]+:", target):
                        continue
                    resolved = (path.parent / target).resolve()
                    try:
                        relative = resolved.relative_to(ROOT).as_posix()
                    except ValueError:
                        issues.append(f"Link escapes source root: {name} -> {target}")
                        continue
                    has_selected_contents = any(
                        item.startswith(relative.rstrip("/") + "/") for item in selected
                    )
                    if relative not in selected and not has_selected_contents:
                        issues.append(f"Unselected link target: {name} -> {target}")
        if path.suffix in {".docx", ".xlsx"}:
            with zipfile.ZipFile(path) as archive:
                for member in archive.namelist():
                    if not member.endswith(".xml"):
                        continue
                    element = ElementTree.fromstring(archive.read(member))
                    # Check visible text, not localized names in the font theme.
                    values = [node.text or "" for node in element.iter()
                              if node.tag.rsplit("}", 1)[-1] == "t"]
                    if any(CJK.search(text) for text in values):
                        issues.append(f"CJK visible text in {name}:{member}")
    for issue in issues:
        print(issue, file=sys.stderr)
    if issues:
        return 1
    print(f"English text and local links checked across {len(selected)} files.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
