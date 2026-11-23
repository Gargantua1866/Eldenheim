# Eldenheim

Eldenheim uses the Elden Ring movement and combat sandbox
([ELDEN-RING-Combat-Rewrite](https://github.com/Funny-Bones/ELDEN-RING-Combat-Rewrite))
to control Valheim's player. Melee weapons and bare fists use the
sandbox's simulation to attack, move, dodge, jump, guard, and take hits,
all with Elden Ring's frame data and animations.

Showcase video

[![Eldenheim showcase video](https://img.youtube.com/vi/2Gdm8KkQkZU/maxresdefault.jpg)](https://youtu.be/2Gdm8KkQkZU)

> [!IMPORTANT]
> A copy of Elden Ring is required to install Eldenheim. The mod's animations
> and data are made from your own installed game files; none are shipped.

## Required mods

- BepInEx, to load the plugin.
- ConfigurationManager and ConditionalConfigSync, both by shudnal, to change
  the mod's settings in game (F1).
- Jotunn, for multiplayer.

The NoWeaponDust mod by Azumatt is highly recommended.

## Easy install (EldenheimSetup.exe)

1. Install BepInEx and the required mods above
2. Close your mod manager if using one
3. Unzip `EldenheimSetup-<version>.zip` anywhere and run
   `EldenheimSetup\EldenheimSetup.exe`.
4. Check the two folders it found (Elden Ring's `Game` folder, and Valheim's
   `BepInEx\plugins`, or a Gale or r2modman profile), then press Install.

The installer reads your copy of Elden Ring to make the mod's data (a few minutes), and
installs everything into `plugins\Eldenheim` (or updates an Eldenheim already
there). In a Gale, r2modman or Thunderstore Mod Manager profile it also adds
Eldenheim to the manager's mod list (Gale's database is backed up first,
to `data.sqlite3.before-eldenheim`). Nothing is downloaded.

The same installer updates, repairs and uninstalls, for the plugins folder chosen:

- **Update** (Install, once installed): installs this Setup's version and
  keeps your settings. The data is made again only when it is missing, when
  this Setup makes different data, or after a game patch; otherwise it takes a
  second.
- **Repair**: checks every installed file against what Setup installed,
  replaces the plugin's files, makes the data again if any of it changed, removes
  anything else from the mod's folder, and re-adds the mod manager's entry.
- **Uninstall**: deletes the mod's folder (with any test files in it), its config
  (`BepInEx\config\com.gargantua.eldenheim.cfg`, so your settings too), its
  entry in the mod manager's list, and Setup's own work folder and backup.

To build the exe: `python installer/build.py [VALHEIM_DIR]` (needs the .NET SDK
and `python -m pip install pyinstaller zstandard`). It writes
`build/package/EldenheimSetup-<version>.zip`, which holds the plugin and the
tools but nothing from Elden Ring, so it can be shared.

### Movesets

Eldenheim has 57 Elden Ring movesets. Any can be picked for any Valheim weapon class in F1 → 5 - Movesets

| Elden Ring weapon class | Movesets |
|---|---|
| Daggers | Dagger, Erdsteel Dagger, Main-gauche |
| Throwing blades | Smithscript Dagger |
| Straight swords | Longsword, Warhawk's Talon, Carian Knight's Sword |
| Light greatswords | Milady |
| Greatswords | Claymore, Knight's Greatsword |
| Colossal swords | Colossal Sword, Zweihander, Godslayer's Greatsword |
| Thrusting swords | Rapier |
| Heavy thrusting swords | Heavy Thrusting Sword, Great Épée |
| Curved swords | Curved Sword, Shamshir, Flowing Curved Sword |
| Curved greatswords | Curved Greatsword, Zamor Curved Sword |
| Katanas | Uchigatana, Serpentbone Blade, Meteoric Ore Blade |
| Great katanas | Great Katana, Rakshasa's Great Katana |
| Twinblades | Twinblade, Godskin Peeler |
| Axes | Battle Axe, Hand Axe, Warped Axe, Smithscript Axe |
| Greataxes | Greataxe, Crescent Moon Axe |
| Hammers | Club, Morning Star, Stone Club |
| Flails | Flail |
| Great hammers | Great Hammer, Smithscript Hammer |
| Colossal weapons | Colossal Weapon |
| Spears | Short Spear, Smithscript Spear, Cross-Naginata |
| Great spears | Great Spear |
| Halberds | Halberd, Banished Knight's Halberd, Guardian's Swordspear |
| Reapers | Reaper |
| Whips | Whip |
| Fists | Fist |
| Hand-to-hand arts | Dryleaf Arts, Dane's Footwork |
| Claws | Claw, Raptor Talons |
| Beast claws | Beast Claw |
| Torches | Torch |

The Smithscript weapons throw when Elden Ring's do, usually on heavy attacks.
- Spear: Heavy1, Running Heavy
- Axe: Heavy1, Jump Heavy
- Daggers: Every attack
- Hammer: Heavy1 (sledgehammers use their AoE if fully charged)
- ThrowSpears setting removes the spear from your inventory when thrown.
- Throws while locked on fly at the target, lifted to make up for the drop at that range.
- Two-handing the Dagger puts one in each hand and throws from both.

Feint attacks come with the classes that have them in Elden Ring.
The Carian Knight's Sword guards while its heavies charge: a blocked hit doesn't stop the attack. 
Hand-to-hand arts kick with the feet: their kicks strike where the foot is.

Attacks chain as the game's script has them: a light after a run heavy, first
heavy or guard counter is the chain's second swing, and after a feint it's the
backstep's attack. Milady's unique attack chain is included.

### Attacks list for players new to Elden Ring

- **Light attack combo:** press light attack again as each swing ends to go
  on to the next (up to six, depending on the weapon).
- **Heavy attack:** tap heavy for a quick heavy, or hold it to charge a
  stronger one that staggers more. Let go to swing. A second heavy
  after the first does the follow-up heavy.
- **Running attacks:** light or heavy while sprinting.
- **Rolling attack:** light after a roll.
- **Backstep attack:** light after a backstep (the dodge with no
  direction held).
- **Crouch attack:** light while crouching.
- **Jump attacks:** light or heavy in the air, striking as you come down.
- **Guard counter:** heavy right after blocking a hit. Plays the Elden Ring sound effect.
- **Feints:** press dodge while charging a heavy: you hop back instead of swinging.
  Classes that have them: Curved Swords, Thrusting Swords,
  Heavy Thrusting Swords, Beast Claw, Smithscript Dagger, Great Katanas
- **Left-hand attacks:** with a weapon in the left hand, the guard button
  attacks with it. With two weapons of the same class, it plays the
  power-stance combo (and its running, rolling and backstep attacks).
- **Two-handing:** hold Use and press light attack (right weapon) or guard
  (left weapon) to hold one weapon in both hands, with its two-handed
  moveset. Do it again to go back to one hand.
- **Smithscript throws:** the Smithscript Dagger throws itself on every
  attack, and the Smithscript Spear, Axe and Hammer on their heavies. The
  weapon reforms in your hand. Throws go where the camera looks, or at the
  target when locked on.

After a running, rolling, backstep or heavy attack, or a guard counter, a
light attack goes on as the combo's second swing.

More settings under F1 → 2 - Controls:

- `ValheimEmptyHanded`: with nothing in either hand, Valheim's own movement,
  jumping and animations take over (as with a tool or a bow).
- `GuardTowardCamera`: while guarding unlocked, the player faces the crosshair
  and moves sideways and back as when locked on.
- `CameraAutoRotation` (on, as in Elden Ring): with a gamepad and no lock-on,
  the camera gently turns to follow the way you move; off, only the right stick
  turns it.
- `FreeAimVertical` (default 10): while unlocked, attacks aim up or down the way the
  camera looks by up to this many degrees. 0 turns it off.

And under 6 - Combat (server), `WeaponKnockbackScale` scales how far hits push what
they strike (1 = Valheim's weapon knockback, 0 = none).

## Controls

These are the defaults. To rebind, open F1, click "Set", then press a
button.

| Action | Keyboard / mouse | Gamepad |
|---|---|---|
| Roll / backstep (tap), sprint (hold) | Valheim's Run (Left Shift) | B |
| Jump | Valheim's Jump (Space) | A |
| Light attack | Valheim's Attack (LMB) | RB |
| Heavy attack (hold to charge) | Valheim's Secondary Attack | RT |
| Guard / left-hand attack; guard counter = heavy after a block | Valheim's Block (RMB) | LB |
| Crouch | Valheim's Crouch | L3 |
| Walk (toggle) | Valheim's ToggleWalk | — |
| Lock on (with nothing to lock: recenter the camera) | Caps Lock | R3 |
| Reverse backstep (not locked on): backstep, then immediately press a direction (S goes forward, A right, D left) | — | — |
| Switch lock-on target | flick the mouse left/right | right stick left/right |
| Two-hand right / left | hold Valheim's Use + Attack / Block | hold Valheim's Use (A; X in its alternative layouts) + RB / LB |

With an empty left hand, the guard button throws a left punch, as in Elden
Ring; `EmptyHandGuard` (F1 → 2 - Controls) makes it guard with the right
weapon instead, Valheim-style. The guard can be raised while sprinting.

### Weapons in the off hand

To put a melee weapon in the left hand, hold the `TwoHandModifier` button
(by default Valheim's Use: E on keyboard; A on gamepad, or X in Valheim's
alternative gamepad layouts) while you equip it
from the hotbar or the inventory (with the inventory open, hold Use and
right-click the weapon; Use closes the inventory when let go, unless it
equipped something). On a gamepad, hold Use a moment, then equip the weapon in
the inventory as usual (a quick tap of Use still equips into the right hand). It replaces a shield or torch in that hand.
Equipping without holding the button puts the weapon in the right hand, as
usual.

Valheim's two-handers also work in either hand, with a shield, torch or
weapon in the other. They are held one-handed then, and two-handed when
alone. Sheathing (R) and unsheathing keep each weapon in its hand.

- **Power stance:** with two weapons of the same class, one in each hand,
  the guard button plays the power-stance moveset. They cross on
  the back while sheathed. As in Elden Ring, a Main-gauche in the left hand also
  power-stances with a Rapier in the right (the Rapier's power stance).
- **Paired weapons:** Valheim weapons that are already a pair (knuckles/claws,
  Skoll and Hati, the dual axes) take both hands naturally. Claws are
  held two-handed; Skoll and Hati and the dual axes play their class's power
  stance.
- Two-handing one weapon puts the other hand's item on the back, with
  Valheim's equip sound.

**Stamina:** Sprinting, jumping and dodging cost what
they cost in vanilla (with the Run and Dodge skills and gear). A first light
attack costs the weapon's own attack stamina, and other attacks cost Elden
Ring's proportion of that.
Attacks follow Elden Ring's logic (F1 → 7 - Stamina & Movement → `NegativeStamina`):
any attack can start while there is stamina left, whatever it costs. One that
empties the bar keeps it empty (flashing red) until that attack ends, so the
combo can't continue; then it refills as usual. With the option off, an attack
needs its whole cost to even start like in Valheim. Stamina doesn't refill during an action until its
recovery frame. Each hit is charged to the weapon that swings it, so
each weapon in a power stance pays its own stamina cost.

**Fall damage** Falls do Valheim's damage (100 at most). A fall
that does more than 50 damage ends in Elden Ring's heavy landing animation.

**Separate dodge and sprint buttons.** By default one button does both: tap
to dodge, hold to sprint. To give each its own button:

1. Open F1 → *2 - Controls* and turn on `SeparateSprint`.
2. Rebind one of the two, because both start on the same button. Under
   *3 - Keyboard & Mouse*, `Dodge` and `Sprint` are both Left Shift; under
   *4 - Gamepad*, `Dodge` is B and `Sprint` is L3, which is also `Crouch`.
   Click "Set" next to `Dodge` or `Sprint` (or `Crouch`) and press the new
   button.

`DodgeTrigger` (also under *2 - Controls*) sets
whether the dodge comes out on Release (as in Elden Ring) or on Press.
Press fits separate buttons best, since with `SeparateSprint` off it leaves no way
to sprint.
If using a gamepad, `LeftJoystickInventory` lets the left
stick walk while the inventory is open (only the D-pad moves the selection) and `DodgeInInventory` makes B
dodge and sprint instead of closing it (Y closes it). A gamepad button still
held when a menu closes or the game unpauses does nothing until it is let go
(held keys carry on, so a sprint goes on past the inventory).

**Jumping over attacks:** as in Elden Ring, your legs can't be hit in a
  jump. Once you're more than 0.35 m off the ground, a blow that lands below
  your hips misses, with no hit at all.

**Weapon class moveset defaults:** 
- BareHands → Fist
- OneHandedSwords → Longsword
- TwoHandedSwords → Claymore
- Knives → Dagger
- OneHandedClubs → Morning Star
- TwoHandedClubs → Great Hammer
- OneHandedAxes → Hand Axe
- TwoHandedAxes → Greataxe
- Polearms → Halberd
- Spears → Smithscript Spear
- Knuckles → Claw

Sledgehammer AoE comes with fully charged heavies, guard counters, 
and the Smithscript Hammer's fully charged throws (F1 → 6 - Combat → `SledgeAoeChargedOnly`;
if off, uncharged heavies slam too, which is overpowered).

Therzie's Warfare:
- PairedSwords → Power stanced curved swords
- Scythes → Reaper
- ThrowingAxes → Smithscript Axe

Therzie's weapons follow their Valheim type above.
Each weapon's special (its secondary attack: the dwarven lightning, Doombringer's
meteor, the Hellblade Cleavers' fire, the fists' roars) comes with the heavies.
Some only activate on a fully charged heavy: the big ones (a lightning strike,
anything spawned or shot) and the war pikes' dust burst (also on guard
counters). Its sledges slam as vanilla's do, and its on-hit effects
(Vampirism, Bleeding...) count Eldenheim's hits.

## Sound

Attacks, footsteps, rolls, jumps and landings use Valheim's own sounds and dust.
Three sounds come from Elden Ring:

- the guard counter (F1 → 9 - Sound; quieter than the game's mix by default);
- the guard break;
- being parried (`ParrySound`, in place of Valheim's).

Each is the recording that the matching Elden Ring animation plays on its first
frame (`a000_019500` for the guard break, `a000_005800` for being parried).
`tools/pick_sounds.py` copies those three recordings from the sandbox's
`assets/sounds/` into `build/sounds/`, and the build copies them and
`assets/player_sounds.bin` to the plugin. Without those files, the three moments play only Valheim's
sounds.

The F1 sections marked *(server)* (6 - Combat, 7 - Stamina & Movement) follow
the server's values in multiplayer.

## How it is built

| Part | What it is |
|---|---|
| `sandbox/` | The sandbox itself (MIT, Funny-Bones and contributors), at the version Eldenheim's data is built from. Its tools read your game files. |
| `src/Eldenheim.Sim` | The sandbox's `src/sim`, ported line for line to C#. Matches the Rust sim exactly over the whole 502 s demo (`tests/Eldenheim.Sim.Replay`). |
| `src/Eldenheim` | The BepInEx plugin. It ticks the sim at 60 Hz inside Valheim's motion step, moves the body through PhysX, and plays the animations through a playable output that writes the whole pose. |
| `tools/export_data.py` | Exports the extracted data (`eldenheim_data.bin`) from the sandbox's generated `extracted.rs`, without Rust. |
| `tools/simtrace` | A Rust harness that compiles the sandbox's own `sim` and `demo`. It records the demo, tick by tick, for the replay check (and exports the same data file as `export_data.py`). |
| `tools/bake_unity.py` | Bakes every clip the sandbox plays as bone rotations, from your own game files. |

## Manual setup (your own copy of Elden Ring)

This repository contains no files from Elden Ring or Valheim, and none are
shipped. The mod's data is generated from your own installed copy of Elden Ring.
Shadow of the Erdtree is not needed. Its weapon styles are in the base game's own
Data3 archive since patch 1.12, so every up-to-date copy has them.

You need Python 3.10+ (`python -m pip install zstandard`; outside Windows also
`pycryptodome`), the
.NET SDK, and Valheim with BepInEx. Run everything from this folder. Rust is only needed for the replay check
below.

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
   python tools/export_data.py build/eldenheim_data.bin
   ```

   ```bash
   python tools/pick_sounds.py
   ```

5. Build the plugin:

   ```bash
   dotnet build src/Eldenheim -c Release
   ```

The build copies the plugin and the generated files to
`C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\plugins\Eldenheim` (`-p:ValheimDir=...` for
elsewhere). Valheim must be closed.

That folder is also a Gale package: select everything inside it (not the
folder itself, so `manifest.json` sits at the zip's root), zip it, and use
Gale's "Import local mod". `python tools/package.py` does this for you: it
builds into a clean folder and writes
`build/package/Gargantua1866-Eldenheim-<version>.zip`. Never share the
generated files: they are derived from Elden Ring.

To check the C# sim against the sandbox, record the sandbox's demo, then
replay it through the C# sim:

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
2. In `sandbox/`, make three additions, in the same order in each list:
   - `(name, row)` to `WEAPONS` in `tools/extract.py`, before the shield;
   - its striking line to `BLADES` in the same file;
   - a stand-in shape to `WEAPON_PARTS` in `src/rig.rs`.

   The row's category and style (byte 0xEE) pick the animations. A style
   with no animations in the game files plays its category's.
3. Rebuild the data in the order under Manual setup (setup.py,
   bake_unity, export_data, simtrace demo) and run the replay check.
4. Add the menu name to `Settings.ErClasses` (and a note to a Movesets entry
   if it suits a Valheim weapon type).

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
