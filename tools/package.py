"""Gale package: builds the plugin into a clean folder and zips that folder's
contents, so manifest.json (name, author, version, description), icon.png and
README.md sit at the zip's root, as Gale's "Import local mod" reads them.

No config is included: settings start at their defaults. The zip holds the data
generated from your own copy of Elden Ring, so don't share it.

    python tools/package.py [VALHEIM_DIR]

VALHEIM_DIR (default: the ValheimDir environment variable, else the build's
own default) is only where the build finds Valheim's and BepInEx's DLLs.
Writes build/package/Gargantua1866-Eldenheim-<version>.zip and never
overwrites an existing one.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

version = re.search(r'BepInPlugin\(Guid, "Eldenheim", "([\d.]+)"\)',
                    (ROOT / "src" / "Eldenheim" / "Plugin.cs").read_text(encoding="utf-8")).group(1)
out = ROOT / "build" / "package" / f"Gargantua1866-Eldenheim-{version}.zip"
if out.exists():
    sys.exit(f"{out} exists: bump the version in Plugin.cs first (old packages are kept)")

stage = ROOT / "build" / f"stage-{version}"
shutil.rmtree(stage, ignore_errors=True)
cmd = ["dotnet", "build", str(ROOT / "src" / "Eldenheim"), "-c", "Release", f"-p:PluginDir={stage}"]
valheim = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("ValheimDir")
if valheim:
    cmd.append(f"-p:ValheimDir={valheim}")
if subprocess.run(cmd).returncode != 0:
    sys.exit("build failed")

need = ["manifest.json", "icon.png", "README.md", "Eldenheim.dll", "Eldenheim.Sim.dll",
        "eldenheim_data.bin", "eldenheim_anims_all.bin"]
missing = [n for n in need if not (stage / n).exists()]
if missing:
    sys.exit(f"missing from the build: {missing} (see README, Setup)")

out.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for p in sorted(stage.rglob("*")):
        if p.is_file():
            z.write(p, p.relative_to(stage).as_posix())

# Lint: only our files, nothing of Valheim's or BepInEx's, no config, logs or test output.
with zipfile.ZipFile(out) as z:
    names = z.namelist()
    bad = [n for n in names if re.search(r"assembly_|UnityEngine|0Harmony|BepInEx\.dll|\.cfg$|\.log$|commands\.txt|demo_trace", n)]
    if bad or z.testzip() is not None:
        out.unlink()
        sys.exit(f"lint failed: {bad}")
    manifest = json.loads(z.read("manifest.json"))
print(f"{out} ({out.stat().st_size / 1e6:.1f} MB, {len(names)} files; "
      f"{manifest['author']}-{manifest['name']} {manifest['version_number']})")
