"""Generates src/sim/extracted.rs from unpacked Elden Ring files.

Inputs (unpacked by the user with UXM / WitchyBND, found through the
ER_FILES and ER_GAME_DIR environment variables; see tools/paths.py):
  chr/c0000.anibnd.dcx          animation events (TAE)
  chr/c0000_a00_hi.anibnd.dcx   base-movement animations (root motion)
  chr/c0000_a{2,3,4}x.anibnd.dcx weapon-moveset animations (root motion)
  regulation-bin/*.param        weapons, stamina costs and motion values

Usage: python tools/extract.py
"""
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import hkanim
import hkx
import param
import paths
import tae
from erfmt import open_bnd
from skel import fk, qrot
from pathlib import PureWindowsPath

SRC = paths.er_files()
OUT = Path(__file__).parent.parent / "src" / "sim" / "extracted.rs"
NEVER = 9999.0
# Styles the sandbox's weapon table does not use are read too, so their clips
# can be baked even though no action is extracted from them: a6x and a9x hold
# the later styles, among them the 12x whose two-handed heavy suits the game
# well, and a1x holds the 01x guard family.
PACKS = (
    "c0000_a00_hi", "c0000_a0x", "c0000_a1x", "c0000_a2x", "c0000_a3x",
    "c0000_a4x", "c0000_a5x", "c0000_a6x", "c0000_a9x",
    # Shadow of the Erdtree's styles (the Smithscript weapons' among them).
    "c0000_dlc01", "c0000_dlc02",
)

# (display name, EquipParamWeapon row). The moveset category, attack rating
# and behaviour variation are read from the row. Rows are the first weapon of
# each class, so the later names are the class rather than a specific weapon.
# The order is shared with WEAPON_LENGTH in data.rs and WEAPON_PARTS in rig.rs,
# and the shield must stay last: it is what the left hand holds.
WEAPONS = [
    ("Dagger", 1000000),
    ("Longsword", 2000000),
    ("Claymore", 3180000),
    ("Greatsword", 4000000),
    ("Rapier", 5000000),
    ("Uchigatana", 9000000),
    ("Club", 11000000),
    ("Battle Axe", 14000000),
    ("Short Spear", 16000000),
    ("Halberd", 18000000),
    ("Heavy Thrusting Sword", 6000000),
    ("Curved Sword", 7000000),
    ("Curved Greatsword", 8010000),
    ("Twinblade", 10000000),
    ("Great Hammer", 12000000),
    ("Flail", 13000000),
    ("Greataxe", 15000000),
    ("Great Spear", 17010000),
    ("Reaper", 19000000),
    ("Whip", 20000000),
    ("Fist", 21000000),
    ("Claw", 22000000),
    ("Colossal Weapon", 23000000),
    ("Torch", 24000000),
    # A second greatsword moveset: the Inseparable Sword (and the Knight's
    # Greatsword family) play style a128 where the Claymore plays a125.
    ("Inseparable Sword", 2090000),
    # Shadow of the Erdtree: a spear (style 247) whose heavy attack throws it.
    ("Smithscript Spear", 16500000),
    # The Morning Star: a club category weapon with its own heavy attacks.
    ("Morning Star", 11050000),
    # Two more styles of categories already here: the Hand Axe (style a170;
    # the "Battle Axe" class above is the category's own moveset) and the
    # Shamshir (a curved sword, style a156).
    ("Hand Axe", 14020000),
    ("Shamshir", 7030000),
    # The Banished Knight's Halberd: the halberd style a210 (swung like a great
    # spear) that the "Halberd" class above leaves out; the Great Epee: a heavy
    # thrusting sword of style a151.
    ("Banished Knight's Halberd", 18030000),
    ("Great Epee", 6020000),
    # The Erdsteel Dagger: a dagger of style a103 (thrusts; the Blade of
    # Calling's too).
    ("Erdsteel Dagger", 1150000),
    # The Crescent Moon Axe: a greataxe of style a177 (its own heavies).
    ("Crescent Moon Axe", 15030000),
    # Styles of their own (row ids from Smithbox's English row names):
    # the Raptor Talons (claws, a227), the Godskin Peeler (twinblade, a121)
    # and the Serpentbone Blade (katana, a165).
    ("Raptor Talons", 22030000),
    ("Godskin Peeler", 10010000),
    ("Serpentbone Blade", 9080000),
    # Shadow of the Erdtree's great katana category (a61); Rakshasa's has its
    # own heavy attacks (style a953).
    ("Great Katana", 66500000),
    ("Rakshasa's Great Katana", 66520000),
    # The Stone Club: a hammer of style a180 (row id from WitchyBND's Paramdex names).
    ("Stone Club", 11140000),
    # Guardian's Swordspear: a halberd of its own style.
    ("Guardian's Swordspear", 18110000),
    # Styles of their own (row ids from Smithbox's English row names): two
    # straight swords (Warhawk's Talon a117, Carian Knight's Sword a113), the
    # Warped Axe (a171), the Cross-Naginata (a spear, a202), the Zamor Curved
    # Sword (a curved greatsword, a215) and the Meteoric Ore Blade (a katana, a166).
    ("Warhawk's Talon", 2240000),
    ("Carian Knight's Sword", 2180000),
    ("Warped Axe", 15010000),
    ("Cross-Naginata", 16110000),
    ("Zamor Curved Sword", 8050000),
    ("Meteoric Ore Blade", 9030000),
    # Shadow of the Erdtree's other Smithscript weapons, which throw themselves
    # (see THROWS): the Greathammer (a250), the Axe (a248) and the Dagger,
    # a throwing blade (category 53) whose every attack is a throw.
    ("Smithscript Hammer", 12500000),
    ("Smithscript Axe", 14500000),
    ("Smithscript Dagger", 63500000),
    # Shadow of the Erdtree's categories: the light greatswords (Milady, 60),
    # the beast claws (62) and the hand-to-hand arts (55; Dane's Footwork is
    # style a246), which strike with the hands and feet (see LIMBS).
    ("Milady", 67500000),
    ("Beast Claw", 68500000),
    ("Dryleaf Arts", 60500000),
    ("Dane's Footwork", 60510000),
    ("Shield", 30000000),
]

# The Smithscript weapons throw themselves: their attacks' bullets (TAE event
# 2) become hits flagged as throws, which a host that can launch projectiles
# (Eldenheim) fires. Other classes' bullets are effects (the hand-to-hand
# arts' gusts) and stay out.
THROWS = ("Smithscript Spear", "Smithscript Hammer", "Smithscript Axe", "Smithscript Dagger")
DEFAULT_WEAPON = "Longsword"

# Thickness of the hit capsule around a weapon's striking line. ESTIMATE, and
# deliberately not the game's (0.3-0.4 m): those fat capsules are met by slim
# ones on the target's bones, whereas the sandbox's dummy is hit on its whole
# visible body. With the game's value a swing would land well before the
# weapon is seen to touch.
BLADE_RADIUS = 0.1

# The striking part of each weapon: a line between two points in the weapon
# bone's frame, as (across, along), where "along" runs from the grip to the
# tip. ESTIMATE: these follow the sandbox's stand-in models, because the
# game's weapon models (which carry the real points) are not unpacked. Pole
# weapons include a length of shaft, which hits in the game too.
BLADES = {
    "Dagger": ((0.0, 0.05), (0.0, 0.38)),
    "Longsword": ((0.0, 0.1), (0.0, 0.96)),
    "Claymore": ((0.0, 0.13), (0.0, 1.37)),
    "Greatsword": ((0.0, 0.15), (0.0, 1.8)),
    "Rapier": ((0.0, 0.1), (0.0, 1.08)),
    "Uchigatana": ((0.0, 0.1), (0.0, 1.03)),
    "Club": ((0.0, 0.2), (0.0, 0.57)),
    "Battle Axe": ((0.0, 0.45), (0.12, 0.66)),
    "Short Spear": ((0.0, 0.5), (0.0, 1.66)),
    "Halberd": ((0.0, 0.6), (0.0, 1.85)),
    "Heavy Thrusting Sword": ((0.0, 0.1), (0.0, 1.32)),
    "Curved Sword": ((0.0, 0.1), (0.04, 0.9)),
    "Curved Greatsword": ((0.0, 0.13), (0.06, 1.43)),
    "Twinblade": ((0.0, -1.05), (0.0, 1.05)),
    "Great Hammer": ((0.0, 0.55), (0.0, 1.16)),
    "Flail": ((0.0, 0.5), (0.0, 0.74)),
    "Greataxe": ((0.0, 0.75), (0.2, 1.12)),
    "Great Spear": ((0.0, 0.8), (0.0, 2.26)),
    "Reaper": ((0.0, 0.7), (0.45, 1.42)),
    "Whip": ((0.0, 0.1), (0.0, 2.08)),
    "Fist": ((0.0, -0.03), (0.0, 0.09)),
    "Claw": ((0.0, 0.0), (0.0, 0.34)),
    "Colossal Weapon": ((0.0, 0.6), (0.0, 1.56)),
    "Torch": ((0.0, 0.2), (0.0, 0.53)),
    "Inseparable Sword": ((0.0, 0.13), (0.0, 1.37)),
    "Smithscript Spear": ((0.0, 0.5), (0.0, 1.66)),
    "Morning Star": ((0.0, 0.25), (0.0, 0.6)),
    "Hand Axe": ((0.0, 0.4), (0.1, 0.6)),
    "Shamshir": ((0.0, 0.1), (0.04, 0.95)),
    "Banished Knight's Halberd": ((0.0, 0.6), (0.0, 1.85)),
    "Great Epee": ((0.0, 0.1), (0.0, 1.32)),
    "Erdsteel Dagger": ((0.0, 0.05), (0.0, 0.4)),
    "Crescent Moon Axe": ((0.0, 0.75), (0.2, 1.12)),
    "Raptor Talons": ((0.0, 0.0), (0.0, 0.34)),
    "Godskin Peeler": ((0.0, -1.05), (0.0, 1.05)),
    "Serpentbone Blade": ((0.0, 0.1), (0.04, 1.08)),
    "Great Katana": ((0.0, 0.13), (0.06, 1.45)),
    "Rakshasa's Great Katana": ((0.0, 0.13), (0.06, 1.45)),
    "Stone Club": ((0.0, 0.25), (0.0, 0.7)),
    "Guardian's Swordspear": ((0.0, 0.6), (0.0, 1.9)),
    "Warhawk's Talon": ((0.0, 0.1), (0.04, 0.95)),
    "Carian Knight's Sword": ((0.0, 0.1), (0.0, 1.0)),
    "Warped Axe": ((0.0, 0.7), (0.2, 1.1)),
    "Cross-Naginata": ((0.0, 1.0), (0.05, 2.0)),
    "Zamor Curved Sword": ((0.0, 0.13), (0.08, 1.4)),
    "Meteoric Ore Blade": ((0.0, 0.1), (0.04, 1.05)),
    "Smithscript Hammer": ((0.0, 0.55), (0.0, 1.16)),
    "Smithscript Axe": ((0.0, 0.4), (0.1, 0.62)),
    "Smithscript Dagger": ((0.0, 0.05), (0.0, 0.35)),
    "Milady": ((0.0, 0.12), (0.0, 1.25)),
    "Beast Claw": ((0.0, 0.0), (0.0, 0.36)),
    # The hand-to-hand arts strike with the hand, or (LIMBS) a foot.
    "Dryleaf Arts": ((0.0, -0.03), (0.0, 0.09)),
    "Dane's Footwork": ((0.0, -0.03), (0.0, 0.09)),
    "Shield": ((-0.28, 0.0), (0.28, 0.0)),
}

# Hits anchored on the body rather than the weapon (AtkParam dummy points):
# ER's hand-to-hand arts punch with points 1/2 and kick with 5/6. Which
# side is not known from the files here (the points live in the body model),
# so of each pair the limb that travels furthest through the window strikes.
# Other points (the weapon's, and body points such as 80 or 830/831, whose
# place isn't known) keep the weapon's line.
LIMBS = {
    "hand": [("R_Forearm", "R_Hand", 0.12), ("L_Forearm", "L_Hand", 0.12)],
    "foot": [("R_Calf", "R_Toe0", 0.06), ("L_Calf", "L_Toe0", 0.06)],
}
BODY_POINTS = {1: "hand", 2: "hand", 5: "foot", 6: "foot"}

# One-handed animation ids; the two-handed set is the same ids plus 2000.
ATTACKS = [
    ("Light1", 30000), ("Light2", 30010), ("Light3", 30020),
    ("Light4", 30030), ("Light5", 30040), ("Light6", 30050),
    ("RunLight", 30200), ("RunHeavy", 30210),
    ("RollAttack", 30300), ("CrouchAttack", 30310), ("BackstepAttack", 30400),
    ("Heavy1Charge", 30500), ("Heavy1", 30505), ("Heavy2Charge", 30510), ("Heavy2", 30515),
    ("GuardCounter", 30700),
    ("JumpLightLand", 31070), ("JumpLightLandShort", 31071),
    ("JumpHeavyLand", 31270), ("JumpHeavyLandShort", 31271),
    # With a weapon in the left hand: its own chain on the left attack button.
    *[("LeftLight%d" % (i + 1), 35000 + 10 * i) for i in range(6)],
    # With the same class of weapon in each hand, that button is a paired
    # ("power stance") moveset instead.
    *[("PairedLight%d" % (i + 1), 34000 + 10 * i) for i in range(6)],
    ("PairedRun", 34200), ("PairedRoll", 34300), ("PairedBackstep", 34400),
    ("PairedJumpLand", 34570), ("PairedJumpLandShort", 34571),
    # The feint: a dodge early in a heavy's charge (TAE 67/100290, the
    # charge's own window) turns it into a slash stepping back. Only the
    # classes that have one: thrusting and curved swords, great katanas.
    ("Feint1", 30950), ("Feint2", 30955),
]
AIR_LIGHT, AIR_HEAVY = 31030, 31230
# The jump attack with a weapon in each hand.
AIR_PAIRED = 34530
TWO_HAND_OFFSET = 2000

# ChrActionFlag ids (event type 0).
F_NO_TURN, F_DODGING, F_CANCEL_RH, F_CANCEL_MOVE = 7, 8, 4, 11
F_CANCEL_GUARD, F_IN_DODGE, F_CANCEL_DODGE, F_CANCEL_JUMP = 22, 25, 26, 32
F_IN_COMMON, F_CANCEL_R1, F_CANCEL_R2, F_JUMP_FRAMES = 87, 115, 116, 132
# From when the left-hand attack button takes over: inside a paired chain,
# and anywhere else.
F_CANCEL_L1_PAIRED, F_CANCEL_LH = 117, 16
SP_CHARGING = 100280
# Guarding through an attack (the Carian Knight's Sword's charging heavies).
F_GUARD = 3
# TAE 67 effects the game's script (ExecAttack) reads to change what the next
# press plays, while they're on: a light press goes to this swing, a heavy to
# the second heavy. Milady's chains are made of them. 19915 outranks the others.
SP_NEXT_LIGHT = ((19915, 2), (19903, 3), (19904, 4))
SP_NEXT_HEAVY2 = 19912
EVENT_BLEND = 16

# Weapon-independent actions: (Rust variant, TAE file, animation id).
BASE = [
    ("Backstep", "a00", 27000),
    ("SprintStop", "a00", 22200),
    ("LandLight", "a00", 202100),
    ("LandRun", "a00", 202127),
    ("LandSprint", "a00", 202125),
    # Landing on the move while locked on: front, back, left, right.
    *[("LandStrafeWalk(Dir::%s)" % d, "a00", 202110 + i) for i, d in enumerate(("Front", "Back", "Left", "Right"))],
    *[("LandStrafe(Dir::%s)" % d, "a00", 202115 + i) for i, d in enumerate(("Front", "Back", "Left", "Right"))],
    # Landing from a fall rather than a jump: a deep crouch, or from high up
    # a sprawl the character has to get up from.
    ("LandFall", "a00", 202300),
    ("LandHeavy", "a00", 202310),
]
for _load, _base in (("Light", 27100), ("Medium", 27110), ("Heavy", 27120)):
    for _i, _d in enumerate(("Front", "Back", "Left", "Right")):
        BASE.append((f"Roll(Load::{_load}, Dir::{_d})", "a00", _base + _i))
# Rolling while crouched is its own set, and leaves you crouched.
for _load, _base in (("Light", 327100), ("Medium", 327110), ("Heavy", 327120)):
    for _i, _d in enumerate(("Front", "Back", "Left", "Right")):
        BASE.append((f"CrouchRoll(Load::{_load}, Dir::{_d})", "a00", _base + _i))
# Walking and running jumps have back, left and right versions for use while
# locked on, where the character keeps facing its target.
JUMPS = [("Stand", 202000), ("Sprint", 202030)]
for _gait, _base in (("Walk", 202010), ("Run", 202020)):
    for _i, _d in enumerate(("", "Back", "Left", "Right")):
        JUMPS.append((_gait + _d, _base + _i))
for _kind, _anim in JUMPS:
    BASE.append((f"Jump(JumpKind::{_kind})", "a00", _anim))

# Reactions to being hit: (Rust variant, TAE file, animation id).
#
# The game grades hits by level and picks one of four animations per level;
# which of the four goes with which side is chosen here by how each one
# throws the head (a hit from the front snaps it back, and so on).
REACTIONS = [
    ("Hurt(HurtLevel::Small, Dir::Front)", "a00", 5110),
    ("Hurt(HurtLevel::Small, Dir::Back)", "a00", 5100),
    ("Hurt(HurtLevel::Small, Dir::Left)", "a00", 5120),
    ("Hurt(HurtLevel::Small, Dir::Right)", "a00", 5130),
    ("Hurt(HurtLevel::Middle, Dir::Front)", "a00", 5200),
    ("Hurt(HurtLevel::Middle, Dir::Back)", "a00", 5230),
    ("Hurt(HurtLevel::Middle, Dir::Left)", "a00", 5220),
    ("Hurt(HurtLevel::Middle, Dir::Right)", "a00", 5210),
    ("Hurt(HurtLevel::Large, Dir::Front)", "a00", 5300),
    ("Hurt(HurtLevel::Large, Dir::Back)", "a00", 5310),
    ("Hurt(HurtLevel::Large, Dir::Left)", "a00", 5330),
    ("Hurt(HurtLevel::Large, Dir::Right)", "a00", 5320),
    # Knocked off the feet and thrown 4 m. Picked by which way the root
    # motion throws the body: a hit from the front throws it backwards.
    ("Hurt(HurtLevel::Knockdown, Dir::Front)", "a00", 5400),
    ("Hurt(HurtLevel::Knockdown, Dir::Back)", "a00", 5410),
    ("Hurt(HurtLevel::Knockdown, Dir::Left)", "a00", 5420),
    ("Hurt(HurtLevel::Knockdown, Dir::Right)", "a00", 5430),
    # The medium shield's guard reactions (a000_0042xx are landings). Each
    # guard style has its own set, of the same lengths: 19200/19300/19400 for
    # the medium, great and small shields, and the breaks 19500/19510/19520.
    ("GuardHit", "a00", 19200),
    # Guard broken: the guard flung open, then hunched over.
    ("GuardBreak", "a00", 19500),
    # A blow blocked that all but broke the guard: thrown back 5 m in a back
    # roll, up from a crouch.
    ("GuardBlow", "a00", 19220),
]
PACKS_REACTIONS = ("c0000_a00_md", "c0000_a00_lo")  # also hold the swap clips

# Changing grip or weapon: (Rust variant, start animation, end animation).
# Each is a short reach for the weapon, during which the change takes effect,
# followed by a settle. The game plays them on the upper body only. Each end
# is the one whose first pose is exactly the start's last pose; every start
# begins, and every end finishes, in the neutral stance. 29000 is the right
# hand reaching for its weapon and 29030 the left. Two-handing borrows the
# *other* hand's reach, because that is the hand putting something away.
SWAPS = [
    ("ToTwoHandRight", 29060, 29050),
    ("ToTwoHandLeft", 29080, 29020),
    ("ToOneHandFromRight", 29040, 29050),
    ("ToOneHandFromLeft", 29010, 29020),
    ("NextWeapon", 29000, 29020),
    ("NextLeft", 29030, 29050),
]
EVENT_SET_STYLE, EVENT_SWITCH_WEAPON = 32, 33

# Looping locomotion: speed is root-motion distance over duration.
# The character is the Vagabond starting class at its starting level.
START_CLASS = 3000
# CharaInitParam: vigor, mind, endurance, ... one byte each from here.
CLASS_STATS = 0xC2
# CalcCorrectGraph rows turning a stat into a maximum.
HP_GRAPH, STAMINA_GRAPH = 100, 104


def stat_curve(row, stat):
    """Evaluates a CalcCorrectGraph row: five (stat, value) knots, with an
    exponent shaping each span (negative ones curve the other way)."""
    at = struct.unpack_from("<5f", row, 0)
    value = struct.unpack_from("<5f", row, 20)
    shape = struct.unpack_from("<5f", row, 40)
    for i in range(4):
        if stat <= at[i + 1]:
            t = max(0.0, (stat - at[i]) / (at[i + 1] - at[i]))
            t = t ** shape[i] if shape[i] > 0 else 1 - (1 - t) ** -shape[i]
            return value[i] + (value[i + 1] - value[i]) * t
    return value[4]


def vitals():
    """(max HP, max stamina) of the starting class."""
    stats = param.rows("CharaInitParam")[START_CLASS]
    vigor, endurance = stats[CLASS_STATS], stats[CLASS_STATS + 2]
    graphs = param.rows("CalcCorrectGraph")
    return int(stat_curve(graphs[HP_GRAPH], vigor)), int(stat_curve(graphs[STAMINA_GRAPH], endurance))


SPEEDS = [
    ("WALK_SPEED", 20000),
    ("RUN_SPEED", 20100),
    ("RUN_BACK_SPEED", 20101),
    ("RUN_SIDE_SPEED", 20102),
    ("SPRINT_SPEED", 20200),
    ("CROUCH_WALK_SPEED", 320000),
    ("CROUCH_RUN_SPEED", 320100),
]


def frames(t):
    return round(t * 30.0, 2)


def clip_name(file, anim_id):
    return "a%03d_%06d" % (int(file[1:]), anim_id)


class Source:
    def __init__(self):
        bnd = open_bnd(SRC / "chr" / "c0000.anibnd.dcx")
        self.files = bnd
        self.tae_raw = {PureWindowsPath(n).stem: d for _, n, d in bnd if n.endswith(".tae")}
        self.tae = {}
        self.hkx = {}
        for pack in PACKS + PACKS_REACTIONS:
            for _, n, d in open_bnd(SRC / "chr" / f"{pack}.anibnd.dcx"):
                if n.endswith(".hkx"):
                    self.hkx.setdefault(PureWindowsPath(n).stem, d)
                elif n.endswith(".tae"):
                    # The event files for the styles that live outside the base
                    # archive. Without these, a style such as a125 cannot be
                    # found at all, because every lookup starts from its TAE.
                    self.tae_raw.setdefault(PureWindowsPath(n).stem, d)
        self.behavior = param.rows("BehaviorParam_PC")
        self.atk = param.rows("AtkParam_Pc")
        self.weapons = param.rows("EquipParamWeapon")
        self.bullets = None
        self.skeleton = None

    def blade(self, file, anim_id, start, end, left_hand, span):
        """Where the weapon's striking line is on every frame of a hit window:
        [ax, ay, az, bx, by, bz] per frame, in the character's own space."""
        if self.skeleton is None:
            names, parents, rest = hkanim.skeleton(next(d for _, n, d in self.files if n.endswith("Skeleton.hkx")))
            self.skeleton = (names, parents, rest)
        names, parents, rest = self.skeleton
        bone = names.index("L_Weapon" if left_hand else "R_Weapon")
        anim = hkanim.Animation(self.hkx[self.hkx_name(file, anim_id)])
        out = []
        for frame in range(int(start), int(-(-end // 1)) + 1):
            local = list(rest)
            for b, transform in zip(anim.bones, anim.sample_seconds(frame / 30.0)):
                local[b] = transform
            position, rotation = fk(parents, local)[bone][:2]
            across, along = qrot(rotation, (1, 0, 0)), qrot(rotation, (0, 1, 0))
            if left_hand:
                # The left-hand bone is the right one mirrored: its tip is the other way.
                along = [-v for v in along]
            row = []
            for x, y in span:
                point = [position[i] + across[i] * x + along[i] * y for i in range(3)]
                # The game's space is mirrored front to back relative to the sandbox's.
                row += [point[0], point[1], -point[2]]
            out.append(row)
        return out

    def limb_blade(self, file, anim_id, start, end, limbs):
        """As blade, for a hit struck with a limb (LIMBS): of the candidate
        limbs, the one whose end travels furthest through the window, as a
        line from midway along it to just past its end."""
        if self.skeleton is None:
            self.blade(file, anim_id, start, start, False, ((0, 0), (0, 0)))
        names, parents, rest = self.skeleton
        anim = hkanim.Animation(self.hkx[self.hkx_name(file, anim_id)])
        poses = []
        for frame in range(int(start), int(-(-end // 1)) + 1):
            local = list(rest)
            for b, transform in zip(anim.bones, anim.sample_seconds(frame / 30.0)):
                local[b] = transform
            poses.append(fk(parents, local))
        best, best_travel = None, -1.0
        for a_name, b_name, extend in limbs:
            a, b = names.index(a_name), names.index(b_name)
            rows = []
            for pose in poses:
                pa, pb = pose[a][0], pose[b][0]
                d = [pb[i] - pa[i] for i in range(3)]
                n = max(sum(v * v for v in d) ** 0.5, 1e-6)
                near = [(pa[i] + pb[i]) / 2 for i in range(3)]
                far = [pb[i] + d[i] / n * extend for i in range(3)]
                rows.append([near[0], near[1], -near[2], far[0], far[1], -far[2]])
            travel = sum(sum((r1[3 + i] - r0[3 + i]) ** 2 for i in range(3)) ** 0.5 for r0, r1 in zip(rows, rows[1:]))
            if travel > best_travel:
                best, best_travel = rows, travel
        return best

    def strike_blade(self, file, anim_id, start, end, left_hand, span, anchor):
        """The striking line of a hit: on a limb for the body's points, else on the weapon."""
        if anchor in BODY_POINTS:
            return self.limb_blade(file, anim_id, start, end, LIMBS[BODY_POINTS[anchor]])
        return self.blade(file, anim_id, start, end, left_hand, span)

    def table(self, file):
        if file not in self.tae:
            self.tae[file] = tae.parse(self.tae_raw[file]) if file in self.tae_raw else {}
        return self.tae[file]

    def anim(self, file, anim_id):
        """The animation whose events apply, following event imports."""
        return self._imported(file, anim_id)[2]

    def _imported(self, file, anim_id):
        """(file, id, entry) at the end of the import chain; imports can cross files."""
        anim = self.table(file).get(anim_id)
        for _ in range(4):
            if anim is None or anim.import_from is None:
                break
            category, target = divmod(anim.import_from, 1000000)
            nxt = self.table("a%d" % category).get(target)
            if nxt is None:
                break
            file, anim_id, anim = "a%d" % category, target, nxt
        return file, anim_id, anim

    def hkx_name(self, file, anim_id):
        """Name of the HKX this animation plays: its own, the imported one's, or the one it borrows."""
        own = clip_name(file, anim_id)
        if own in self.hkx:
            return own
        file, anim_id, entry = self._imported(file, anim_id)
        if clip_name(file, anim_id) in self.hkx:
            return clip_name(file, anim_id)
        if entry is not None and entry.hkx_from is not None:
            borrowed = "a%03d_%06d" % divmod(entry.hkx_from, 1000000)
            if borrowed in self.hkx:
                return borrowed
        return None

    def motion(self, file, anim_id):
        name = self.hkx_name(file, anim_id)
        got = hkx.root_motion(self.hkx[name])
        if not got:
            # Stationary animation with no reference frame: no motion, and
            # its length is that of the skeletal animation itself.
            length = hkanim.Animation(self.hkx[name]).frames_at_30fps()
            return [(0.0, 0.0, 0.0)] * (length + 1)
        _duration, samples = got
        # Havok space here: +X is the character's left, -Z is forward.
        return [(x, y, -z) for x, y, z, _rot in samples]

    def weapon(self, row_id):
        row = self.weapons[row_id]
        (variation,) = struct.unpack_from("<i", row, 0x04)
        (weight,) = struct.unpack_from("<f", row, 0x10)
        (attack,) = struct.unpack_from("<H", row, 0xC8)
        return {
            "category": row[0xE7],
            "variation": variation,
            "attack": attack,
            "weight": weight,
            # Animation category of the idle/guard stance, one- and two-handed.
            "stance": (row[0xF0], row[0xF1]),
            # The weapon's own style (0: none): its animations, where that
            # style has them, replace the category's (Claymore 125, Knight's
            # Greatsword and Inseparable Sword 128).
            # spAtkcategory, a u16 (Rakshasa's Great Katana: 953).
            "alt": struct.unpack_from("<H", row, 0xEE)[0],
        }

    def judge(self, variation, judge_id):
        """(stamina cost, motion value, guard stamina damage multiplier,
        seconds both sides freeze for when the hit lands, radius of the hit
        capsule, whether it sits on the left-hand weapon, the dummy point the
        capsule is anchored on)."""
        # Weapons without their own rows fall back to their class's.
        for var in (variation, variation // 100 * 100):
            row = self.behavior.get(100000000 + var * 1000 + judge_id)
            if row is not None:
                break
        else:
            return None
        (ref_id,) = struct.unpack_from("<i", row, 0x0C)
        (stamina,) = struct.unpack_from("<i", row, 0x14)
        atk = self.atk.get(ref_id)
        if atk is None:
            return None
        (mv,) = struct.unpack_from("<H", atk, 0x3E)
        (stam_dmg,) = struct.unpack_from("<H", atk, 0x46)
        (hit_stop,) = struct.unpack_from("<f", atk, 0x14)
        # Where the capsule is anchored: points numbered 10000-10999 are on
        # the left-hand weapon's model.
        (anchor,) = struct.unpack_from("<h", atk, 0x2C)
        return stamina, mv / 100.0, stam_dmg / 100.0, hit_stop, BLADE_RADIUS, 10000 <= anchor < 11000, anchor

    def throw_judge(self, variation, judge_id):
        """As judge, for a thrown weapon: its behaviour row refers (refType 1)
        to a Bullet row, whose first field is the AtkParam the throw hits with."""
        if self.bullets is None:
            self.bullets = param.rows("Bullet")
        for var in (variation, variation // 100 * 100):
            row = self.behavior.get(100000000 + var * 1000 + judge_id)
            if row is not None:
                break
        else:
            return None
        (ref_id,) = struct.unpack_from("<i", row, 0x0C)
        (stamina,) = struct.unpack_from("<i", row, 0x14)
        bullet = self.bullets.get(ref_id) if row[0x09] == 1 else None
        atk = self.atk.get(struct.unpack_from("<i", bullet, 0x00)[0]) if bullet is not None else None
        if atk is None:
            return None
        (mv,) = struct.unpack_from("<H", atk, 0x3E)
        (stam_dmg,) = struct.unpack_from("<H", atk, 0x46)
        (hit_stop,) = struct.unpack_from("<f", atk, 0x14)
        return stamina, mv / 100.0, stam_dmg / 100.0, hit_stop, BLADE_RADIUS, False, -1


def windows(anim, flag):
    return sorted((frames(e.start), frames(e.end)) for e in anim.events if e.type == 0 and e.s32(0) == flag)


def first(anim, *flags):
    starts = [w[0] for f in flags for w in windows(anim, f)]
    return min(starts) if starts else NEVER


def fnum(v):
    return "NEVER" if v >= NEVER else f"{v:.1f}"


def effect_window(anim, effect):
    """The frames a TAE 67 effect is on, if the animation has it."""
    found = sorted((frames(e.start), frames(e.end)) for e in anim.events if e.type == 67 and e.s32(0) == effect)
    return found[0] if found else None


def next_light(anim):
    for effect, swing in SP_NEXT_LIGHT:
        window = effect_window(anim, effect)
        if window:
            return (*window, swing)
    return (0.0, 0.0, 0)


def next_heavy(anim):
    return effect_window(anim, SP_NEXT_HEAVY2) or (0.0, 0.0)


def hit_events(anim):
    return sorted((frames(e.start), frames(e.end), e.s32(2)) for e in anim.events if e.type == 1)


# TAE event 608 (AnimSpeedGradient): the animation plays at a speed going
# from its first value to its second across the event (DSAnimStudio's ER
# template names it). Every other time is in the animation's own frames, so
# the sim only has to advance the frame faster there.
EVENT_SPEED = 608


def speed_literal(anim):
    windows = sorted(
        (frames(e.start), frames(e.end), round(e.f32(0), 3), round(e.f32(1), 3))
        for e in (anim.events if anim is not None else ())
        if e.type == EVENT_SPEED and e.f32(0) > 0.0 and e.f32(1) > 0.0 and e.end > e.start
        and not (e.f32(0) == 1.0 and e.f32(1) == 1.0)
    )
    return "&[" + ", ".join("(%.1f, %.1f, %.3f, %.3f)" % w for w in windows) + "]"


def blend_frames(src, file, anim_id):
    entry = src.anim(file, anim_id)
    blends = [e.end * 30.0 for e in entry.events if e.type == EVENT_BLEND] if entry else []
    return blends[0] if blends else 4.0


def hit_literal(start, end, mv, stam_dmg, cost, hit_stop, radius, blade, throw):
    return (
        "Hit { from: %.1f, to: %.1f, mv: %.2f, guard_damage: %.2f, stamina: %.1f, stop: %.3f, radius: %.2f, blade: %s, throw: %d }"
        % (start, end, mv, stam_dmg, cost, hit_stop, radius, blade_literal(blade), throw)
    )


def bullets(anim):
    """An animation's bullet events (from, to, judge, hand): 1 the right hand's, 2 the left's."""
    return sorted((frames(e.start), frames(e.end), e.s32(2), 2 if e.s32(0) == 2 else 1) for e in anim.events if e.type == 2)


def blow_family(anchor):
    """Hits on one limb or one weapon: the body's points each their own, else
    by thousands (a weapon's 100s, the left weapon's 10000s, 11000s, 21000s)."""
    return ("body", anchor) if anchor in BODY_POINTS else ("weapon", anchor // 1000)


def air_throw(src, weapon, file, anim_id):
    """A Smithscript jump attack's first throw, as (from, to, cost, radius, hand, blade), or None."""
    events = bullets(src.anim(file, anim_id)) if weapon["name"] in THROWS else []
    if not events:
        return None
    start, end, judge, hand = events[0]
    got = src.throw_judge(weapon["variation"], judge) or (20.0, 1.3, 1.0, 0.08, 0.1, False, -1)
    return start, end, got[0], got[4], hand, src.blade(file, anim_id, start, end, hand == 2, weapon["blade"])


def action_def(src, name, file, anim_id, variation, reaction=False, span=None, base=None, throws=False, spins=False):
    """Rust `ActionDef { .. }` literal for one animation, or None if it does not exist.

    Reactions carry no input window and their flags mean something narrower
    than on a normal action, so for them: input is listened for from the start
    (the guard reactions keep ER's own window), the dodge flag is not treated
    as invincibility, and a hurt animation frees
    everything at the frame it frees movement. A knockdown is the exception:
    its flags are taken as written, giving invincibility while down and an
    early roll to get up."""
    anim = src.anim(file, anim_id)
    if anim is None or src.hkx_name(file, anim_id) is None:
        return None
    motion = src.motion(file, anim_id)
    total = len(motion) - 1
    dodge = windows(anim, F_DODGING)
    # Only the unconditional window counts; later ones carry a state condition.
    iframes = dodge[0] if dodge else (0.0, 0.0)
    common = first(anim, F_IN_COMMON)
    in_dodge = first(anim, F_IN_DODGE)
    cancel = {
        "light": first(anim, F_CANCEL_R1, F_CANCEL_RH),
        "heavy": first(anim, F_CANCEL_R2, F_CANCEL_RH),
        "dodge": first(anim, F_CANCEL_DODGE),
        "jump": first(anim, F_CANCEL_JUMP),
        "guard": first(anim, F_CANCEL_GUARD),
        "move": first(anim, F_CANCEL_MOVE),
    }
    cancel["left"] = first(anim, F_CANCEL_L1_PAIRED, F_CANCEL_LH)
    if cancel["left"] == NEVER:
        cancel["left"] = cancel["light"]
    if reaction:
        # The guard reactions keep ER's own input window (the flip listens from frame 16).
        if not name.startswith("Guard"):
            common = in_dodge = 0.0
        if "Knockdown" in name:
            # ER gives a knockdown no input window: nothing is heard before its first way out (the roll up).
            common = in_dodge = min(cancel.values())
        else:
            iframes = (0.0, 0.0)
            if name.startswith("Hurt"):
                cancel = {key: cancel["move"] for key in cancel}
    hits = hit_events(anim)
    turns = sorted((frames(e.start), frames(e.end), round(e.f32(0), 1)) for e in anim.events if e.type == 224)
    charging = sorted(
        (frames(e.start), frames(e.end)) for e in anim.events if e.type == 67 and e.s32(0) == SP_CHARGING
    )

    stamina = 0.0
    hit_list = []
    # Every hit that actually does damage, each with its own stamina cost.
    # Some animations also carry marker events whose attack row has no motion
    # value; those are not hits.
    # Windows of one limb or weapon that overlap or follow on at once are one
    # blow (the game's several capsules for a swing, the arts' two for a kick,
    # Milady's long slashes): a target is struck once, at the first one's values.
    # Not `spins` (twinblades): each window there is another turn of the spin.
    blows = []
    for start, end, judge in hits if variation is not None else ():
        got = src.judge(variation, judge)
        if not got:
            print("  ! no behaviour row for", clip_name(file, anim_id), "judge", judge)
            continue
        cost, mv, stam_dmg, hit_stop, radius, left_hand, anchor = got
        if mv <= 0.0:
            continue
        left_hand = left_handed(name, judge, left_hand)
        key = (left_hand, blow_family(anchor))
        for blow in reversed(blows):
            if not spins and blow["key"] == key and start <= blow["end"]:
                blow["end"] = max(blow["end"], end)
                break
        else:
            blows.append(dict(key=key, start=start, end=end, mv=mv, stam_dmg=stam_dmg, cost=cost, hit_stop=hit_stop,
                              radius=radius, left_hand=left_hand, anchor=anchor))
    for b in blows:
        blade = src.strike_blade(file, anim_id, b["start"], b["end"], b["left_hand"], span, b["anchor"])
        hit_list.append(hit_literal(b["start"], b["end"], b["mv"], b["stam_dmg"], b["cost"], b["hit_stop"], b["radius"], blade, 0))
        if len(hit_list) == 1:
            stamina = float(b["cost"])
    # The Smithscript weapons throw themselves (a bullet, TAE event 2): the
    # throw is a hit window of its own, flagged, which a host that can launch
    # projectiles (Eldenheim) fires. The Spear's and Axe's throwing heavies
    # only throw; the Hammer's spin first; every Dagger attack throws.
    if throws and variation is not None:
        for start, end, judge, hand in bullets(anim):
            # The throw's own behaviour row (through its Bullet row).
            got = src.throw_judge(variation, judge)
            if not got or got[1] <= 0.0:
                # None: an estimate, a charged throw hitting harder by as much
                # as the moveset's own charged swing does (its second heavy,
                # which is the class's). After a swing it costs nothing more.
                boost = 1.0
                if name.endswith("Charge") and base:
                    mvs = []
                    for slot in (anim_id + 10, anim_id + 15):
                        slot_anim = src.anim(base, slot)
                        sums = [src.judge(variation, j) for _s, _e, j in hit_events(slot_anim)] if slot_anim is not None else []
                        mvs.append(sum(g[1] for g in sums if g))
                    if mvs[0] > 0.0 and mvs[1] > 0.0:
                        boost = mvs[0] / mvs[1]
                got = (0.0 if hit_list else 20.0, 1.3 * boost, 1.0, 0.08, 0.1, False, -1)
            cost, mv, stam_dmg, hit_stop, radius, _left, _anchor = got
            blade = src.blade(file, anim_id, start, end, hand == 2, span)
            hit_list.append(hit_literal(start, end, mv, stam_dmg, cost, hit_stop, radius, blade, hand))
            if len(hit_list) == 1:
                stamina = float(cost)
            print("  throw:", clip_name(file, anim_id), "frames %.0f-%.0f, judge %d, hand %d, mv %.2f, stamina %.0f" % (start, end, judge, hand, mv, cost))
    charge = "Some((%.1f, %.1f))" % charging[0] if charging else "None"

    fields = [
        'name: "%s"' % name,
        'source: "%s"' % clip_name(file, anim_id),
        "total: %.1f" % total,
        "input_from: %s" % fnum(common),
        "input_dodge_from: %s" % fnum(min(common, in_dodge)),
        "cancel_light: %s" % fnum(cancel["light"]),
        "cancel_heavy: %s" % fnum(cancel["heavy"]),
        "cancel_dodge: %s" % fnum(cancel["dodge"]),
        "cancel_jump: %s" % fnum(cancel["jump"]),
        "cancel_guard: %s" % fnum(cancel["guard"]),
        "cancel_move: %s" % fnum(cancel["move"]),
        "cancel_left: %s" % fnum(cancel["left"]),
        "iframes: (%.1f, %.1f)" % iframes,
        "jump_frames: %s" % str(bool(windows(anim, F_JUMP_FRAMES))).lower(),
        "stamina: %.1f" % stamina,
        "hits: &[%s]" % ", ".join(hit_list),
        "charge: %s" % charge,
        "no_turn: &[" + ", ".join("(%.1f, %.1f)" % w for w in windows(anim, F_NO_TURN)) + "]",
        "turn: &[" + ", ".join("(%.1f, %.1f, %.1f)" % t for t in turns) + "]",
        "motion: &[" + ", ".join("[%.3f, %.3f, %.3f]" % m for m in motion) + "]",
        "speed: " + speed_literal(anim),
        "guard: (%.1f, %.1f)" % (windows(anim, F_GUARD)[0] if variation is not None and windows(anim, F_GUARD) else (0.0, 0.0)),
        "chain_light: (%.1f, %.1f, %d)" % next_light(anim),
        "chain_heavy: (%.1f, %.1f)" % next_heavy(anim),
    ]
    return "ActionDef { " + ", ".join(fields) + " }"


def left_handed(kind, judge, by_anchor):
    """Whether a hit is the left-hand weapon's. Off-hand attacks always are.
    Paired attacks number their hits x0 for the right hand and x5 for the left,
    which matches which weapon is moving through each hit's window."""
    if kind.startswith("Left"):
        return True
    if kind.startswith("Paired"):
        return judge % 10 >= 5
    return by_anchor


def blade_literal(blade):
    return "&[" + ", ".join("[" + ", ".join("%.3f" % v for v in row) + "]" for row in blade) + "]"


def swap_def(src, start, end):
    """Rust `SwapDef { .. }` literal for one grip or weapon change."""
    start_anim, end_anim = src.anim("a00", start), src.anim("a00", end)
    start_len = hkanim.Animation(src.hkx[src.hkx_name("a00", start)]).frames_at_30fps()
    end_len = hkanim.Animation(src.hkx[src.hkx_name("a00", end)]).frames_at_30fps()
    applies = [frames(e.start) for e in start_anim.events if e.type in (EVENT_SET_STYLE, EVENT_SWITCH_WEAPON)]
    return 'SwapDef { start: "%s", end: "%s", start_len: %.1f, end_len: %.1f, apply: %.1f, free_from: %.1f }' % (
        clip_name("a00", start),
        clip_name("a00", end),
        start_len,
        end_len,
        applies[0],
        first(end_anim, F_CANCEL_RH),
    )


# Classes that keep their category's own moveset: the Halberd row's style
# (a210) swings it like a great spear.
CATEGORY_ONLY = {"Halberd"}


def style_file(src, weapon, anim_id):
    """The weapon's own style where it has this animation, else its category's."""
    alt = 0 if weapon["name"] in CATEGORY_ONLY else weapon.get("alt") or 0
    if alt:
        file = "a%d" % alt
        if src.anim(file, anim_id) is not None and src.hkx_name(file, anim_id) is not None:
            return file
    return weapon["base_file"]


def gather(src):
    """Everything the generator and the animation baker need, in one pass."""
    weapons = []
    for name, row_id in WEAPONS:
        info = src.weapon(row_id)
        info["name"] = name
        info["base_file"] = "a%d" % info["category"]
        info["file"] = info["base_file"]
        info["blade"] = BLADES[name]
        weapons.append(info)

    attacks = []  # (weapon index, two_hand, kind, literal, file, anim id)
    air = []  # (weapon index, two_hand, heavy, from, to, cost, radius, blade, throw hand, file, anim id)
    for index, weapon in enumerate(weapons):
        for two_hand in (False, True):
            offset = TWO_HAND_OFFSET if two_hand else 0
            for kind, base_id in ATTACKS:
                # Off-hand and paired attacks only exist with a weapon in each hand.
                if two_hand and kind.startswith(("Left", "Paired")):
                    continue
                anim_id = base_id + offset
                weapon["file"] = style_file(src, weapon, anim_id)
                literal = action_def(src, kind, weapon["file"], anim_id, weapon["variation"], span=weapon["blade"], base=weapon["base_file"],
                                     throws=weapon["name"] in THROWS, spins=weapon["category"] == 24)
                if not literal:
                    continue
                # A few paired-weapon attacks carry no hit event of their own.
                # A swing that can never connect is worse than not having it:
                # leaving it out makes the moveset fall back to another attack.
                if "hits: &[]" in literal and not kind.endswith("Short"):
                    print("  skipped (no hit window):", weapon["name"], "2H" if two_hand else "1H", kind)
                    continue
                attacks.append((index, two_hand, kind, literal, weapon["file"], anim_id))
            for heavy, base_id in ((False, AIR_LIGHT), (True, AIR_HEAVY)):
                anim_id = base_id + offset
                weapon["file"] = style_file(src, weapon, anim_id)
                anim = src.anim(weapon["file"], anim_id)
                if anim is None or src.hkx_name(weapon["file"], anim_id) is None:
                    continue
                thrown = air_throw(src, weapon, weapon["file"], anim_id)
                if thrown:
                    start, end, cost, radius, hand, blade = thrown
                    air.append((index, two_hand, heavy, start, end, cost, radius, blade, hand, weapon["file"], anim_id))
                    continue
                if not hit_events(anim):
                    continue
                start, end, judge = hit_events(anim)[0]
                got = src.judge(weapon["variation"], judge)
                if not got:
                    continue
                cost, radius, left_hand = got[0], got[4], got[5]
                blade = src.strike_blade(weapon["file"], anim_id, start, end, left_hand, weapon["blade"], got[6])
                air.append((index, two_hand, heavy, start, end, cost, radius, blade, 0, weapon["file"], anim_id))
        # The paired jump attack: its first damaging hit.
        weapon["file"] = style_file(src, weapon, AIR_PAIRED)
        anim = src.anim(weapon["file"], AIR_PAIRED)
        if anim is not None and src.hkx_name(weapon["file"], AIR_PAIRED) is not None:
            thrown = air_throw(src, weapon, weapon["file"], AIR_PAIRED)
            if thrown:
                start, end, cost, radius, hand, blade = thrown
                air.append((index, False, "paired", start, end, cost, radius, blade, hand, weapon["file"], AIR_PAIRED))
            for start, end, judge in () if thrown else hit_events(anim):
                got = src.judge(weapon["variation"], judge)
                if not got or got[1] <= 0.0:
                    continue
                blade = src.strike_blade(weapon["file"], AIR_PAIRED, start, end, left_handed("Paired", judge, got[5]), weapon["blade"], got[6])
                air.append((index, False, "paired", start, end, got[0], got[4], blade, 0, weapon["file"], AIR_PAIRED))
                break
    return weapons, attacks, air


def main():
    src = Source()
    weapons, attacks, air = gather(src)
    out = [
        "//! GENERATED by tools/extract.py from the game's own files. Do not edit.",
        "//!",
        "//! Timings are animation frames at 30 fps, read from the player's TAE event",
        "//! files. Motion is root motion sampled once per frame from the HKX",
        "//! animations, as cumulative [left, up, forward] metres. Weapons, stamina",
        "//! costs and motion values come from EquipParamWeapon, BehaviorParam_PC and",
        "//! AtkParam_Pc.",
        "",
        "use super::data::{",
        "    ActionDef, ActionId, AirAttackDef, AttackKind, Dir, Hit, HurtLevel, JumpKind, Load, SwapDef, SwapKind,",
        "    WeaponInfo, NEVER,",
        "};",
        "",
    ]
    for const, anim_id in SPEEDS:
        motion = src.motion("a00", anim_id)
        end = motion[-1]
        dist = (end[0] ** 2 + end[2] ** 2) ** 0.5
        out.append("pub const %s: f32 = %.3f;" % (const, dist / ((len(motion) - 1) / 30.0)))

    hp, stamina = vitals()
    out += ["pub const MAX_HP: f32 = %d.0;" % hp, "pub const MAX_STAMINA: f32 = %d.0;" % stamina]

    out += ["", "pub const WEAPONS: &[WeaponInfo] = &["]
    for weapon in weapons:
        out.append(
            '    WeaponInfo { name: "%s", category: %d, attack: %.1f, weight: %.1f, stance: [%d, %d] },'
            % (weapon["name"], weapon["category"], weapon["attack"], weapon["weight"], *weapon["stance"])
        )
        print(weapon["name"], "category", weapon["category"], "attack", weapon["attack"])
    names = [w["name"] for w in weapons]
    out += [
        "];",
        "pub const DEFAULT_WEAPON: usize = %d;" % names.index(DEFAULT_WEAPON),
        "/// What the left hand holds.",
        "pub const SHIELD: usize = %d;" % (len(weapons) - 1),
        "/// The bare hand.",
        "pub const FIST: usize = %d;" % names.index("Fist"),
        "pub const TORCH: usize = %d;" % names.index("Torch"),
        "",
        "#[rustfmt::skip]",
        "pub fn base(id: ActionId) -> Option<ActionDef> {",
        "    use ActionId::*;",
        "    Some(match id {",
    ]
    for variant, file, anim_id in BASE:
        label = variant.replace("Load::", "").replace("Dir::", "").replace("JumpKind::", "")
        out.append("        %s => %s," % (variant, action_def(src, label, file, anim_id, None)))
    for variant, file, anim_id in REACTIONS:
        label = variant.replace("HurtLevel::", "").replace("Dir::", "")
        out.append("        %s => %s," % (variant, action_def(src, label, file, anim_id, None, reaction=True)))
    out += ["        _ => return None,", "    })", "}", ""]

    out += [
        "#[rustfmt::skip]",
        "pub fn attack(weapon: usize, two_hand: bool, kind: AttackKind) -> Option<ActionDef> {",
        "    use AttackKind::*;",
        "    Some(match (weapon, two_hand, kind) {",
    ]
    for index, two_hand, kind, literal, _file, _anim_id in attacks:
        out.append("        (%d, %s, %s) => %s," % (index, str(two_hand).lower(), kind, literal))
    out += ["        _ => return None,", "    })", "}", ""]

    out += [
        "/// Jump attacks while still in the air.",
        "#[rustfmt::skip]",
        "pub fn air_attack(weapon: usize, two_hand: bool, heavy: bool) -> Option<AirAttackDef> {",
        "    Some(match (weapon, two_hand, heavy) {",
    ]
    air_literal = (
        'AirAttackDef { from: %.1f, to: %.1f, stamina: %.1f, source: "%s", radius: %.2f, blade: %s, speed: %s, throw: %d }'
    )
    for index, two_hand, heavy, start, end, cost, radius, blade, thrown, file, anim_id in air:
        if heavy == "paired":
            continue
        out.append(
            '        (%d, %s, %s) => AirAttackDef { from: %.1f, to: %.1f, stamina: %.1f, source: "%s", radius: %.2f, blade: %s, speed: %s, throw: %d },'
            % (
                index, str(two_hand).lower(), str(heavy).lower(), start, end, cost, clip_name(file, anim_id), radius,
                blade_literal(blade), speed_literal(src.anim(file, anim_id)), thrown,
            )
        )
    out += ["        _ => return None,", "    })", "}", ""]

    out += [
        "/// The jump attack with a weapon in each hand.",
        "#[rustfmt::skip]",
        "pub fn air_paired(weapon: usize) -> Option<AirAttackDef> {",
        "    Some(match weapon {",
    ]
    for index, _two_hand, heavy, start, end, cost, radius, blade, thrown, file, anim_id in air:
        if heavy == "paired":
            literal = air_literal % (
                start, end, cost, clip_name(file, anim_id), radius, blade_literal(blade), speed_literal(src.anim(file, anim_id)), thrown
            )
            out.append("        %d => %s," % (index, literal))
    out += ["        _ => return None,", "    })", "}", ""]

    out += [
        "/// Grip and weapon changes.",
        "#[rustfmt::skip]",
        "pub fn swap(kind: SwapKind) -> SwapDef {",
        "    match kind {",
    ]
    for variant, start, end in SWAPS:
        out.append("        SwapKind::%s => %s," % (variant, swap_def(src, start, end)))
    out += ["    }", "}", ""]

    OUT.write_text("\n".join(out), encoding="utf-8", newline="\n")
    print("wrote", OUT, "-", len(attacks), "attacks,", len(air), "air attacks")


if __name__ == "__main__":
    main()
