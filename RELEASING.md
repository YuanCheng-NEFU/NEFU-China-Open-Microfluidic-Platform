# Publish the Project

## Prepare

The Windows package includes all project source. From its extracted project root, create a repository-ready source archive:

```bash
python scripts/build_release.py
```

Extract the generated `dist/NEFU-China-Open-Microfluidic-Platform-0.8.3.zip` into a new directory. You can also use the separately supplied source archive.

From the project root, run:

```bash
python -m unittest discover -s tests -v
python Software/PC_Control/TotalControl_V7_0/tests/test_projects.py
python -m unittest discover -s Software/PYNQ/tests -p test_perf_server.py -v
python scripts/check_distribution.py
python scripts/build_release.py --check-only
```

## Create the Repository

Create a public GitHub repository named `NEFU-China-Open-Microfluidic-Platform`. Leave automatic README and license creation disabled; both are included in the project.

In the extracted source directory, replace `YOUR-ACCOUNT` below and run:

```bash
git init
git add .
git commit -m "Release V7.0 PERF open platform"
git branch -M main
git remote add origin https://github.com/YOUR-ACCOUNT/NEFU-China-Open-Microfluidic-Platform.git
git push -u origin main
git tag v0.8.3
git push origin v0.8.3
```

## Publish the Release

Create a GitHub release from tag `v0.8.3`, titled **NEFU-China Open Microfluidic Platform 0.8.3**. Use [RELEASE_NOTES.md](RELEASE_NOTES.md) as the release description and attach:

| Asset | Use |
| --- | --- |
| `NEFU-China-Open-Microfluidic-Platform-0.8.3-windows-x64.zip` | Complete project with prebuilt Windows applications |
| `NEFU-China-Open-Microfluidic-Platform-0.8.3.zip` | Source distribution |
| Matching `.sha256` files | Download checksums |

The Windows package is the main download. It includes the guide, hardware resources, selected video and all project source; no additional example-data download is required.

Open the public README, PDF guide and MP4, then download and extract the Windows archive to check the published release.

## Build Future Packages

```bash
python scripts/build_release.py
python scripts/build_windows_release.py
```

Package builders use `release_manifest.json` to select public files. Vendor SDKs are installed separately under their own licenses; credentials and laboratory runtime files stay outside the release.
