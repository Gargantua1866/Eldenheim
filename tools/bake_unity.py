"""Bakes Elden Ring player animations for Eldenheim: per frame, the
character-space rotation of every bone of c0000's Skeleton.hkx, plus every
bone's character-space position, at 30 fps.

Unlike the sandbox's player_anims.bin (positions only), this keeps each
bone's full rotation, so the mod can retarget twist as well as direction.

The game's model space is +X left, +Y up, -Z forward. Unity's character
space is +X right, +Y up, +Z forward: the same handedness turned 180 degrees
about Y, so positions become (-x, y, -z) and rotations (-x, y, -z, w).

Usage:
  python tools/bake_unity.py out.bin a000_000000 a000_020000 ...   (version 1, every bone, debug)
  python tools/bake_unity.py --all out.bin                          (version 2, what the mod loads)

--all bakes every clip the sandbox itself plays (the names in its
assets/player_anims.bin), in the lean version 2 layout below.

Reads the game files through the sandbox's readers: ER_SANDBOX points at the
sandbox (default: sandbox/ in this repository), and
ER_FILES / ER_GAME_DIR work as they do there.

File layout (little-endian):
  "EHAN" u32 version=1 u32 bones u32 clips
  bones x { u8 len, name, i16 parent }
  rest:   bones x { f32 px,py,pz, qx,qy,qz,qw }       (character space)
  clips x { u8 len, name, f32 blend, u32 frames,
            frames x bones x { f32 px,py,pz, qx,qy,qz,qw } }
"""
import os
import struct
import sys
from pathlib import Path, PureWindowsPath

HERE = Path(__file__).resolve().parent
SANDBOX = Path(os.environ.get("ER_SANDBOX", HERE.parent / "sandbox"))
sys.path.insert(0, str(SANDBOX / "tools"))

import hkanim  # noqa: E402
import paths  # noqa: E402
from bake_anims import PACKS  # noqa: E402
from erfmt import open_bnd  # noqa: E402
from extract import Source, blend_frames  # noqa: E402
from skel import fk, qmul, qrot  # noqa: E402

VERSION = 1

# Version 2 keeps only what the retarget reads: these bones' rotations
# (int16-quantised), and positions for POSITIONED.
LEAN = [
    "RootPos", "Pelvis", "Spine", "Spine1", "Spine2", "Neck", "Head",
    "L_Clavicle", "L_UpperArm", "L_Forearm", "L_Hand", "R_Clavicle", "R_UpperArm", "R_Forearm", "R_Hand",
    "L_Thigh", "L_Calf", "L_Foot", "L_Toe0", "R_Thigh", "R_Calf", "R_Foot", "R_Toe0",
    "L_Weapon", "R_Weapon",
    *[side + f for side in ("L_", "R_") for f in (
        "Finger0", "Finger01", "Finger02", "Finger1", "Finger11", "Finger12", "Finger2", "Finger21", "Finger22",
        "Finger3", "Finger31", "Finger32", "Finger4", "Finger41", "Finger42")],
]
POSITIONED = ["RootPos", "Pelvis", "L_Weapon", "R_Weapon", "L_Foot", "R_Foot", "L_Hand", "R_Hand", "Head"]


def to_unity(pos, rot):
    return (-pos[0], pos[1], -pos[2]), (-rot[0], rot[1], -rot[2], rot[3])


def sandbox_clip_names():
    """Every clip in the sandbox's own bake, in its order."""
    data = (SANDBOX / "assets" / "player_anims.bin").read_bytes()
    assert data[:4] == b"ERAN"
    joints, oriented, clips = struct.unpack_from("<III", data, 4)
    at = 16
    for _ in range(joints + oriented):
        at += 1 + data[at]
    stride = (joints * 3 + oriented * 9) * 4
    names = []
    for _ in range(clips):
        n = data[at]
        names.append(data[at + 1 : at + 1 + n].decode())
        at += 1 + n
        _blend, frames = struct.unpack_from("<fI", data, at)
        at += 8 + frames * stride
    return names


def main():
    if len(sys.argv) >= 3 and sys.argv[1] == "--all":
        return main_lean(Path(sys.argv[2]))
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    out = Path(sys.argv[1])
    wanted = sys.argv[2:]

    src = Source()
    skeleton = next(d for _, n, d in src.files if n.endswith("Skeleton.hkx"))
    names, parents, rest = hkanim.skeleton(skeleton)
    hkx = dict(src.hkx)
    for pack in PACKS:
        for _, n, d in open_bnd(paths.er_files() / "chr" / f"{pack}.anibnd.dcx"):
            if n.endswith(".hkx"):
                hkx.setdefault(PureWindowsPath(n).stem, d)

    def pose(local):
        return [to_unity(p, q) for p, q in fk(parents, local)]

    clips = []
    for name in wanted:
        # "a023_030000": TAE file "a23", animation 30000, as the sandbox names them.
        file, anim_id = "a%02d" % int(name[1:4]), int(name[5:])
        src.hkx = hkx
        source = src.hkx_name(file, anim_id)
        if source is None:
            print("  ! no HKX for", name)
            continue
        anim = hkanim.Animation(hkx[source])
        blend = blend_frames(src, file, anim_id)
        frames = []
        for frame in range(anim.frames_at_30fps() + 1):
            local = list(rest)
            for bone, transform in zip(anim.bones, anim.sample_seconds(frame / 30.0)):
                local[bone] = transform
            frames.append(pose(local))
        clips.append((name, blend, frames))
        print("  %s: %d frames (from %s)" % (name, len(frames), source))

    def write_pose(f, p):
        for (px, py, pz), (qx, qy, qz, qw) in p:
            f.write(struct.pack("<7f", px, py, pz, qx, qy, qz, qw))

    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "wb") as f:
        f.write(b"EHAN")
        f.write(struct.pack("<III", VERSION, len(names), len(clips)))
        for name, parent in zip(names, parents):
            raw = name.encode()
            f.write(struct.pack("<B", len(raw)) + raw + struct.pack("<h", parent))
        write_pose(f, pose(rest))
        for name, blend, frames in clips:
            raw = name.encode()
            f.write(struct.pack("<B", len(raw)) + raw)
            f.write(struct.pack("<fI", blend, len(frames)))
            for p in frames:
                write_pose(f, p)
    print("wrote", out, out.stat().st_size, "bytes,", len(clips), "clips,", len(names), "bones")


def q16(v):
    return max(-32767, min(32767, int(round(v * 32767.0))))


def despike(poses, bones):
    """Smooths single frames where a bone snaps away and straight back (a
    clip's forearm straightening for one frame: a166_030505 at 9, 92 degrees
    out and 126 back). The next frame must be nearer the last one than the
    bad frame is to either. Returns how many frames it changed."""
    # |dot| of two unit quaternions: cos(half the angle). 45 degrees apart: 0.924.
    far = 0.924
    changed = 0
    for i in range(1, len(poses) - 1):
        before, now, after = poses[i - 1], poses[i], poses[i + 1]
        for b in bones:
            q0, q1, q2 = before[b][1], now[b][1], after[b][1]
            d01 = abs(sum(x * y for x, y in zip(q0, q1)))
            if d01 >= far:
                continue
            d12 = abs(sum(x * y for x, y in zip(q1, q2)))
            d02 = sum(x * y for x, y in zip(q0, q2))
            if d12 >= far or abs(d02) <= max(d01, d12):
                continue
            sign = 1.0 if d02 >= 0.0 else -1.0
            mid = [x + sign * y for x, y in zip(q0, q2)]
            n = sum(v * v for v in mid) ** 0.5
            now[b] = (now[b][0], tuple(v / n for v in mid))
            changed += 1
    return changed


def arm_chains(names, parents):
    """Each arm's bones, hand to clavicle. Its twist bones, if any, come along."""
    chains = []
    for side in ("L_", "R_"):
        chain, bone = [], names.index(side + "Hand")
        while True:
            chain.append(bone)
            if names[bone] == side + "Clavicle":
                break
            bone = parents[bone]
        chains.append(chain)
    return chains


def despike_arms(poses, chains):
    """Smooths single frames where a hand jumps away and straight back (over
    10 cm, landing back within 60% of that) though no one bone snaps far: the
    bones compensate for each other, so the whole arm is smoothed together.
    Returns how many frames it changed."""

    def reach(local, chain):
        at = (0.0, 0.0, 0.0)
        for bone in chain:
            p, q = local[bone]
            r = qrot(q, at)
            at = (p[0] + r[0], p[1] + r[1], p[2] + r[2])
        return at

    def apart(a, b):
        return sum((x - y) ** 2 for x, y in zip(a, b)) ** 0.5

    changed = 0
    for chain in chains:
        for i in range(1, len(poses) - 1):
            before, now, after = poses[i - 1], poses[i], poses[i + 1]
            a, b, c = reach(before, chain), reach(now, chain), reach(after, chain)
            least = min(apart(a, b), apart(b, c))
            if least <= 0.10 or apart(a, c) >= 0.6 * least:
                continue
            for bone in chain:
                q0, q2 = before[bone][1], after[bone][1]
                sign = 1.0 if sum(x * y for x, y in zip(q0, q2)) >= 0.0 else -1.0
                mid = [x + sign * y for x, y in zip(q0, q2)]
                n = sum(v * v for v in mid) ** 0.5
                now[bone] = (now[bone][0], tuple(v / n for v in mid))
            changed += 1
    return changed


def main_lean(out):
    names_wanted = sandbox_clip_names()
    src = Source()
    skeleton = next(d for _, n, d in src.files if n.endswith("Skeleton.hkx"))
    names, parents, rest = hkanim.skeleton(skeleton)
    hkx = dict(src.hkx)
    for pack in PACKS:
        for _, n, d in open_bnd(paths.er_files() / "chr" / f"{pack}.anibnd.dcx"):
            if n.endswith(".hkx"):
                hkx.setdefault(PureWindowsPath(n).stem, d)
    src.hkx = hkx

    chains = arm_chains(names, parents)
    lean = [b for b in LEAN if b in names]
    index = [names.index(b) for b in lean]
    positioned = [lean.index(b) for b in POSITIONED]
    # Only bones on the way to a lean bone need posing.
    needed = set()
    for i in index:
        while i >= 0 and i not in needed:
            needed.add(i)
            i = parents[i]

    def pose(local):
        out_pose = [None] * len(local)
        for i in range(len(local)):
            if i not in needed:
                continue
            p, q = local[i]
            if parents[i] < 0:
                out_pose[i] = (p, q)
            else:
                pp, pq = out_pose[parents[i]]
                rp = qrot(pq, p)
                out_pose[i] = ((pp[0] + rp[0], pp[1] + rp[1], pp[2] + rp[2]), qmul(pq, q))
        return [to_unity(*out_pose[i]) for i in index]

    out.parent.mkdir(parents=True, exist_ok=True)
    with open(out, "wb") as f:
        f.write(b"EHAN")
        f.write(struct.pack("<III", 2, len(lean), len(names_wanted)))
        for b in lean:
            raw = b.encode()
            f.write(struct.pack("<B", len(raw)) + raw)
        for (px, py, pz), (qx, qy, qz, qw) in pose(list(rest)):
            f.write(struct.pack("<7f", px, py, pz, qx, qy, qz, qw))
        f.write(struct.pack("<I", len(positioned)) + struct.pack("<%dH" % len(positioned), *positioned))
        frames_total = 0
        spikes = hand_spikes = 0
        missing = []
        for name in names_wanted:
            file, anim_id = "a%02d" % int(name[1:4]), int(name[5:])
            source = src.hkx_name(file, anim_id) or (name if name in hkx else None)
            if source is None:
                missing.append(name)
                f.write(struct.pack("<B", len(name)) + name.encode() + struct.pack("<fI", 0.0, 0))
                continue
            anim = hkanim.Animation(hkx[source])
            try:
                blend = blend_frames(src, file, anim_id)
            except Exception:
                blend = 0.0
            count = anim.frames_at_30fps() + 1
            f.write(struct.pack("<B", len(name)) + name.encode() + struct.pack("<fI", blend, count))
            poses = []
            for frame in range(count):
                local = list(rest)
                for bone, transform in zip(anim.bones, anim.sample_seconds(frame / 30.0)):
                    local[bone] = transform
                poses.append(local)
            spikes += despike(poses, needed)
            hand_spikes += despike_arms(poses, chains)
            for local in poses:
                p = pose(local)
                f.write(struct.pack("<%dh" % (4 * len(p)), *[q16(c) for _, q in p for c in q]))
                for i in positioned:
                    f.write(struct.pack("<3f", *p[i][0]))
            frames_total += count
        print("wrote", out, out.stat().st_size, "bytes,", len(names_wanted), "clips,", frames_total, "frames,", len(lean), "bones;",
              spikes, "one-frame bone spikes and", hand_spikes, "hand spikes smoothed; missing", missing)


if __name__ == "__main__":
    main()
