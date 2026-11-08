"""Writes build/eldenheim_data.bin from sandbox/src/sim/extracted.rs, without Rust.

Same file, byte for byte, as `simtrace data` (tools/simtrace/src/main.rs,
export_data): this reads the literals extract.py generated instead of
compiling them. Keep the two in step when the format changes.

    python tools/export_data.py [OUT.bin]
"""
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "sandbox" / "src" / "sim" / "extracted.rs"
NEVER = 9999.0  # sandbox/src/sim/data.rs

# Declaration order in sandbox/src/sim/data.rs = the `as u8` ordinals.
LOADS = ["Light", "Medium", "Heavy"]
DIRS = ["Front", "Back", "Left", "Right"]
LEVELS = ["Small", "Middle", "Large", "Knockdown"]
JUMPS = ["Stand", "Walk", "WalkBack", "WalkLeft", "WalkRight", "Run", "RunBack", "RunLeft", "RunRight", "Sprint"]
SWAPS = ["ToTwoHandRight", "ToTwoHandLeft", "ToOneHandFromRight", "ToOneHandFromLeft", "NextWeapon", "NextLeft"]
KINDS = [
    "Light1", "Light2", "Light3", "Light4", "Light5", "Light6", "RunLight", "RunHeavy", "RollAttack", "CrouchAttack",
    "BackstepAttack", "Heavy1Charge", "Heavy1", "Heavy2Charge", "Heavy2", "GuardCounter", "JumpLightLand",
    "JumpLightLandShort", "JumpHeavyLand", "JumpHeavyLandShort", "LeftLight1", "LeftLight2", "LeftLight3",
    "LeftLight4", "LeftLight5", "LeftLight6", "PairedLight1", "PairedLight2", "PairedLight3", "PairedLight4",
    "PairedLight5", "PairedLight6", "PairedRun", "PairedRoll", "PairedBackstep", "PairedJumpLand",
    "PairedJumpLandShort", "Feint1", "Feint2",
]
ENUMS = {"Load": LOADS, "Dir": DIRS, "HurtLevel": LEVELS, "JumpKind": JUMPS, "SwapKind": SWAPS, "AttackKind": KINDS}

TOKEN = re.compile(r'\s*(?:(-?\d+\.\d+|-?\d+)|("[^"]*")|([A-Za-z_]\w*(?:::\w+)*)|(&\[|=>|[\[\](){},:]))')


class Parser:
    """Rust literals as extract.py writes them: numbers, strings, true/false,
    NEVER, None, Some(x), Name { field: v }, &[..], [..], (..), and enum paths."""

    def __init__(self, text):
        self.toks = []
        pos = 0
        while pos < len(text):
            m = TOKEN.match(text, pos)
            if not m or m.end() == pos:
                if text[pos:].strip():
                    raise ValueError("cannot read: %r" % text[pos:pos + 40])
                break
            self.toks.append(m.group(m.lastindex))
            pos = m.end()
        self.i = 0

    def peek(self):
        return self.toks[self.i] if self.i < len(self.toks) else None

    def take(self, want=None):
        t = self.toks[self.i]
        if want is not None and t != want:
            raise ValueError("expected %r, got %r" % (want, t))
        self.i += 1
        return t

    def items(self, close):
        out = []
        while self.peek() != close:
            out.append(self.value())
            if self.peek() == ",":
                self.take()
        self.take(close)
        return out

    def value(self):
        t = self.take()
        if t[0] == '"':
            return t[1:-1]
        if t[0].isdigit() or t[0] == "-":
            return float(t)
        if t in ("&[", "["):
            return self.items("]")
        if t == "(":
            return tuple(self.items(")"))
        if t == "true" or t == "false":
            return t == "true"
        if t == "NEVER":
            return NEVER
        if t == "None":
            return None
        if self.peek() == "{":
            self.take()
            fields = {}
            while self.peek() != "}":
                name = self.take()
                self.take(":")
                fields[name] = self.value()
                if self.peek() == ",":
                    self.take()
            self.take("}")
            return fields
        if self.peek() == "(":  # Some(x), Roll(Load::Light, Dir::Front)
            self.take()
            args = self.items(")")
            return args[0] if t == "Some" else (t, *args)
        if "::" in t:
            enum, variant = t.split("::")
            return ENUMS[enum].index(variant)
        if t in KINDS:
            return KINDS.index(t)
        return t  # a bare unit variant: Backstep, LandLight, ...


def func_body(text, name):
    """The match arms of `pub fn name(..)`, as {key: value} (first arm wins, as in Rust)."""
    start = text.index("pub fn %s(" % name)
    start = text.index("{\n", text.index("match", start)) + 2
    end = text.index("\n        _ => return None", start) if name != "swap" else text.index("\n    }\n}", start)
    arms = {}
    for line in text[start:end].splitlines():
        if not line.strip():
            continue
        p = Parser(line.strip().rstrip(","))
        key = p.value()
        p.take("=>")
        arms.setdefault(key, p.value())
    return arms


def const(text, name):
    return re.search(r"pub const %s: \w+ = ([\d.]+);" % name, text).group(1)


class Out:
    def __init__(self):
        self.b = bytearray()

    def u8(self, v):
        self.b += struct.pack("<B", int(v))

    def u32(self, v):
        self.b += struct.pack("<I", int(v))

    def f32(self, v):
        self.b += struct.pack("<f", v)

    def str(self, s):
        data = s.encode()
        self.u32(len(data))
        self.b += data

    def floats(self, rows):
        self.u32(len(rows))
        for row in rows:
            for v in row:
                self.f32(v)

    def action(self, d):
        self.str(d["name"])
        self.str(d["source"])
        for f in ("total", "input_from", "input_dodge_from", "cancel_light", "cancel_heavy", "cancel_dodge",
                  "cancel_jump", "cancel_guard", "cancel_move", "cancel_left"):
            self.f32(d[f])
        self.f32(d["iframes"][0])
        self.f32(d["iframes"][1])
        self.u8(d["jump_frames"])
        self.f32(d["stamina"])
        self.u32(len(d["hits"]))
        for h in d["hits"]:
            for f in ("from", "to", "mv", "guard_damage", "stamina", "stop", "radius"):
                self.f32(h[f])
            self.floats(h["blade"])
            self.u8(h["throw"])
        if d["charge"] is None:
            self.u8(0)
        else:
            self.u8(1)
            self.f32(d["charge"][0])
            self.f32(d["charge"][1])
        self.floats(d["no_turn"])
        self.floats(d["turn"])
        self.floats(d["motion"])
        self.floats(d["speed"])
        self.f32(d["guard"][0])
        self.f32(d["guard"][1])

    def air(self, d):
        self.f32(d["from"])
        self.f32(d["to"])
        self.f32(d["stamina"])
        self.str(d["source"])
        self.f32(d["radius"])
        self.floats(d["blade"])
        self.floats(d["speed"])
        self.u8(d["throw"])


def all_base():
    """simtrace's all_base(): every base action, as (variant, args, key bytes)."""
    ids = []
    for l in range(3):
        for d in range(4):
            ids.append((("Roll", l, d), [0, l, d, 0]))
            ids.append((("CrouchRoll", l, d), [1, l, d, 0]))
    ids += [("Backstep", [2, 0, 0, 0]), ("SprintStop", [3, 0, 0, 0])]
    ids += [(("Jump", k), [4, k, 0, 0]) for k in range(10)]
    ids += [("LandLight", [5, 0, 0, 0]), ("LandRun", [6, 0, 0, 0]), ("LandSprint", [7, 0, 0, 0])]
    ids += [(("LandStrafeWalk", d), [8, d, 0, 0]) for d in range(4)]
    ids += [(("LandStrafe", d), [9, d, 0, 0]) for d in range(4)]
    ids += [("LandHeavy", [10, 0, 0, 0]), ("LandFall", [11, 0, 0, 0]), ("GuardHit", [13, 0, 0, 0]),
            ("GuardBreak", [14, 0, 0, 0]), ("GuardBlow", [16, 0, 0, 0])]
    ids += [(("Hurt", l, d), [15, l, d, 0]) for l in range(4) for d in range(4)]
    return ids


def export(text):
    o = Out()
    o.b += b"EHDT"
    o.u32(4)  # 4: throws (a byte per hit and air attack) and guard windows (see simtrace)
    for name in ("WALK_SPEED", "RUN_SPEED", "RUN_BACK_SPEED", "RUN_SIDE_SPEED", "SPRINT_SPEED", "CROUCH_WALK_SPEED",
                 "CROUCH_RUN_SPEED", "MAX_HP", "MAX_STAMINA"):
        o.f32(float(const(text, name)))
    for name in ("DEFAULT_WEAPON", "SHIELD", "FIST", "TORCH"):
        o.u32(int(const(text, name)))

    start = text.index("pub const WEAPONS: &[WeaponInfo] = &[") + len("pub const WEAPONS: &[WeaponInfo] = ")
    weapons = Parser(text[start:text.index("\n];", start) + 2]).value()
    o.u32(len(weapons))
    for w in weapons:
        o.str(w["name"])
        o.u8(w["category"])
        o.f32(w["attack"])
        o.f32(w["weight"])
        o.u8(w["stance"][0])
        o.u8(w["stance"][1])

    base, attack = func_body(text, "base"), func_body(text, "attack")
    actions = [(k, base[v]) for v, k in all_base() if v in base]
    for w in range(len(weapons)):
        for two in (False, True):
            for k in range(len(KINDS)):
                d = attack.get((float(w), two, k))
                if d is not None:
                    actions.append(([12, w, two, k], d))
    o.u32(len(actions))
    for key, d in actions:
        for b in key:
            o.u8(b)
        o.action(d)

    air, paired = func_body(text, "air_attack"), func_body(text, "air_paired")
    airs = []
    for w in range(len(weapons)):
        for two in (False, True):
            for heavy in (False, True):
                if (float(w), two, heavy) in air:
                    airs.append(([w, two, heavy], air[(float(w), two, heavy)]))
        if float(w) in paired:
            airs.append(([w, 0, 2], paired[float(w)]))
    o.u32(len(airs))
    for key, d in airs:
        for b in key:
            o.u8(b)
        o.air(d)

    swaps = func_body(text, "swap")
    for k in range(len(SWAPS)):
        s = swaps[k]
        o.str(s["start"])
        o.str(s["end"])
        for f in ("start_len", "end_len", "apply", "free_from"):
            o.f32(s[f])
    print("data: %d actions, %d air attacks, %d weapons" % (len(actions), len(airs), len(weapons)), file=sys.stderr)
    return bytes(o.b)


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "build" / "eldenheim_data.bin"
    if not SRC.exists():
        sys.exit("%s not found: run sandbox/tools/setup.py first (see README)." % SRC)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_bytes(export(SRC.read_text(encoding="utf-8")))
    print("wrote", out)


if __name__ == "__main__":
    main()
