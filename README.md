# Eldenheim

The Elden Ring movement and combat sandbox
([ELDEN-RING-Combat-Rewrite](https://github.com/Funny-Bones/ELDEN-RING-Combat-Rewrite)),
running Valheim's player. While a melee weapon or bare fists are out, the
sandbox's own simulation moves the character, dodges, jumps, attacks, guards
and takes hits, with Elden Ring's frame data and animations. Valheim keeps
everything else.

## How it is built

| Part | What it is |
|---|---|
| `sandbox/` | The sandbox itself (MIT, Funny-Bones and contributors), at the version Eldenheim's data is built from. Its tools read your game files. |
| `src/Eldenheim.Sim` | The sandbox's `src/sim`, ported line for line to C#. Matches the Rust sim exactly over the whole 385 s demo (`tests/Eldenheim.Sim.Replay`). |
| `src/Eldenheim` | The BepInEx plugin. It ticks the sim at 60 Hz inside Valheim's motion step, moves the body through PhysX, and plays the animations through a playable output that writes the whole pose. |
| `tools/simtrace` | A Rust harness that compiles the sandbox's own `sim` and `demo`. It exports the extracted data and records the demo, tick by tick. |
| `tools/bake_unity.py` | Bakes every clip the sandbox plays as bone rotations, from your own game files. |

## Setup (your own copy of Elden Ring)

Nothing from Elden Ring or Valheim is in this repository, and none is shipped:
the mod's data is generated from your own installed copy of Elden Ring.
Shadow of the Erdtree is not needed. Its weapon styles are in the base game's own
Data3 archive since patch 1.12, so every up-to-date copy has them.

You need Python 3.10+ (`python -m pip install pycryptodome zstandard`), Rust,
the .NET SDK, and Valheim with BepInEx. Run everything from this folder.

1. Point the tools at the game's `Game` folder (the one holding
   `eldenring.exe`):

   ```powershell
   $env:ER_GAME_DIR = "C:\Program Files (x86)\Steam\steamapps\common\ELDEN RING\Game"
   ```

2. Unpack the few files the tools read into `er-files/` (or wherever
   `ER_FILES` points):

   ```bash
   python sandbox/tools/unpack_game_files.py
   ```

3. Generate the sandbox's data (action table, animations, sounds):

   ```bash
   python sandbox/tools/setup.py
   ```

4. Bake the mod's animations and export its data:

   ```bash
   python tools/bake_unity.py --all build/eldenheim_anims_all.bin
   ```

   ```bash
   cargo run --release --manifest-path tools/simtrace/Cargo.toml -- data build/eldenheim_data.bin
   ```

5. Build the plugin:

   ```bash
   dotnet build src/Eldenheim -c Release
   ```

The build copies the plugin and the generated files to
`C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\Eldenheim` (`-p:ValheimDir=...` for
elsewhere). Valheim must be closed. Never share the generated files:
they are derived from Elden Ring.

Check the C# sim against the sandbox (records the demo first):

```bash
cargo run --release --manifest-path tools/simtrace/Cargo.toml -- demo build/demo_trace.bin
```


```bash
dotnet run --project tests/Eldenheim.Sim.Replay -c Release -- build/eldenheim_data.bin build/demo_trace.bin
```

## Adding an Elden Ring weapon class

1. Find the weapon's EquipParamWeapon row: Smithbox's
   `Assets/PARAM/ER/Param Row Names/English/EquipParamWeapon.json` names them
   (the plain weapon is the row ending in `0000`).
2. In `sandbox/`, add `(name, row)` to `WEAPONS` in `tools/extract.py`
   (before the shield), its striking line to `BLADES`, and a stand-in shape
   to `WEAPON_PARTS` in `src/rig.rs`, in the same order. The row's category
   and style (byte 0xEE) pick the animations; a style with no animations in
   the game files plays its category's.
3. Rebuild the data in the order under Setup (setup.py,
   bake_unity, simtrace data and demo) and run the replay check.
4. Add the menu name to `Settings.ErClasses` (and a note to a Movesets entry
   if it suits a Valheim weapon type).

## Controls (defaults; all rebindable in F1: "Set", then press the button)

| Action | Keyboard / mouse | Gamepad |
|---|---|---|
| Roll / backstep (tap), sprint (hold) | Valheim's Run (Left Shift) | B |
| Jump | Valheim's Jump (Space) | A |
| Light attack | Valheim's Attack (LMB) | RB |
| Heavy attack (hold to charge) | Valheim's Secondary Attack | RT |
| Guard / left-hand attack; guard counter = heavy after a block | Valheim's Block (RMB) | LB |
| Crouch | Valheim's Crouch | L3 |
| Walk (toggle) | Valheim's ToggleWalk | — |
| Lock on (with nothing to lock: recentre the camera) | Caps Lock | R3 |
| Reverse backstep (not locked on): backstep, then at once press a direction: S goes forward, A right, D left | — | — |
| Switch lock-on target | flick the mouse left/right | right stick left/right |
| Two-hand right / left | hold Valheim's Use + Attack / Block | hold Use (X) + RB / LB |

With an empty left hand, the guard button throws a left punch, as in Elden
Ring; `EmptyHandGuard` (F1 → 2 - Controls) makes it guard with the right
weapon instead, Valheim-style. The guard can be raised while sprinting.

**Left hand:** hold the two-hand modifier (Use) while equipping a melee
weapon (hotbar or inventory) to put it in the left hand, replacing a shield or
torch. Valheim's two-handers work in either hand too, and keep a shield,
torch or weapon in the other (held one-handed then; two-handed when alone).
Sheathing (R) and reloading keep each weapon in its hand.

- **Power stance:** two weapons of the same class, one in each hand: the
  guard button plays the power-stance moveset. Sheathed, they cross on the
  back.
- **Paired weapons:** Valheim items that are already a pair (knuckles/claws,
  Skoll and Hati, the dual axes) take both hands and nothing else. Claws are
  held two-handed; Skoll and Hati and the dual axes play their class's power
  stance, a weapon in each hand.
- Two-handing one weapon puts the other hand's item on the back, with
  Valheim's equip sound.

**Stamina and falls** are Valheim's: sprinting, jumping and dodging cost what
they cost in vanilla (with the Run and Dodge skills and gear). An attack costs
the weapon's own attack stamina for a first light attack, the others in Elden
Ring's proportion, each hit charged to the weapon that swings it (so each
weapon in a power stance pays its own). As in vanilla, an attack you can't pay
for doesn't come out. Falls do Valheim's damage (100 at most); only one past 50
ends in the heavy landing.

Each `Valheim:Name` default follows Valheim's own binding for that button.
Under *2 - Controls*, `SeparateSprint` splits dodge and sprint into two
buttons, and `DodgeTrigger` sets whether the dodge comes out on Release (as
in Elden Ring) or on Press.
With a gamepad and the inventory open, `LeftJoystickInventory` lets the left
stick walk (only the D-pad moves the selection) and `DodgeInInventory` makes B
dodge and sprint instead of closing it (Y closes it). A gamepad button still
held when a menu closes or the game unpauses does nothing until it is let go
(held keys carry on, so a sprint goes on past the inventory).

**Stamina:** attacks follow Elden Ring (F1 → 7 - Stamina & Movement → `NegativeStamina`):
any attack starts while there is stamina left, whatever it costs. One that
empties the bar keeps it empty (flashing red) until that attack ends, so no
chain goes on; then it refills as usual. Off, an attack needs its whole cost,
as in Valheim. Stamina doesn't refill during an action until its
recovery frame.

**Weapons:** each Valheim weapon type's Elden Ring moveset is chosen in F1 →
Weapons (any class can be picked); the defaults are:
- One-handed: swords → Longsword (or Shamshir), knives → Dagger, clubs →
  Morning Star (or Club, the earlier one), axes → Hand Axe (or Battle Axe,
  the earlier one), atgeirs → Halberd (or Banished Knight's Halberd), bare hands → Fist.
- Spears → Smithscript Spear (Shadow of the Erdtree): its heavy attacks throw
  it, with Valheim's spear projectile and sounds, at full damage at any
  range (charged throws hit harder, as charged swings do); it stays in your
  hand, or with F1 → 5 - Movesets → `ThrowSpears` the spear itself flies, leaves
  the inventory and lands where it hits, as in Valheim.
- Knuckles and claws → Claw (Raptor Talons; or Fist, the earlier one), two-handed.
- Two-handed: swords (the Krom too) → Claymore or Knight's Greatsword (F1 →
  Weapons → TwoHandedSwords: Elden Ring's greatswords share a category but
  differ by style, 125 for the Claymore, 128 for the Knight's Greatsword family and Inseparable
  Sword); sledges → Great Hammer, axes → Greataxe. Skoll and
  Hati → Dagger, dual axes → the one-handed axes' class, in power stance (paired weapons
  can't be two-handed).
- Equipping, sheathing (R) and drawing play Elden Ring's swap for the hand
  that changed.
- Poles are held where Elden Ring's reach as far: atgeirs and spears slide
  along the shaft to match.
- Left hand: shield → Shield, torch → Torch, a weapon → its class.
- Bows, staffs and tools hand the player back to Valheim.

## Sound

Swings, steps, rolls, jumps and landings use Valheim's own sounds and dust.
The Elden Ring sounds are the guard counter's own (F1 → 9 - Sound, quieter than
the game's mix by default), the guard break's and the parry's (`ParrySound`,
in place of Valheim's). Each is the recording the game's own clip plays on its
first frame (the guard break's `a000_019500` and being parried, `a000_005800`).
The build copies them from the sandbox's `assets/player_sounds.bin` and
`assets/sounds/`; without them those moments are Valheim's sounds alone.

The F1 sections marked *(server)* (6 - Combat, 7 - Stamina & Movement) follow
the server's values in multiplayer.

## Testing aids

- **Commands:** `commands.txt` beside the DLL (one command per line) drives
  scripted tests, and `results.log` records what happened. See `Commands.cs`:
  `status`, `press`/`hold`/`stick`, `equip PREFAB [left]`, `hide`/`show`,
  `spawn`, `clear`, `shot NAME` (the game's own screenshot),
  `smooth start|stop`, `noon`, `camdist`, `deform`, `timescale X`,
  `cam X Y Z` (camera held beside the body), `turn DEG`, and
  `probe start|stop` (how deep the left forearm and hand sink into the drawn
  torso and armour meshes per action frame, with a screenshot of the worst).
- Use Valheim's own training dummies.
- **F1 → Debug → EventLog** (off by default): writes every swing, hit,
  stamina use and reaction to the BepInEx log and `results.log`. Scripts that
  read those lines turn it on first with `setting EventLog true`.
- **F1 → Debug → CombatLog:** an on-screen list of recent stamina use and
  damage dealt, with totals.
- **F1 → Debug → IframeDebug:** a pale blue capsule round the player while
  invincible, and the action's frame and i-frame window on screen.

## License

MIT (`LICENSE`). `sandbox/` and the sim port are under the sandbox's MIT
license, Copyright (c) 2026 Funny-Bones and contributors.
