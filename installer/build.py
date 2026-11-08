"""Builds dist/EldenheimSetup/EldenheimSetup.exe and zips it to
build/package/EldenheimSetup-<version>.zip.

The exe holds the plugin's DLLs and the tool scripts, never Elden Ring data:
that is made on the player's machine. Needs, on the build machine only:
the .NET SDK, Valheim with BepInEx (for the plugin's references), and
`python -m pip install pyinstaller zstandard`.

    python installer/build.py [VALHEIM_DIR]
"""
import importlib.util
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
STAGE = ROOT / "build" / "installer"
PLUGIN = STAGE / "plugin"
SCRIPTS = sorted((ROOT / "sandbox" / "tools").glob("*.py")) + [
    ROOT / "tools" / n for n in ("bake_unity.py", "export_data.py", "pick_sounds.py")]
PLUGIN_FILES = ["Eldenheim.dll", "Eldenheim.Sim.dll", "manifest.json", "icon.png", "README.md"]

version = re.search(r'BepInPlugin\(Guid, "Eldenheim", "([\d.]+)"\)',
                    (ROOT / "src" / "Eldenheim" / "Plugin.cs").read_text(encoding="utf-8")).group(1)
out = ROOT / "build" / "package" / f"EldenheimSetup-{version}.zip"
if out.exists():
    sys.exit(f"{out} exists: bump the version in Plugin.cs first (old packages are kept)")

# The plugin, built clean; only its own files are taken (the build also copies
# any local game-derived data into the folder).
shutil.rmtree(STAGE, ignore_errors=True)
build_dir = STAGE / "dotnet"
cmd = ["dotnet", "build", str(ROOT / "src" / "Eldenheim"), "-c", "Release", f"-p:PluginDir={build_dir}"]
valheim = sys.argv[1] if len(sys.argv) > 1 else os.environ.get("ValheimDir")
if valheim:
    cmd.append(f"-p:ValheimDir={valheim}")
if subprocess.run(cmd).returncode != 0:
    sys.exit("plugin build failed")
PLUGIN.mkdir(parents=True)
for name in PLUGIN_FILES:
    shutil.copy2(build_dir / name, PLUGIN / name)

# The tool scripts run from data files, so PyInstaller can't see their imports:
# name every non-local module they use.
local = {p.stem for p in SCRIPTS}
mods = set()
for p in SCRIPTS:
    text = p.read_text(encoding="utf-8")
    for m in re.finditer(r"^\s*from\s+([A-Za-z_][\w.]*)\s+import\s+(\w+)", text, re.M):
        if m.group(1).split(".")[0] not in local:  # `from Crypto.Cipher import AES` needs the submodule too
            mods |= {m.group(1), m.group(1) + "." + m.group(2)}
    for m in re.finditer(r"^\s*import\s+([A-Za-z_][\w.]*)", text, re.M):
        if m.group(1).split(".")[0] not in local:
            mods.add(m.group(1))


def is_module(name):
    try:
        return importlib.util.find_spec(name) is not None
    except ModuleNotFoundError:
        return False


# Left out: pycryptodome (only the non-Windows fallback; Windows' own AES is
# used), and Python's OpenSSL bindings (libcrypto/libssl; nothing is downloaded,
# and hashlib falls back to its built-in sha256). A "Crypto" folder in a game
# tool's download also just looks like a coin miner.
EXCLUDE = ["Crypto", "Cryptodome", "ssl", "_ssl", "_hashlib", "cffi", "_cffi_backend"]
mods = {m for m in mods if is_module(m) and m.split(".")[0] not in EXCLUDE}  # drops `from pathlib import Path` style names
hidden = [a for m in sorted(mods) for a in ("--hidden-import", m)]
print("hidden imports:", ", ".join(sorted(mods)))

sep = os.pathsep
data = [
    f"{ROOT / 'sandbox' / 'tools'}{sep}payload/sandbox/tools",
    f"{PLUGIN}{sep}payload/plugin",
] + [f"{ROOT / 'tools' / n}{sep}payload/tools" for n in ("bake_unity.py", "export_data.py", "pick_sounds.py")]
cmd = [sys.executable, "-m", "PyInstaller", "--noconfirm", "--clean", "--windowed", "--onedir",
       "--name", "EldenheimSetup", "--icon", str(ROOT / "src" / "Eldenheim" / "icon.png"),
       "--distpath", str(STAGE / "dist"), "--workpath", str(STAGE / "work"), "--specpath", str(STAGE),
       "--exclude-module", "__pycache__", *hidden]
for m in EXCLUDE:
    cmd += ["--exclude-module", m]
for d in data:
    cmd += ["--add-data", d]
cmd.append(str(ROOT / "installer" / "eldenheim_setup.py"))
if subprocess.run(cmd).returncode != 0:
    sys.exit("PyInstaller failed")

dist = STAGE / "dist" / "EldenheimSetup"
out.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for p in sorted(dist.rglob("*")):
        if p.is_file() and "__pycache__" not in p.parts:
            z.write(p, ("EldenheimSetup" / p.relative_to(dist)).as_posix())
    names = z.namelist()
# Lint: nothing derived from the game may ship.
bad = [n for n in names if re.search(r"\.(bin|ogg|dcx|param|bnk|rs)$|er-files|extracted", n)
       and "packed_codebooks" not in n]
# ...and nothing that reads as crypto to someone skimming the folder.
bad += [n for n in names if re.search(r"crypto|libssl", n, re.I)]
if bad:
    out.unlink()
    sys.exit(f"lint failed, game data or crypto-named files in the zip: {bad}")
print(f"{out} ({out.stat().st_size / 1e6:.1f} MB, {len(names)} files)")
