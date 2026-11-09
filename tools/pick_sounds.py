"""Copies only the recordings Eldenheim plays into build/sounds/:

  - each guard counter's own sounds: the clips of family "700" (a0xx_030700,
    a0xx_032700), the recordings no other action family uses (as
    ErSounds.Own decides at runtime);
  - the guard break's and the parry's (ErSounds.GuardBreakFile, ParryFile).

Reads the sandbox's player_sounds.bin (ERSD v2) and sounds/*.ogg, made by
sandbox/tools/setup.py. A recording not baked there (bake_sounds.py
--no-media, as EldenheimSetup runs it) is converted from the game's sound
banks instead, so only these few are ever written.

    python tools/pick_sounds.py
"""
import shutil
import struct
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "sandbox" / "assets"
OUT = ROOT / "build" / "sounds"
FIXED = {599687853, 842851583}  # guard break, parry


def read_tree(data, at):
    kind = data[at]
    at += 1
    if kind == 0:
        (media,) = struct.unpack_from("<I", data, at)
        return [media], at + 4 + 6 * 4
    (count,) = struct.unpack_from("<H", data, at)
    at += 2
    media = []
    for _ in range(count):
        got, at = read_tree(data, at)
        media += got
    return media, at


def used_media(data):
    assert data[:4] == b"ERSD" and struct.unpack_from("<I", data, 4)[0] == 2, "not an ERSD v2 file"
    (n,) = struct.unpack_from("<I", data, 8)
    at, trees = 12, []
    for _ in range(n):
        media, at = read_tree(data, at)
        trees.append(media)
    (n,) = struct.unpack_from("<I", data, at)
    at += 4
    clips = {}
    for _ in range(n):
        size = data[at]
        name = data[at + 1:at + 1 + size].decode()
        at += 1 + size
        (count,) = struct.unpack_from("<H", data, at)
        at += 2
        clips[name] = [struct.unpack_from("<fI", data, at + 8 * e)[1] for e in range(count)]
        at += 8 * count
    families = defaultdict(set)
    for name, events in clips.items():
        for tree in events:
            if tree < len(trees):
                for media in trees[tree]:
                    families[media].add(name[-3:])
    used = set(FIXED)
    for name, events in clips.items():
        if name.endswith("700"):
            for tree in events:
                if tree < len(trees) and all(families[m] == {"700"} for m in trees[tree]):
                    used.update(trees[tree])
    return used


def main():
    table = ASSETS / "player_sounds.bin"
    if not table.exists():
        sys.exit(f"no {table}: run sandbox/tools/setup.py with the sound banks unpacked first")
    used = used_media(table.read_bytes())
    if OUT.exists():
        shutil.rmtree(OUT)
    OUT.mkdir(parents=True)
    missing = []
    banks = None
    for media in sorted(used):
        src = ASSETS / "sounds" / f"{media}.ogg"
        if src.exists():
            shutil.copy2(src, OUT / src.name)
            continue
        if banks is None:
            sys.path.insert(0, str(ROOT / "sandbox" / "tools"))
            import bake_sounds
            banks = bake_sounds.load_banks()
        ogg, why = bake_sounds.recording(banks, media)
        if ogg is None:
            missing.append(f"{src.name} ({why})")
        else:
            (OUT / src.name).write_bytes(ogg)
    print(f"{OUT}: {len(used) - len(missing)} recordings" + (f", missing {missing}" if missing else ""))


if __name__ == "__main__":
    main()
