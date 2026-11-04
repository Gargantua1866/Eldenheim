"""Private test package: Eldenheim plus the Elden Ring data generated from this
PC's own game files, as a Gale/r2modman "import local mod" zip.

For installing on the user's own second machine only. It contains data derived
from Elden Ring: never share, upload or commit it.

    python tools/package_private.py      (after dotnet build -c Release)

Writes build/package/Gargantua-Eldenheim-<version>-PRIVATE.zip.
"""
import json
import re
import struct
import sys
import zipfile
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SANDBOX = ROOT / "sandbox"
BIN = ROOT / "src" / "Eldenheim" / "bin" / "Release"
BUILD = ROOT / "build"
CONFIG = Path(r"C:\Games\Valheim\BepInEx\config\com.gargantua.eldenheim.cfg")

version = re.search(r'BepInPlugin\(Guid, "Eldenheim", "([\d.]+)"\)',
                    (ROOT / "src" / "Eldenheim" / "Plugin.cs").read_text(encoding="utf-8")).group(1)

# (path in zip, source). plugins/ lands in BepInEx/plugins/Gargantua-Eldenheim/, config/ in BepInEx/config/.
files = [
    ("plugins/Eldenheim.dll", BIN / "Eldenheim.dll"),
    ("plugins/Eldenheim.Sim.dll", BIN / "Eldenheim.Sim.dll"),
    ("plugins/eldenheim_data.bin", BUILD / "eldenheim_data.bin"),
    ("plugins/eldenheim_anims_all.bin", BUILD / "eldenheim_anims_all.bin"),
    ("plugins/player_sounds.bin", SANDBOX / "assets" / "player_sounds.bin"),
]
files += [(f"plugins/sounds/{p.name}", p) for p in sorted((SANDBOX / "assets" / "sounds").glob("*.ogg"))]
if CONFIG.exists():
    files.append(("config/com.gargantua.eldenheim.cfg", CONFIG))

missing = [str(src) for _, src in files if not src.exists()]
if missing:
    sys.exit("missing: " + ", ".join(missing))


def png(size, rgb):
    """A plain square PNG (Gale wants a 256x256 icon)."""
    raw = b"".join(b"\0" + bytes(rgb) * size for _ in range(size))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


manifest = {
    "name": "Eldenheim",
    "version_number": version,
    "website_url": "",
    "description": "PRIVATE test build with Elden Ring-derived data. Do not share.",
    "dependencies": ["denikson-BepInExPack_Valheim-5.4.2351"],
}
readme = f"""# Eldenheim {version} (PRIVATE test build)

Contains data generated from Elden Ring's game files on the author's own PC.
For the author's own machines only: do not share, upload or redistribute.

Install: Gale (or r2modman) -> Import local mod -> this zip, in a profile with
BepInExPack_Valheim. Both players need this same build; a player with other
data shows the other with Valheim's animation and logs it.
"""

out = BUILD / "package" / f"Gargantua-Eldenheim-{version}-PRIVATE.zip"
out.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("manifest.json", json.dumps(manifest, indent=2))
    z.writestr("README.md", readme)
    z.writestr("icon.png", png(256, (150, 120, 60)))
    for name, src in files:
        z.write(src, name)

# Lint: only our files, nothing from the game itself, no logs or test output.
with zipfile.ZipFile(out) as z:
    names = z.namelist()
    bad = [n for n in names if re.search(r"assembly_|UnityEngine|0Harmony|BepInEx\.dll|\.log$|shots/|demo_trace", n)]
    if bad or z.testzip() is not None:
        sys.exit(f"lint failed: {bad}")
print(f"{out} ({out.stat().st_size / 1e6:.1f} MB, {len(names)} files)")
