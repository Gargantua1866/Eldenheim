# Eldenheim — journal

## 2026-10-27 — Milestone 0 spikes

Plugin: `src/Eldenheim.Spikes` (throwaway). Driven from `commands.txt` beside
the DLL, results in `results.log` there (see `Commands.cs` for the command
list). Test world `ERTest`, character `ertest` (black-metal sword + banded
shield). Valheim run windowed, 1280×720, Unity 6000.0.75f1. Soulsheim was
disabled during the runs and restored afterwards. The spike's `AutoLoad` is
now off.

Clips for 0b were baked with
`python tools/bake_unity.py build/eldenheim_anims.bin a000_000000 a000_020000 a000_020100 a023_030000`.
That gives character-space rotations and positions of all 150 HKX bones at
30 fps (Havok → Unity is a 180° turn about Y: `(-x, y, -z)` and
`(-qx, qy, -qz, qw)`, with no mirroring).

### 0a — an animation job on Valheim's player: PASS, with a change of design

| Try | Result |
|---|---|
| Inject a job into `animator.playableGraph` (between the controller and its output) | **No effect at all.** Bone positions were identical to vanilla with an attack pose at weight 1. The Animator's own graph can't be extended. |
| Second graph, own `AnimationPlayableOutput` on the Animator, job writing only the mapped bones | Overrides the controller, but everything the job does **not** write is reset to defaults: the hips fell to −0.21 m and the character sank into the ground. |
| Same, with the job writing the **whole pose** (all 22 mapped bones' rotations and positions, and every other transform under the Armature at its bind pose) | **Works.** Test pose: hips exactly at the bind height (0.910 m), a clean A-pose. Sword and shield follow the hands. Output weight 0.5 gives a pose halfway between, so hand-offs can fade. |

- The player Animator's update mode is **Fixed**.
- **Humanoid foot IK runs after our output.** The feet sat at y = 0.120 in
  every pose, with their direction off by 29–47°. Eldenheim must turn off
  Valheim's foot IK (the IK pass for the player) while it owns the pose, and
  place the feet itself.
- On the humanoid rig, forearm **twist** is redistributed: overrides of up to
  30° of twist, but the forearm's direction is within 1.4°.

### 0b — rotation bake + retarget: PASS

Retarget: `target = er(t) · er_rest⁻¹ · A⁻¹ · valheim_rest`, in character
space, with A turning each ER bone's rest direction onto Valheim's (both rigs
are A-poses; A is under 20° except the neck at 25°). Valheim's rest comes from
the mesh bindposes.

Measured direction error (bone → child, after the Animator ran):

| Pose | Spine/arms/legs | Feet (overwritten by IK) |
|---|---|---|
| ER rest | 0.1–2.2° (neck 5.8°) | 32–35° |
| `a000_000000` idle, frame 0 | 0.3–2.9° (neck 4.6°) | 29–47° |
| `a023_030000` longsword light 1, frame 30 (mid-swing) | 0.3–1.4° (left leg 7–12°) | 13–29° |

The Valheim rig: 53 skinned bones, `Player(Clone)/Visual/Armature/Hips`,
Mixamo names including all fingers (`LeftHandIndex1..3`, `…Thumb1..3`,
`…Middle`, `…Ring`, `…Pinky`), plus `Jaw`. The bind pose is an A-pose, with
the hands at 1.05 m (ER's: 1.03 m).

### 0c — body takeover at 60 Hz: PASS for ground and slopes; steps need a step-up

`Character.UpdateMotion` is skipped for the local player. A 60 Hz accumulator
moves the body through PhysX by velocity, clears `m_rootMotion`, holds
`m_maxAirAltitude` and turns off `useGravity`.

| Test | Result |
|---|---|
| Flat stone, 4–6 m/s, four directions | 98–100% of the intended distance, ground gap 0, no vertical jitter |
| Gentle slopes | 99%, ground within 1.7 cm, largest step per tick 1.5 cm |
| Slope with the ground probed only under the feet | 77%: the capsule drags. Fixed by walking along the ground plane |
| Into a character (skeleton) | Blocked, no tunnelling |
| `wood_stair` (a 27° box ramp plus landing boxes) from its low end | Climbed 0.9 m, then stopped at a 0.15 m lip onto the top landing |
| `stone_stair` (convex mesh with real steps) | Not climbed. That piece spawned 0.2–0.4 m above the ground, so the first riser was over the 0.35 m step height; retest on level ground |

**Design consequences:**
- Walk along the ground normal taken at the capsule's front edge, scaled to
  keep the sim's horizontal speed (as Valheim does with `m_lastGroundNormal`).
- Add a real step-up: sweep the capsule raised by `STEP_HEIGHT`, then settle
  down onto the step.
- A piece spawned facing the player rises *away* from them. The first stair
  test walked into a stair's 1 m high top end, which was correctly a wall.

### 0d — Shift as dodge: PASS

| Run held for 4 s | IsRunning | Stamina | Noise |
|---|---|---|---|
| Vanilla | 558/558 frames | −26 | 30 |
| Eldenheim takeover, `run`/`autoRun` cleared in `SetControls`, `m_running` false | 0/429 frames | none drained (regenerating) | **0** |

Eldenheim's own reader still saw Run held. Valheim reads no input while its
window is unfocused (`PlayerController.TakeInput()` → `SetControls` with all
false), so automated tests inject at `SetControls`.

**Consequence:** under the takeover, the player makes **no noise**, so enemies
can't hear them. The real controller must set the noise itself (walk, run,
sprint and roll levels).

### Next

1. The step-up sweep and a stairs retest on level ground (stone, wood, the
   top lip).
2. Turn off the player's foot IK while Eldenheim owns the pose, and confirm
   the feet then follow the clip.
3. Then milestone 1: the sim port and the differential tests.

## 2026-10-27 — Milestone 1, and the first playable Eldenheim

### Milestone 1: the sim port — PASS, exact
- `tools/simtrace` (Rust) compiles the sandbox's own `src/sim` and `src/demo.rs`
  by `#[path]`. It exports every extracted table to `eldenheim_data.bin`:
  1255 actions, 122 air attacks, 25 weapons, 6 swaps. It also plays the demo
  headless and records each tick's input and state to `demo_trace.bin`
  (23,080 ticks, 385 s).
- `src/Eldenheim.Sim` is the line-for-line C# port, with glam's float
  semantics.
- `tests/Eldenheim.Sim.Replay` replays the trace, applying the director's
  edits between ticks: **0 of 23,080 ticks differ**, across 128 distinct
  states. Position, HP, stamina, frames and the dummy are bit-identical; the
  worst yaw error is 2.4e-7 rad.

### The plugin (`src/Eldenheim`)
- **Full bake:** `bake_unity.py --all` writes every clip the sandbox plays
  (its own `player_anims.bin` list): 1530 clips, 94,307 frames, 55 bones,
  int16 rotations, 52 MB, 35 s.
- **Takeover:** Eldenheim owns the player while the item maps to an ER class.
  - The sim ticks at 60 Hz inside `Character.UpdateMotion`.
  - The body moves by velocity, and collision is read back from PhysX.
  - `PhysicsLevel` gives the sim its `height`/`slide` from sphere casts of the
    capsule's width.
- **Animation:** `rig.rs`'s chooser, ported in rotations: cross-fades, the
  carry, guard and swap arm layers, and the hands closed on the weapon.
  - The retarget covers 52 bones, all fingers included.
  - The right attach point turns so the held item's blade lies along ER's
    `R_Weapon` Y axis (measured: **0° off**).
  - Foot IK onto the real ground, and the body eased over steps.
  - Valheim's foot IK and head look are off while Eldenheim owns the pose.
- **Combat:**
  - The sim's blade capsules are swept against Valheim colliders, and
    `HitData` is built like `Attack.DoMeleeAttack`'s, scaled by ER motion
    value relative to Light1.
  - Swing sounds and the white trail come from the weapon's own effect lists
    (`sfx_sword_swing` trailStart, `fx_swing_camshake`). They fire on each
    hit's first active frame, measured: Light1 at frame 13.0 (window 13–16),
    Light4 at 11.0 (11–14), GuardCounter at 18.0 (18–21).
- **Incoming hits:** `Character.RPC_Damage` goes through `Sim.ReceiveHit`.
  I-frames drop the hit, a block runs through Valheim's own `IsBlocking`, and
  any other hit plays ER's hurt reaction graded by damage share.
- **Stamina:** a view of Valheim's (320 → 97 ER units). Spending goes through
  `UseStamina`.

### Verified in game
- **Movement:** run at ER's 4.01 m/s, backstep, rolls, crouch, jumps and
  falls. Climbs the spawned stone stairs (y 35.75 → 36.43) and runs off them.
- **Attacks:** the light chain 1→2→3→4, heavy, charged heavy, guard counter.
- **Hits:** the sparring dummy, Valheim training dummies, a Greyling.
  A swing from 4 m misses, as in ER.
- **Guard:** blocks the dummy's frontal slam. A Greyling's hit from behind
  got through (`Hurt(Small, Back)`), as ER's guard arc says.
- **Lock-on:** targets the dummy and Greylings; the camera follows the target.
- **The demo:** the whole sandbox demo plays in Valheim on the sandbox's
  arena, built in the sky. It was checked by pausing at 19 points and at 4
  swing frames. No skin wrapping or stretching was seen: only rotations are
  written, so bone lengths stay Valheim's. The worst twist in a measured
  window was 88° (forearm).

### Known gaps
- The demo shows Valheim's held item for every ER weapon class. Only the
  animations change.
- Valheim's speed modifiers (food, armour, encumbrance) don't apply to the
  sim's speeds yet, and only a Valheim two-handed item sets a two-handed grip.
- The paired / left-hand weapon slot (milestone 6) and remote-player pose sync
  (milestone 5) aren't started.
- In free play, lock-on only targets Characters (not training dummies, which
  are pieces).

## 2026-10-27 — Play-test fixes and the left hand

The user's first hand play-test listed 17 items. What changed, and how each
was checked (scripted runs in `build/tests/t1..t8.txt`; screenshots with the
new `shot` command, which the game takes itself on the frame):

| # | Item | Fix | Checked |
|---|---|---|---|
| 1 | Head tilted up | The head took the neck's 25° rest correction; it now takes none (both rest heads face forward) | Side shot: level |
| 2, 9 | No sprint/roll/jump sounds or dust; walking dust repeating | Valheim's `FootStep` fires on its own clip's curve while the animator's `forward_speed`/`sideway_speed` are up; the takeover stopped setting them (stuck at 0, or at the last speed). They are held at 0, and `Steps` plays a step when a foot comes down in the ER clip (with `m_running` for run dust), plus Valheim's jump, dodge and land effects | `run step`, `walk step`, `land` in status |
| 3 | No movement with the inventory open | Valheim only holds back attack/block/crouch then; so does Eldenheim | Walked 6 m with it open |
| 4 | Stutter | 60 Hz ticks fall unevenly into 50 Hz physics steps (1 or 2 per step). The body now follows the sim a tick behind, interpolated; PhysX collision is read back as the shortfall from where it was sent | Frame-to-frame speed spread 1.1 % running, 0.0 % sprinting |
| 5 | Sparring dummy | Removed from free play (F1, console, lock-on); it stays in the demo, which is scripted around it | — |
| 6 | Fall damage | Capped at 100, like Valheim's | 40 m fall: 90 |
| 7 | Spears backwards | A spear is held near its head, so the longer half is the shaft: its axis is flipped | Blade 0° off ER |
| 8 | Atgeir left hand off the shaft | Two-handed, an arm IK keeps the left hand where ER has it relative to the right weapon bone, fading out when ER's hand leaves the handle | Grip weight 1.00; two-handed longsword matches the sandbox |
| 10 | Battle axe misses | Hits now also sweep the held item's own length (from its mesh), a tick to a tick | Greyling hit at 2.8 m |
| 11 | No guard while sprinting | Guard held cancels the sprint (sim change; replay still 0/23,080 differing) | — |
| 12 | Guard pose | Arm layers now keep the layer clip's arm directions, as rig.rs does (it moves joints by the chest offset only; the port was turning them by the chest's rotation). With an empty left hand the guard button is a punch, as in ER (the user's screenshot) | Shield guard matches the sandbox |
| 13 | Guard counter sound | `ErSounds` (from the Krom port) plays only the recordings no other action uses, at 0.5 by default | Loads; 1 of 13 sounds queued |
| 14 | Claws | Knuckles/claws → Claw, paired | status |
| 15 | Krom | → Greatsword | status, shoulder carry |
| 16 | Attack stamina | Attacks cost the weapon's own attack stamina (Valheim's skill and equipment modifiers) for Light1, others in ER proportion | Light1 black metal sword: 5 of 308 |
| 17 | Left hand / paired | Below | — |

**Left-hand weapons (`Hands.cs`):** equipping a one-handed weapon with the
two-hand modifier held puts it in the left hand (Valheim's `EquipItem` is
skipped for it). Equipping a right weapon would make Valheim unequip a
non-shield left item, so the left weapon is hidden from it for the call and
put back. `ShowHandItems` (R) and `EquipInventoryItems` (load) route the left
weapon back by a mark in its custom data. Visuals follow rig.rs's show():
two-handing the left armament reparents its instance to the right attach
point and hides the right weapon; two-handing hides the left hand's item.
Each hand's attach point is turned so its item lies along ER's R/L_Weapon
(0° measured on both). Damage, sound and trail come from the hand that swings
(paired: whichever blade is nearer the hit).

**A bug found on the way:** with knuckles equipped, "the skinned mesh with the
most bones" was the knuckles (they share the body's bone list) and the
retarget read their bind pose: whole sessions posed wrong. The body is now
`VisEquipment.m_bodyModel`, and the pose job no longer binds held items.

**Open:** guarding with the right weapon when the left hand is empty (Valheim
style) would be an option against ER's rules; asked the user.

## 2026-10-27 — Second play-test

| # | Item | Fix | Checked |
|---|---|---|---|
| 1 | Sprint free; jump/roll too dear | Moving costs Valheim's own: `CheckRun`'s drain (Run skill, gear, status effects, raises Run), `OnJump`'s cost (raises Jump), `GetDodgeStaminaUse` (Dodge skill; raises Dodge 0.1). ER's own moving costs are ignored | Sprint ~5/s at low skill |
| 2 | Fall damage | Valheim computes it: `m_maxAirAltitude` is tracked from Valheim's ground contact (the sim lands a step before PhysX touches, so resetting on the sim's landing erased the fall). The sim no longer hurts or kills on falls (`ExternalFallDamage`); the heavy landing needs 12 m (Valheim's 50 damage) | Not hit-tested: the test character now has 25 HP |
| 3 | Atgeir left hand | The wrist was on the handle (0.08 m from the shaft line, as in ER), but the fingers were open: ER closes the off hand in a layer the sandbox lacks. The right hand's grip is copied to the left while it holds the handle | — |
| 4 | Guard while sprinting | `GuardWhileSprinting`: the guard rises over the sprint, which continues | — |
| 5 | Shield angle | Shields line up their face (thinnest extent) with ER L_Weapon's Z | Guard: face 0.95 forward |
| 6 | Left weapons backwards | The left weapon bone is the right one mirrored (rig.rs turns left models half a turn): blades lie along its -Y | Krom in the left points forward |
| 7 | Pair items | Knuckles/claws, Skoll and Hati, dual axes: two-handed on equip, nothing in the other hand | status |
| 8 | Claw hits | Hits now sweep every open window for its whole length, remembering targets per window (was: first contact closed the window); fists and claws sweep the real hands; motion values scale against Light1's *total* | Claw Light1: both windows land, ~half damage each |
| 9 | Turning | `MoveAlongWish` + Valheim's `m_turnSpeed`/`m_runTurnSpeed` (300°/s): you move where you press, the body turns after | 0→86° in 0.3 s |
| 10 | Smithscript Spear | Not done: its throw is DLC data (`DLC.bdt`), which the sandbox's unpacker has no key for. Base a036 has no throw | — |
| 11 | Krom | ER greatswords all use category 25 (sandbox "Claymore"); "Greatsword" is 26, the colossal swords. Krom is back on Claymore | status |
| 12 | Attack stamina | The reference counted `ActionDef.Stamina`, which grounded attacks never spend; now Light1's hits only. Valheim's own skill/gear reductions still apply (Krom 20 → 17.8) | log |
| 13 | Battle axe through enemies | Same fix as 8: a bush, rock or first enemy no longer uses the swing up | — |
| 14 | Empty-hand guard | `EmptyHandGuard` option (off): the weapon's two-handed guard, left hand on the handle | — |
| 15 | Two-handers + off-hand | Any playable melee weapon can go in either hand; a right two-hander keeps the off-hand (Valheim's `EquipItem` is kept from unequipping it) | Krom+Krom, Krom+shield |
| 16 | Charged/counter bonus | Applied through ER's motion values: Longsword 1H Light1 1.00, Heavy 1.25, charged 1.60, guard counter 1.25 | `mv` command |

Test aids added: `give`/`remove`/`unequip`, `cleanup PREFIX` (removes
spawned pieces with no creator, never player-built ones), `hitdebug on|off`,
`mv WEAPON`, `leftaxes`. Test-spawned training dummies were left behind by
`clear` (it removes Characters, not the piece); `cleanup` removed two.

## 2026-10-27 — Third play-test

| # | Item | Fix | Checked |
|---|---|---|---|
| 1 | No fall damage | `Character.OnCollisionStay` ignores contacts while `m_jumpTimer < 0.1`, and `m_jumpTimer`/`m_lastGroundTouch` are advanced only by the `UpdateMotion` Eldenheim replaces: Valheim never touched ground. Both timers are advanced again; `m_maxAirAltitude` follows vanilla (`!IsOnGround()`) | 6.5 m fall: 16 damage (formula 15.8) |
| 2 | Atgeir left hand | The left hand's own grip point (`LeftHand_Attach`) is put on the shaft line, not ER's wrist offset | — |
| 3, 14 | Atgeir in the ground; spear held at its tip | Poles slide along their shaft until the tip reaches ER's (halberd 1.85 m, spears 1.66): atgeir −0.39 m, spear +0.53 m | log |
| 4 | First guard counter silent | Its recordings are preloaded when Eldenheim takes over (Vorbis loads asynchronously) | Not audible-tested |
| 5, 6 | Terms | "Power stance": two same-class weapons; "paired weapons": one item for both hands | README |
| 7 | Power stance stamina | Each hit is charged to the blade that swings it | bronze 4.5 / gold 8.9 |
| 8 | Cheap attacks near empty | As vanilla: an attack whose first hit can't be paid (+0.1) doesn't start; what starts is charged in full, per hit, as its window opens (was: the sim's clamped drop) | 4.0 < 4.0+0.1 refused |
| 9 | Debug list | F1 → Debug → CombatLog: recent stamina and damage, totals overall and over 10 s | screenshot |
| 10 | Early Axes in one hand | Paired items are skinned to both hands; the two-handed grip IK pulled the left hand onto the right handle. No grip IK for paired weapons; Skoll and Hati and the dual axes play their class's power stance | screenshot |
| 11 | Sheathed power stance | The left weapon is mirrored across the spine: the two cross | screenshot |
| 12 | Two-handing with an off-hand | The idle hand's item goes on the back (`VisEquipment.AttachBackItem`) with Valheim's equip sound | screenshot |
| 13 | Turning | Back to the sandbox's rates and facing-led movement | — |
| 15 | Motion values by class | They differ (charged heavy: dagger 1.50, greataxe 1.73–1.78, Claymore 2H 0.50+1.50); each class's own are used | `mv` |
| 16 | Power-stance crouch attack | `PairedCrouchAttack`: crouched, the guard button plays the power-stance rolling attack | status |
| 17 | Claymore / Inseparable Sword | EquipParamWeapon byte 0xEE is the weapon's style: Claymore 125, Knight's Greatsword and Inseparable Sword 128, Colossal Greatsword 136. The sandbox's extractor (local change) now uses the style's animation where it has one, and has an "Inseparable Sword" class; F1 → Weapons → TwoHandedSwords picks one | `a128_032000` plays |
| 18 | Sprint hunch | `SprintLean` (0.5): the upper body turns back towards upright about the pelvis while sprinting | screenshot |
| 19 | Braced guard | ER's guard clips carry braced legs (feet 0.77–0.84 m apart vs 0.55, hips 10 cm lower); standing still, the legs take them | `clipfeet` |
| 20 | Smithscript Spear | Its style (247) is in `chr/c0000_dlc02.anibnd.dcx`, patched into Data3 (DLC.bdt holds other files; its BHD5 is version 255). The heavies' bullet event (TAE 2) becomes the hit window (MV 1.30 and 20 stamina fallbacks: the game has no behaviour rows for them); Eldenheim fires the weapon's own Valheim spear projectile there, with no item (no drop, no loss) and the hit's damage | `bronzespear_projectile` |

Sandbox changes (local, uncommitted, in the user's checkout): `tools/extract.py`
(weapon styles, DLC packs, two classes, the throw), `tools/bake_anims.py`
(DLC packs), `tools/unpack_game_files.py` (DLC anim packs), `src/rig.rs` (two
stand-in models). Regenerated: `src/sim/extracted.rs`, `assets/player_anims.bin`.
Its 63 tests pass; Eldenheim's replay is exact over the new 23,590-tick demo.

## 2026-10-27 — Fourth play-test

| # | Item | Fix | Checked |
|---|---|---|---|
| 1 | Roll attacks queued late | `EarlyRollAttacks`: attacks pressed anywhere in a roll/backstep are kept for its attack window | — |
| 2 | Crouch lost during actions | `QueueCrouch`: a crouch pressed during any action toggles as it ends | — |
| 3 | Double jump on a double tap | `NoAirJumpBuffer`: jump presses in the air are ignored | one jump logged |
| 4 | Crossed sheath drifts | The left back weapon keeps a fixed offset from the right one (mirrored once) and rides on it | not seen clearly |
| 5 | No draw/sheathe/equip animation | A change of the right item plays ER's right-hand swap, a left-only change the left-hand one; the new item shows from the change-over frame | `swap:` log |
| 6, 7 | Atgeir on the great spear moveset; spear grip | The Halberd row's style (a210) is a great-spear-like moveset: the extractor keeps the Halberd on its category (`CATEGORY_ONLY`). All Valheim polearms are ER halberds | no a210 clips left |
| 8 | Power-stance sound from the right weapon | Each window's sound and trail are its own weapon's (the window's owner) | log |
| 9 | Power-stance damage from the right weapon; always two hits | Each hit window belongs to one hand (the blade nearest ER's capsule as it opens); only that weapon sweeps and damages in it | log |
| 10 | Trails gone after swapping | Hidden items were deactivated: `MeleeWeaponTrail` destroys its mesh in `OnDisable` and builds one only in `Start`. Items are hidden by their renderers now | — |
| 11, 12 | Bare fists: silent, hits missing | Fist tracking took over both hands whatever the left held (a left sword struck as unarmed); each hand is tracked on its own, and a bare left fist owns its windows. Unarmed's swing sound is `m_startEffect` (`sfx_unarmed_swing`) | log |
| 13 | Negative stamina | `NegativeStamina` (on): any attack starts with stamina left and can take the bar below zero; the bar flashes red until above zero. Valheim has no slower rate at zero: it refills at its own rate after its 1 s delay | −4 |
| 14 | Regeneration during attacks | Valheim's regen timer is held whenever the sim doesn't regenerate (before an action's recovery frame, a press waiting, the air) | — |
| 15 | Early Axes in one hand two-handed | Paired weapons ignore the two-hand toggle | stays OneHand |
| 16 | Light after a jump attack | `JumpAttackChains`: light after a jumping light (and the power-stance jump) goes on at Light2; heavies unchanged | — |
| 17 | Double hits in jump attacks | The landing act's windows that already struck in the air are skipped (`Act.Landed`) | — |
| 18 | Krom guard left hand | See below; with the pose binding fixed the left wrist sits 0.08 m from the shaft (ER 0.16) | screenshot |
| 19 | Spears thrown away | `ThrowSpears` (off): the spear itself flies, leaves the inventory and drops where it lands | `spear thrown away` |
| 20 | Charged throw | Charged throws scale by the moveset's own charged ratio (1.56 one-handed, 1.60 two-handed vs 1.30) | extractor |

**Bind pose with the Ashlands chest.** Loading in the Ashlands medium chest,
Unity refuses every `TransformStreamHandle` of the pose job ("could not
resolve … because the avatar is invalid") and never retries: the body stays
in its bind pose. Binding with the chest off and equipping it later works;
rebinding the animator doesn't help. The job now holds only the body's own
skeleton bones (not cloth colliders or other added objects), and a watchdog
switches to writing the pose onto the bones directly in `LateUpdate` when
most bones stay off what was set for a second.

Test aids: `target PREFAB DIST` (a creature that can't die or fight),
`setting KEY VALUE`, `comps`, `compsof NAME`; `give` now reports a full
inventory and `remove` takes back only what `give` added. An earlier run
with the old `remove` deleted the character's own SwordGold and one
SpearBronze (the inventory was full, so `give` had added nothing); both were
given back at quality 1.

## 2026-10-27 — Fifth play-test

| # | Item | Fix | Checked |
|---|---|---|---|
| 1 | Test character | `ertest` is disposable: tests may kill it and add or delete items | — |
| 2 | Negative stamina shown; chains went on | The bar stops at 0. An attack that runs it dry leaves you exhausted (bar held at 0, flashing) until that attack is over and its overshoot has refilled; nothing starts meanwhile, so the next press is a first attack. Rolls unchanged | out at Light1, next press after refill = Light1 |
| 3 | Stray second heavy after a charge | Heavy presses within 6 frames of the button coming up are ignored (`HeavyDebounce`; a bouncing button) | — |
| 4 | Atgeir animations broken | Eldenheim's bake takes its clip list from the sandbox's `player_anims.bin`, which was re-baked after it: the Halberd's a038 attacks were missing, so the pose sat on the idle frame. Re-baked | a038 clips play |
| 5 | Equipping mid-attack | `Player.InAttack` includes Eldenheim's attacks (Valheim then refuses equip, unequip and sheathe); drawing is refused too | equip refused mid-swing |
| 6 | Charged heavies | Fully charged heavies' swing sounds are 1.5× louder and a little lower | not heard |
| 7 | Chain damage | Already in: ER's motion values grow along a chain (Longsword 1.00, 1.01, 1.02, 1.03, 1.10) and each hit is scaled by them; Valheim's own chain multiplier is never used | data |
| 8 | Claws missing hits | One hand per window applies only to two different weapons; with one item in both hands (claws, fists) either hand lands | claw hits land |
| 9 | Early Axes, Skoll and Hati | Work like claws: `PairedOnLight` makes the light button play the power-stance set (run, roll, crouch and jump versions too) and the guard button guards (one-handed guard, no grip IK) | PairedLight1 on light |
| 10 | Hit-stop | `HitStop` (Visuals, off): ER's freeze of attacker and target on a hit | — |
| 11 | T-pose with tools | The failed pose job (chest armour, see above) threw every frame and left the animator in bind pose even after Eldenheim let go. In direct mode its graph is now destroyed | hammer with chest on |

## 2026-10-27 — Sixth play-test

| # | Item | Cause and fix | Checked |
|---|---|---|---|
| 1 | No damage taken | The sim judged a hit before Valheim's dodge check: a heavy hit started the knockdown reaction, whose 59 i-frames made Valheim drop the damage. Reactions are gone (item 3) | Skeleton hits take health |
| 2 | Stuck at 0 after a knockdown | Regeneration paused through the knockdown; no knockdowns now | — |
| 3 | Infinite poise | `NoFlinch`: no hurt reactions, no swap cancel; incoming stagger and push zeroed | — |
| 4 | Atgeir left hand (see below) | The shaft direction is now the longest mesh's own axis | both hands on the shaft |
| 5 | No debt below 0 | The bar only stays at 0 until the attack that emptied it ends; then Valheim's regeneration | — |
| 6 | Regen rate | `StaminaRegenRate` (Balance, 150): Valheim's `staminaregenrate` world setting while enabled | config |
| 7 | Late regeneration | Attack costs no longer start Valheim's 1 s regen delay; regeneration resumes at the action's recovery frame (`CancelMove`) | — |
| 8 | Landings from small drops | `MinLandingFall` 1.5 m (walking off less plays no landing), `StepDown` 0.6 m followed down | — |
| 9 | Charged sound unchanged | ZSFX rewrites volume and pitch every frame from its own values; its modifiers carry it now (×1.6, pitch 0.8) plus an octave-down copy | not heard |
| 10 | Fist elbow strike | Hand sweeps now run elbow to past the knuckles | both Heavy2Charge hits land |
| 11 | Extra attack after a jump attack | `NoAirAttackBuffer`: attack presses during an air attack are ignored | — |
| 12 | Club heavies | New sandbox class "Morning Star" (row 11050000): the category's own heavies (a033) instead of the Club row's style (a183); Valheim clubs map to it | data |
| 13 | Freeze with non-weapons | Creating the pose job's graph, when its handles fail (Ashlands chest at load), leaves the animator unable to write bones for the session, Valheim's own animation included; destroying it and `Animator.Rebind()` don't recover it. The pose is now written straight onto the bones in `LateUpdate`; no graph, no stream handles | bones move with the hook |
| 14 | Ladder | Valheim's wood ladder is four 0.66 m steps with `AutoJumpLedge` triggers. `Character.OnAutoJump` now hops the sim; a thin-ray check lets steep ramps through | climbed 35.3 → 41.6 m |
| 15 | LandRun on walking jumps | `LightLandings`: running landings only when sprinting | — |
| 16 | Air control | `AirSpeed`/`AirAccel`: 2.5 m/s² steering in the air, a small nudge in the jump itself | — |
| 17 | Turning at take-off | `JumpTurnFrames` 6: the jump faces the stick at once | — |
| 18 | Two-handers in the air | The left hand keeps its last grip on the handle through jumps and falls | grip 1.00 mid-jump |
| 19 | Feather fall | `MaxFallSpeed` from `SEMan.ModifyWalkVelocity`; no heavy landing; steering 12 m/s² towards run or sprint speed | 12 m drop at ~5 m/s |
| 20 | Grappling hook | It is a bow-type item: Valheim's own (Eldenheim also steps aside while `GrapplingPoint.m_localGrappler` is set) | Valheim animates it |
| 21 | Run and Jump skills | Sprint × `GetRunSpeedFactor` (1 + 0.25 Run, gear); jump height × (1 + 0.4 Jump) | — |
| 22 | Spear pickup to the left hand | The pickup key is the two-hand modifier: `Humanoid.Pickup` no longer routes to the left | — |
| 23 | Repeated perfect dodges | Valheim calls `HitWhileDodging` per collider a swing touches; now once per roll | — |
| 24 | Sneaking | `IsCrouching`/`IsSneaking` come from the sim (Valheim's read its animator); Sneak skill raised as `OnSneaking`; `SneakStamina` (off) brings back the drain | sneak True |

**Atgeir grip, root cause.** `Controller.BladeAxis` took the shaft as the
attach point's longest box axis (Z). Valheim's atgeirs sit 21° off that axis
in the hand (their box in attach space is 1.25 × 0.58 m across for a 3.05 m
pole; spears and swords read 0°), so the blade alignment, the pole slide,
the hit sweep and the left-hand grip line all followed a line beside the
visible shaft. The axis now comes from the longest mesh's own bounds. The
`shaft` command lists each mesh part in attach space for a new item.

Sandbox: "Morning Star" class added (`tools/extract.py`, `src/rig.rs`);
the data chain was rebuilt in order (extract → sandbox bake → Eldenheim
bake → simtrace data and demo); 63 tests pass, replay exact over 23,846 ticks.

## 2026-10-28 — Seventh play-test

Built and deployed; none of it checked in game yet (the user tests).

| # | Item | Cause and fix |
|---|---|---|
| 1 | Capes thrash | The pose moved to `LateUpdate` (sixth play-test), after MagicaCloth's `beforeLateUpdateDelegate`: the cloth simulated on last frame's body. The pose is now written first in that delegate (re-hooked each frame), `LateUpdate` only as a fallback |
| 2 | Movesets for every class | F1 → Weapons: one choice per Valheim type (one/two-handed swords, knives, one/two-handed clubs, one/two-handed axes, polearms, spears, knuckles), any ER class. Earlier movesets kept as options: Club, Battle Axe, Fist. The old `TwoHandedSwords` enum value `InseparableSword` is carried over |
| 3 | Axes → Hand Axe | The game's own names (WeaponName.fmg) show the sandbox's "Battle Axe" row 14000000 *is* the Battle Axe (category moveset); the Hand Axe is row 14020000, style a170. New sandbox class "Hand Axe" (lights from a170); Battle Axe kept as the option |
| 4 | Shamshir | New sandbox class "Shamshir" (row 7030000, style a156: its lights; heavies are the curved sword category's) |
| 5 | Lock-on dot, target switching | A dot on the target (`LockOnDot`); a mouse flick (`LockOnSwitchFlick` px within ~0.15 s) or the right stick pushed over moves the lock to the nearest target on that side |
| 6 | Stiff right arm one-handing big weapons | Stances 2/3 (Claymore, Greataxe, spears, Halberd) carry the right arm from the stance idle while moving, a still pose. The carry is now an offset from the plain idle, keeping 75 % of the run's arm swing |
| 7 | Charged heavy's deep sound late | Its copies play at pitch 0.8/0.5, so their loudest moment arrives 1.25×/2× later. Every swing sound now starts early by its recording's measured peak (÷ pitch, at most 0.4 s), so the peak lands on the window's first frame |
| 8 | NegativeStamina tooltip | Rewritten: no cost beyond zero any more |
| 9 | Rebinding | F1 input entries have "Set" (press the key, mouse or pad button; Esc cancels) and "Default" |
| 10 | Late regeneration | `Player.InAttack` (patched to include Eldenheim's attacks) also zeroes Valheim's regen in `UpdateStats` until the attack's last frame. The patch now steps aside inside `UpdateStats`; Eldenheim's own hold ends at the recovery frame |
| 11 | Fist two-handed Light2 | Its second hit is the left fist, which wasn't tracked while two-handing: a bare left hand is now a fist whatever the grip. Sounds out of sync: unarmed's swing sound peaks well after its start (vanilla plays it as the attack begins): see 7 |
| 12 | Hands off two-handers in jumps | The hold covered only the airborne part; now the whole jump (take-off, air, landing acts), fading in and out over 0.2 s |
| 13 | Power-stance battleaxes sprint slowly | `GetRunSpeedFactor` counted both axes' movement penalty (×1.5 each) and only the sprint pays it. The left weapon's penalty is ignored, and the sprint never drops below 1.1 × the run |

Sandbox (local, uncommitted, as before): `tools/extract.py` and `src/rig.rs`
gain Hand Axe and Shamshir. Rebuilt in order: extract → bake_anims → bake_unity
→ simtrace data/demo. 63 sandbox tests pass; replay exact over 24,288 ticks.

### Follow-ups (same day)

| Item | Fix |
|---|---|
| Left sheath moves with the pose at R | The crossed left back weapon is the right one's mirror image taken in the bind pose, held on the skeleton bone both hang from |
| Menu names | Inseparable Sword → Knight's Greatsword, Greatsword → Colossal Sword (menu only; saved values carried over) |
| Lock-on in a crowd | The target nearest the crosshair: angle off the camera's ray, plus 1° per 2 m |
| One-handed carry too loose, glitching at stops | The swing offset took the run's whole arm pose against the plain idle. Now only the upper arm turns, by 60 % of its swing about that clip's average (≤ 20°), the arm below kept as held; none in the (non-looping) stops |
| Separate sprint late | `InstantSprint` (sim option, Eldenheim only): the sprint button sprints from its first frame |
| Two-handed weapon lowered in jumps | Both arms take the stance's two-handed idle from take-off to landing (as the guard layer does) |
| One-handed poles' right arm | Seen in game (scripted run, front shots): the carried arm kept the idle's upright direction while the body leant into the run, so it hung plumb like a mannequin's. For the pole stance (3) the hold now rides on the chest (leans and turns with it); greatswords and greataxes unchanged |
| New movesets | Sandbox classes "Banished Knight's Halberd" (row 18030000, style a210: the lights; the Halberd class keeps the category's) and "Great Epee" (row 6020000, style a151: the heavies), shown as Great Épée. Data chain rebuilt; 63 tests; replay exact over 24,802 ticks |
| Jump attacks lose hits on landing | Landing marked every landing hit as already struck when the air swing hit, and the air swing went dead after its first contact, so a power stance's (or two-handed fists') second blow depended on jump timing. `LandFollowUps`: the air swing stays live with one window per hand when the landing has simultaneous hits, and landing skips only the hits within the air swing. Checked in game: Bjorn claws land both blows every jump |
| Reverse backstep | `ReverseBackstepFrames` 4: a move input within 4 frames of a backstep, not locked on, faces the body that way, so the backstep goes the other way (S: forward, A: right, D: left, W: plain). Checked in game before the inversion: backstep + input moved 2.47 m the other way |
| Backstep attack after a reverse backstep | `BackstepAttackTurn` 1800°/s towards the stick until its first hit, not locked on |
| Two-handed halberd heavies: arms through the torso | Measured (new `arms in (peak)` in status): standing and running 0, heavy 0.149 m, charged heavy 0.092 m into the torso. ER's arms are posed for a slimmer body. After the pose, each elbow and wrist is kept outside an oval round the spine (0.17 m wide, 0.14 m deep, chest frame) and the arm re-solved (right arm first, so the left grip follows the handle). `setting ArmsOutOfTorso false` turns it off for comparison. Not seen on screen: the camera stays behind the character in attacks |

## 2026-10-28 — Guard break, knockdowns, halberd arms

Built and deployed; not checked in game yet (the user tests).

| Item | Cause and fix |
|---|---|
| Guard break | Valheim's block meter (`GetStaggerPercentage`) decides it: a `Humanoid.BlockAttack` postfix that finds the meter full starts ER's guard break. The sandbox's GuardBreak was `a000_004270` (a 4.5 s fall to the ground); it is now `a000_004250`, a 1.5 s stumble with no cancel windows, as in ER. The sim no longer breaks the guard on its own stamina |
| Blocked back flip | New action `GuardBlow` (`a000_004280`, the end of the sandbox's ActionId and simtrace tag 16): a blocked blow that alone fills at least half the meter and leaves it at `GuardBlowAt` (0.85) or more, short of full |
| Knockdowns | An `RPC_Damage` postfix compares health before and after: an unblocked hit taking `KnockdownShare` (0.25) of maximum health or more, after armour, knocks down from the attacker's side. Started after Valheim's damage, so its i-frames no longer cancel the blow (the first play-test's item 1). Smaller hits still don't flinch |
| Guard break sound | Not added: ER's guard break and flip clips have no sound events (event 139 on the hand slots: the held equipment's clang, chosen at run time). Numbered look-alikes `s000004250`/`s000004280` are looping ambience. Valheim's own block sound plays |
| ER sounds echo | Recordings measured dry; `ErSounds` sources now bypass reverb zones (`reverbZoneMix` 0) |
| Halberd arms through the torso | ER's left hand doesn't let go: it slides along the shaft (+0.53 to −0.55 m from the right hand) down to the right hip, so the forearm crosses the belly with both joints outside the oval the earlier fix tested. `KeepArmOut` now tests four points along each bone; the left hand opens as it is pushed off the handle (fully by 8 cm) |

Data chain rebuilt (extract → bake_anims → bake_sounds → bake_unity → simtrace data/demo); 63 sandbox tests pass, replay exact over 24,802 ticks.

## 2026-10-28 — Guard reactions fixed, parries, blockers (play-tests 2 and 3)

Built and deployed; not checked in game (the user tests; in-game autotests stopped at their request). The previous entry's clip IDs are wrong: `a000_004250`/`004280` are a fall and a landing roll.

| Item | Cause and fix |
|---|---|
| Guard break / blow-back clips | Now `a000_019500` (break) and `a000_019220` (thrown back in a back roll), with per-style sets for greatshields (`0195x0`/`0193xx`, hold `000110`) and small shields (`019520`/`0194xx`, hold `000120`) picked by the shield's Valheim type: tower = greatshield, buckler = small, others medium. The user confirmed the break and blow-back look right |
| Early queueing in the blow-back | The extractor gave every reaction an input window from frame 0; the guard reactions now keep ER's own (break from frame 14, blow-back from 16, of 70 and 64) |
| GuardBlowPercent | Was "meter at least X% full" plus a hidden "this blow filled half". Now one number: the blow alone fills at least X% of the meter without filling it |
| Meter-full sound | Valheim has none of its own: the block sound plays and the hit gets through, sounding the player's `m_hitEffects` only for hits of a tenth of max HP or more. Eldenheim plays that hit sound for smaller ones (`GuardBreakVolume`); the deeper block-sound replay is gone |
| Parries never happened | The `IsBlocking` postfix was true only during a hit, so `UpdateBlock` kept `m_blockTimer` at -1. It now follows the sim's guard outside hits (the parry window starts with the guard) and the sim's verdict inside `RPC_Damage` |
| Block from the first frame | `GUARD_RAISE_FRAMES` 4 → 0 in both sims (replay still exact) |
| Wrong item's block sound | `GetCurrentBlocker` postfix returns the item that guards (two-handed or right-hand guard → right weapon), so block power, parry bonus and sound match it |
| Gear penalties | Walking and running pay the gear movement modifier; `GearPenalties` off removes all of Valheim's gear penalties |
| Defaults | `SprintLean` 0.65, `KnockdownPercent` 50; knockdown and blow-back thresholds are 0–100 sliders |

63 sandbox tests pass; replay exact over 24,796 ticks.

Follow-up (same day): the meter-full sound is now Elden Ring's guard break, from the user's own recording (`sounds/guard_break.ogg`, trimmed to 2.0 s, kept in the sandbox's ignored `assets/sounds/` and deployed with the others), played by `ErSounds` with reverb zones bypassed at `GuardBreakVolume`; the Valheim hit-sound stand-in is gone. Knockdowns listened for input from frame 0 (ER gives them no input window); they now listen from their first way out, the roll up at frame 36 of 105.

Follow-up 2: (1) the stamina bar now flashes, as in Valheim, when sprint, roll or jump is asked for on an empty bar; only attacks did before. (2) With `SneakStamina` on, an empty bar stands the player up (Player.UpdateCrouch). (3) Shields broke later than vanilla: Valheim fills the block meter twice per blocked hit, in `BlockAttack` and again in `ApplyDamage` for what gets past the shield (× `m_staggerMultiplier`). Incoming zeroed that multiplier for infinite poise, so the second fill was lost. Blocked hits now keep it, and the guard break and blow-back are judged after all of `RPC_Damage` (BlockAttack prefix records the meter; the RPC_Damage postfix decides).

## 2026-10-28 — Shields one-handed, swing ends, hitbox view

Built and deployed; not checked in game.

| Item | Cause and fix |
|---|---|
| Two-handed shields | The two-hand-left input is dropped while the left item is a shield |
| Swing ends not hitting | The sim runs at 60 Hz over 30 fps clips (half a frame per tick), and a window closed on the first tick at or past its end frame, so the last half frame of the blade's path (its fastest part on many swings) was never swept. `WindowHit` now stays open for the tick that crosses the end and sweeps up to it. Ground attacks only: air attacks use the sim's shared `ActiveHitNow`, unchanged so the replay stays exact |
| Trails not matching hits | Trails were held 4 frames past each window, as Valheim's clips do; they now end with the window |
| HitboxDebug (Debug) | LineRenderer capsules: each held item's capsule always, green, red while a window is open; the sim's ER blade capsule in yellow during windows |

## 2026-10-28 — Sprint-guard turns, head-sized hitboxes, paired blades, sledge slams, Erdsteel Dagger

Built and deployed. Some in-game checks with the test character (the user then took over testing).

| Item | Cause and fix |
|---|---|
| Swing trails | Back to 4 frames past each window (the previous change reverted) |
| Guarding while sprinting | Turns to the stick at once, no pivot (sim: `Sprinting && Guarding` sets the yaw; sandbox never has both, replay exact) |
| Hit capsule width | Each held item's capsule radius is the widest its far 40% (head or point) gets around the blade line, + 2 cm (vertices if readable, else its box corners). Measured: iron sledge 0.325 m |
| Paired weapons on fist hitboxes | Skoll and Hati and the dual axes are one skinned mesh (`attach_skin`, bound to the whole body, mesh not readable), nothing on the attach points, so no blade was found. Each hand's half is now found by motion: the mesh is baked every 0.1 s and each vertex goes to the hand it stays still against; the split is kept per item. Fists until the hands have moved apart. Berserkir: right 264 vertices, reach 0.77 m (Valheim's own trail tip is at 0.77), left 262, reach 0.66 m. Knuckles and claws keep the fists |
| Sledge AoE | The sledges' shockwave is their attack's trigger effect, which played on every swing. Now only heavies (heavy, charged, running, jumping, guard counter) slam: at the first thing struck, or, striking nothing, where the head meets the ground (terrain or a solid piece) up to 6 frames past the window. The slam is Valheim's `DoAreaAttack`: trigger effects plus a blow to everything within `m_attackRayWidth + m_attackRayWidthCharExtra` (4 m for the iron sledge). Seen in game hitting a target and its neighbours; the bare-ground case not seen yet |
| HitboxDebug | Uses each item's own radius; shows the slam sphere for half a second |
| Erdsteel Dagger | New class (EquipParamWeapon 1150000, style a103: only heavies, so the dagger's lights with its own heavies, as in ER). Sandbox weapon 31; menu option under any moveset choice |

Follow-up (same day, not checked in game): (1) sledges lost their swing sound on every attack: Valheim's sledge trigger list holds `sfx_sledge_swing` beside the hit's sfx/vfx, and the whole list had been moved to the slam. The swing entries (prefab name contains "swing") now play on the swing; the slam plays the rest. (2) Guarding at a sprint still pivoted sometimes: reversing direction leaves a tick with no stick direction, which started `SprintStop` (the brake). It is skipped while guarding at a sprint. (3) New class Crescent Moon Axe (EquipParamWeapon 15030000, greataxe category 32, style a177: its own heavies over the greataxe's lights); sandbox weapon 32. Replay exact over 25,284 ticks.

## 2026-10-28 — No log flooding, demo and dummy removed, co-op plan

Built and deployed; replay exact. Not checked in game.

| Item | Cause and fix |
|---|---|
| Microstutters from logging | Every swing, hit, stamina use and reaction went to the BepInEx log (console + disk) and was appended to `results.log` with a synchronous `File.AppendAllText` on the main thread, several times per attack. That event log is now behind F1 → Debug → `EventLog`, off by default; one-off lines (load, patch failures, blade setup) still go out |
| Sky-arena demo and in-game dummy | Removed (`Demo.cs`, `DummyView.cs`, the `demo`/`stops`/`pauseswing`/`resume` commands, `Space.Origin`). The sim's `World`/`Dummy` stay for the replay test |
| Multiplayer | PLAN §3.7 rewritten for co-op without PvP: sync the chooser's inputs and run the chooser on remotes, ER sounds heard by everyone, stagger reactions, data hash, admin-synced balance |

Follow-up (same day, not checked in game): Valheim's stagger is skipped for the active player (`Character.RPC_Stagger` prefix). It only ever fired from a blocked hit filling the block meter (unblocked hits carry no stagger), where the sim already plays GuardBreak; vanilla's version snapped the body to face the blow and ran a hidden stagger under the pose.

## 2026-10-28 — Other players' poses

Built and deployed; replay exact, and the new pose state round-trips on all 25,284 ticks. Not seen in game (needs two clients).

| Item | Cause and fix |
|---|---|
| Other players slid in an idle pose | The owner now writes the chooser's inputs (`Eldenheim.Sim.PoseState`, ~50 B) to ZDO `eh_pose` with its data hash in `eh_data`; a remote's `Controller.FixedUpdate` fills a stand-in sim from it, moves its frames on between updates, and poses that player with the same chooser, retarget and hand visuals. Hand items come from `VisEquipment`'s hashes. Valheim's foot and head IK are skipped for any posed player |
| A second player broke the local player | `Controller.Awake` set `Controller.Local` for every player's character, so one who spawned after you took it over and the mod stopped driving you (`Patches.Owning` checks `Local`). `Local` is now set in the local player's own `Update` |
| Capes on other players | The pre-cloth pose write covers every posed player, not only the local one |
| Different data builds | A player with another data build is shown with Valheim's animation, logged once |

Follow-up (same day, not heard in game): other players hear the ER sounds. A remote plays the guard counter's own sounds when the synced state starts a guard counter (scheduled as far into the clip as the update arrived) and the guard break when it starts `GuardBreak`, through the same 3D source as the owner's (40 m linear rolloff). The per-counter "sound: … are its own" log line is now behind EventLog.

Follow-up (same day, not seen in game): weapon trails on other players. A remote tracks its blades from its own pose and runs `Combat.Swings` on the stand-in sim with swing sounds off (`Combat.RemoteTrails`), so trails follow the same hit windows; a frame never steps back within an action when a late update arrives. Remote event logs are cleared each step (nothing read them, so they grew without end).

Follow-up (same day, not tried on a server): the F1 Balance section follows the server (`BalanceSync`). The server or host sends its Balance values after each client's peer info and again when its admin changes one; the client applies them without saving its config file and restores its own values (then saves) when it leaves the world. Not Jotunn: Valheim's peer RPC is enough.

Follow-up (same day): dedicated-server check, from the code only (no server installed here). Nothing to fix: without a local player or ER data every Eldenheim path stays off, the patches fall through to vanilla, and MagicaClothV2.dll ships with any Valheim build (`assembly_valheim` uses it). Details and how to try it on a real server in PLAN §3.7.

## 2026-10-28 — 0.2.0 private package for co-op testing

`tools/package_private.py` builds `build/package/Gargantua-Eldenheim-<version>-PRIVATE.zip` (Gale "import local mod" layout): both DLLs, the generated ER data, the sandbox's sound table and 115 recordings, and this PC's Eldenheim config. It holds Elden Ring-derived data, so it is for the user's own machines only: never shared, uploaded or committed (`build/` is ignored). Version bumped to 0.2.0 so the log shows which copy loaded.

## 2026-10-28 — 0.2.1: first co-op play-test

Built, deployed and repackaged (`Gargantua-Eldenheim-0.2.1-PRIVATE.zip`); replay exact, pose state round-trips. Not checked in game.

| Report | Cause and fix |
|---|---|
| Two-handed bare fists with a weapon in the other slot miss hits | Two-handing fists puts the other item on the back, but the left hand kept tracking that item's blade (and with the left hand two-handed, both blades were cleared). Blades now follow what each hand holds: two-handed fists strike with both bare hands; a two-handed weapon with its right-hand blade |
| Charged heavy sounds plain to others | Its deeper, louder copies were networked effects whose pitch and volume were changed only on the owner. Changed copies now play on each client by themselves (`ZNetView.m_forceDisableInit`); remotes play just those |
| Others never saw a perfect dodge | Other clients judge i-frames from the player ZDO's `dodgeinv` (vanilla writes it from the animator, which Eldenheim doesn't drive). The owner now writes it from the sim. The once-per-roll limit moved to `RPC_HitWhileDodging`, on the dodger's client |
| EmptyHandGuard: left hand not where two-handed guarding puts it | The two-handed guard arms were laid over the one-handed body. Guarding with the right weapon now uses the two-handed stance whole (`Chooser.GuardsTwoHanded`) |
| EmptyHandGuard not synced (others saw an invisible-shield guard) | The guard options are sim flags that never travelled; they are in the pose state now (version 2) |
| Console flooded on equip, two-handing, sheathing, a throw | Blade, pole and shield measurements and swing-sound peaks were logged each time; now only with EventLog |
| Not as smooth as ER | The pose was made in the 50 Hz physics step; it is now made every rendered frame at that frame's point between sim ticks (local and remote players) |
| A little slower than ER | Confirmed: ER's TAE event 608 `AnimSpeedGradient` plays parts of 15-25% of attack animations at 1.13-1.7x (charged heavies up to 20 frames shorter, curved greatswords most). The sandbox ignores it, so Eldenheim does too; see PLAN §7 |

## 2026-10-29 — 0.2.2: sheaths and swaps

Built, deployed and packaged (0.2.0 and 0.2.1 zips kept; 0.2.0 was rebuilt from 54a5989 after being deleted by mistake). Not checked in game.

| Report | Fix |
|---|---|
| Power stance sheathed: the mirrored weapon's edge faces the other's | The mirrored one is also rolled half a turn about its own length (its meshes' longest axis) |
| Two-handing the main weapon: the off-hand weapon hangs where the main one would | Valheim hangs every one-handed weapon at `m_backMelee`. The off-hand weapon now hangs mirrored across the spine (with the same roll), as in power stance; shields and torches keep Valheim's spots |
| Arms through the torso during swaps between two weapons | The left hand lets go of the handle for the whole swap (no grip IK while `SwapState` is set) |
| When do ER's sped-up windows apply? | Always, inside their TAE 608 windows. The Claymore (style a125) has them only on its charged heavies: `030500` frames 12-25 and `030510` frames 15-28 at 1.2x, one- and two-handed. That is 2.2 animation frames shorter, 4.3 frames at 60 fps, matching the user's frame-by-frame comparison (4 frames). The earlier per-class table read the category files (a25); styled weapons use their style's file |

## 2026-10-29 — 0.3.0: ER's animation speed windows, outward-facing axes

Built, deployed and packaged (earlier zips kept). Replay exact over the re-recorded demo (25,151 ticks, 133 fewer than before: the sped-up windows). Not checked in game.

| Item | Change |
|---|---|
| Speed windows | ER's TAE event 608 (`AnimSpeedGradient`: speed at start, speed at end) plays parts of animations faster. The sandbox's `extract.py` now reads it for every action and air attack (`speed: &[(from, to, a, b)]`, 487 actions), and both sims advance the action's frame by `DF * speed_at(frame)`; every other number stays in the animation's own frames, as ER's events are. Hit sweeps cover the tick's actual step; the chooser, the sandbox's `rig.rs` and remote extrapolation (`PoseState.Advance`) run ahead at the same rate. `eldenheim_data.bin` is version 2. Check: the Claymore (a125) charged heavies `030500`/`030510` get frames 12-25 / 15-28 at 1.2x, 2.2 animation frames (4.3 at 60 fps) shorter, which the user measured in both games |
| Crossed axes faced each other | The fixed half-turn of the mirrored one is gone. A sheathed item with a heavy side (an axe's blade, measured off its shaft) is first turned to face away from the body's middle, then mirrored, so both face out. Applies to power stance and the mirrored off-hand weapon |
| Off-hand atgeir through the shoulder | The half-turn was about the item's box axis; atgeirs sit ~25° off it, so the shaft swung. Turns now go about the longest mesh's own shaft through the grip, and only items with a heavy side turn |

## 2026-10-29 — 0.4.0: five new classes, arms out of the belly

Built, deployed and packaged (earlier zips kept). Data chain rebuilt (extract, bake_anims 1,723 clips, bake_unity, simtrace); the demo tour now runs 26,465 ticks over 156 states, replay exact. Not checked in game.

| Item | Change |
|---|---|
| New classes | Raptor Talons (claw, style a227, 53 attacks), Godskin Peeler (twinblade, a121, 51), Serpentbone Blade (katana, a165, 50), Great Katana (Shadow of the Erdtree category a61, 46) and Rakshasa's Great Katana (46). Rows from Smithbox's English row names. Rakshasa's names style a185, which has no animations in the game files, so it plays the category's with its own behaviour rows (variation 6601: its own motion values and stamina). All five are in every F1 moveset menu |
| Pole movesets: left arm through the torso | Not the pole animations: in ER the halberd's left arm comes no nearer the spine than the Claymore's or katanas'. The arm test's oval was 0.14 m in front of the spine; Valheim's body mesh (measured from bundle c4210710, bind pose) reaches 0.17-0.24 m in front, 0.14-0.20 m behind, ~0.21 m to each side. The oval now reaches 0.20 m forward and 0.15 m back (width unchanged, so hanging arms aren't pushed out). Applies to every class; poles show it most because they hold the left hand low across the belly |

## 2026-10-29 — 0.5.0: second play-test of the new classes

Built, deployed and packaged (earlier zips kept). Data chain rebuilt, the sound table too; replay exact. Not checked in game.

| Report | Cause and fix |
|---|---|
| One-handed halberds and spears wobble while moving | The carried arm took a share of the run's arm swing and the chest's full rotation (each stride's twist and bob), which a long pole magnifies at its tip. Poles now get no arm swing and follow the chest smoothed over ~0.35 s (the lean stays) |
| Second heavy can't be queued early | `HeavyDebounce` ignored any heavy press within 6 frames (0.2 s) of letting the button go, which a quick tap-tap is. Now 1 frame (a bouncing button is milliseconds) |
| Multi-hit damage | Checked, no change: each hit deals weapon damage × its own ER motion value ÷ the Light1 total, read from each hit's AtkParam row; ER already splits multi-hit attacks (Serpentbone heavy 0.5 + 1.1, Erdsteel Dagger heavy 0.9 + 0.9, single-hit heavies 1.25) |
| Great Katana: no guard counter sound | The sound table predated the class; rebaked (`bake_sounds`), it now has a061's guard counters |
| Moveset menus | Listed in Elden Ring's weapon-type order, each class's styles after it (the default no longer jumps to the top) |
| Rakshasa's Great Katana's own heavy | The extractor read EquipParamWeapon 0xEE as a byte; it is `spAtkcategory`, a u16. Rakshasa's is 953 (read as 185, which has no data); style a953 exists, its heavies borrowing the Claymore's (a125) animations with its own events |
| Atgeir: left hand still in the torso | The hand itself wasn't tested (only shoulder to wrist), and the oval's front fit a bare body. Two points along the hand are tested now, and the oval reaches 0.22 m forward (armour) |
| Torch flame trails to the vanilla hand position | Unity updates particles after `Update` but before the pre-cloth hook where the pose was written: on frames the animator ran, the flame emitted from Valheim's hand. The pose is now written at the end of `Update` (local and remote players) |

## 2026-10-29 — 0.6.0: torch, atgeir arms, thrown falls, ER guard-break and parry sounds, i-frames, F1 menu

Built, deployed and packaged (earlier zips kept). Data chain rebuilt (bake_anims 1,772 clips, bake_sounds, bake_unity, simtrace); 64 sandbox tests pass; replay exact over 26,465 ticks.

| Report | Cause and fix |
|---|---|
| Torch plays Morning Star | A torch alone goes to the right hand, where it was mapped by its skill (Clubs → OneHandedClubs → Morning Star). `ClassOf` now gives every torch ER's Torch. Checked in game: right Torch, weapon Torch |
| Atgeir: left hand still through the torso (end of charged heavy 1) | Measured in game with the new `probe` command (left forearm, wrist and finger joints against the baked body and armour meshes, per action frame): 10.3 cm deep at Heavy1Charge frames 68-70 (the thumb, beside the forearm's line), 11.9 cm in Heavy1 with the guard off. The guard tested the hand only along the forearm, pushed once and its oval was 0.17 m wide against a ~0.21 m body. Now: the hand along its own bones with a 3.5 cm pad, up to four pushes until clear, oval 0.22 m wide. After: charged heavy end 0.5 cm, Heavy1 2.2 cm, lights 4.4 cm at most (forearm against the flank); idle, run and sprint still pushed 0 mm |
| Falling after a hit | New in both sims: leaving a ledge during a flinch, guard blow or guard break is a thrown fall (`Air.thrown`), posed as ER's `a000_004060` (thrown onto the back, 55 frames) then `004010` (the tumble, looped), landing in the heavy landing (`202310`, which starts from that pose). Picked by stick-figure renders. Eldenheim lands it so from 1.5 m (MinLandingFall) up. Sandbox test `knocked_off_a_ledge_tumbles_and_lands_in_the_sprawl`. Not checked in game |
| Guard-break sound from the game | Every guard-break clip (`019500/019510/019520`) plays `Play_c999999985` on frame 0, which no other reaction does: one recording, 599687853 (-2 dB). Its loudness envelope matches the user's `guard_break.ogg` (correlation 0.998, same 2.0 s), so it is that sound; it ships with the baked sounds |
| ER parry sound (`ParrySound`, on) | Being parried (`a000_005800/5810`: start mid-swing, arms flung open) plays `Play_c999999991` on frame 0: recording 842851583, 3.9 s, 0 dB. `bake_sounds` bakes that clip's sounds too. Valheim's parry effect is `vfx_perfectblock` (particles) + `sfx_perfectblock` (sound, networked): while `BlockAttack` runs the sound entry is left out, and a parry plays ER's; other players hear it through a counter in the player's ZDO (`eh_parry`) |
| Valheim i-frames (`ValheimIframes`, off) | Elden Ring: rolls 0-13 frames (0.43 s), heavy 0-12, backstep 0-7 (0.23 s). Valheim: invincible from the dodge to its clip's `DodgeMortal` event: clip "Dive", 1.50 s, event at 0.70 s = 21 frames (read in game). On, rolls and backsteps get 0-21 (a sim host option, `DodgeIframes`, so the sandbox replay is unchanged) |
| I-frame viewer (`IframeDebug`) | Pale blue capsule while invincible; the action, its frame and its i-frame window on screen. Checked in game |
| F1 menu | Numbered sections: 1 General, 2 Controls, 3 Keyboard & Mouse, 4 Gamepad, 5 Movesets, 6 Combat (server), 7 Stamina & Movement (server), 8 Visuals, 9 Sound, Debug, Test. Saved values move with their settings (checked: all 63 kept). BalanceSync syncs every section marked (server) |

## 2026-10-29 — 0.6.1: torch light, parry volume, jump leg i-frames, gamepad and menus

Built, deployed and packaged (earlier zips kept). The sims are unchanged (replay as for 0.6.0). Not checked in game.

| Report | Cause and fix |
|---|---|
| Torch shines while the other weapon is two-handed | Hiding a held item turned off its renderers only; its `Light` stayed on, and `LightLod` turns lights back on by distance. Hidden items now have their lights off and `LightLod` paused, both restored when shown |
| Parry volume | `ParryVolume` (9 - Sound, 0-1, default 1) |
| Jump i-frames on the legs | ER: in a jump the legs pass over low blows. The sim already had it (a low hit on an airborne player 0.35 m or more above the ground is `Jumped`), but Eldenheim called every hit high. A hit whose Valheim contact point is below the hips now counts as low. IframeDebug shows a capsule from the feet to the hips while it applies |
| Gamepad presses act after menus | When control came back (unpause, inventory closed), every button's last state read as up, so the button that closed the menu and was still down counted as a new press (A unpausing jumped; B closing the inventory dodged on release). Any action held when control returns, or when a menu closes, is now ignored until let go |
| Gamepad in the inventory | `LeftJoystickInventory`: the left stick walks and stops moving the selection (`ZInput.GetButtonDown("JoyLStick…")` reads false inside `InventoryGui.Update`). `DodgeInInventory`: B dodges and sprints, not closing it (`JoyButtonB` reads false there unless a dialog it closes is open); Y still closes. Valheim gives a gamepad no input with the inventory open, so Eldenheim takes it then, with only dodge and sprint |

## 2026-10-29 — 0.6.2: held keys through menus, volumes in percent, no block push, spear throw origin

Built, deployed and packaged (earlier zips kept). Sims unchanged. Not checked in game.

| Report | Cause and fix |
|---|---|
| A held sprint key stopped when the inventory closed | 0.6.1 ignored every held action after a menu until released. Only gamepad buttons are ignored now (the press that closed the menu); keys carry on |
| Volumes in percent | `GuardCounterVolumePercent` 40, `GuardBreakVolumePercent` 30, `ParryVolumePercent` 40 (0-100). New names, since an old saved share such as 1 would read as 1 %; the old entries are dropped |
| Valheim's Block Force (`RemoveBlockForce`, on) | `Humanoid.BlockAttack` ends a successful block of a melee blow with `attacker.Damage` carrying no damage and a push of the guarding item's `GetDeflectionForce()`. With the option on, that hit is dropped (a `Character.Damage` prefix, only inside the local player's `BlockAttack`, only for a damage-free push) |
| Smithscript throws sometimes start low | The spear left from the drawn blade's near end. That is read once per physics step from the last rendered pose, before the sim steps, so a throw tick could see a pose a frame or more old (frame drops, two ticks in one step); and on a pole slid along its shaft the near end can be the butt, low behind the hand. It now leaves from ER's own spear line at the throw's tick (0.5 m up the shaft from the grip, the sim's sweep) |

## 2026-10-29 — 0.6.3: gamepad inventory options that work, Use as the gamepad modifier, lock-on stick, upright tower shields

Built, deployed and packaged (earlier zips kept). Sims unchanged. Not checked in game.

| Report | Cause and fix |
|---|---|
| Stick still moves the inventory selection; B still closes it | 0.6.1 patched `ZInput.GetButtonDown`, a one-line static method: inlined into its callers, the patch never ran. Now, at the start of `InventoryGui.Update`, the left stick's direction buttons and B are cleared with Valheim's own `ZInput.ResetButtonStatus` (which clears them for that frame; a held button doesn't read as pressed again) |
| Gamepad Use didn't two-hand or equip into the left hand | The modifier's gamepad default was Y (which also opens the inventory). It is now `Valheim:JoyUse`, Valheim's own Use button (X), and an old saved Y default moves to it. For equipping, a gamepad modifier counts once held 0.15 s, so the X tap that equips in the inventory still goes to the right hand |
| Volume defaults | Guard counter 30, guard break 40, parry 40 (0.6.2 had the first two swapped; saved 40/30 becomes 30/40) |
| Lock-on switching lags on a controller | The stick switched only if it crossed 0.7 in the same frame it left the middle (leaving the middle disarmed it), so a flick that took a few frames did nothing until a faster one; and a second flick within 0.3 s was dropped. Now it arms in the middle, switches at half tilt, once per push, with no wait |
| Off-hand sounds like the thrown spear | Not found in the code: swing sounds come from each hand's item every tick, and the throw unequips the spear. EventLog now logs each swing sound's item ("swing sound: ... from ...") to find it |
| Tower shields held diagonally; Serpent Scale | Only the shield's face was lined up with ER's, leaving its roll as Valheim's attach had it. Tower shields now also turn their height onto ER's left weapon X axis (upright in the greatshield guard), by the smaller turn so they never flip. Any shield that can't parry (`m_timedBlockBonus` ≤ 1, the Serpent Scale too) counts as a tower shield: upright and ER's greatshield guard reactions |

## 2026-10-29 — 0.6.4: pole carry, tower shield hold reverted, compressed-clip errors

Built, deployed and packaged (earlier zips kept). Sims unchanged. Not checked in game.

| Report | Cause and fix |
|---|---|
| One-handed spears and atgeirs carried stiffly | 0.5.0 took their arm swing away entirely (it had wobbled the tip). They now swing a quarter as much as other carried weapons, at most 6° (~20 cm at a 2 m tip), and follow the chest smoothed over 0.2 s (was 0.35) |
| Tower shield hold | 0.6.3's upright turn removed: shields line up by their face only, as before. Shields that can't parry still count as tower shields for the guard reactions |
| "Cannot get data on compressed samples" errors (spear thrust sounds) | Timing a swing sound reads its clip's samples to find its loudest moment; Unity can't for clips not stored decompressed and logs an error. Those clips are now skipped (their sound starts at the hit window, as in Valheim) |

## 2026-10-29 — 0.6.5: Auto Lock-On, Auto-Target, fist swing sounds on time

Built, deployed and packaged (earlier zips kept). Sims unchanged. Not checked in game.

| Report | Cause and fix |
|---|---|
| `ToggleAutoLock-On` (off) | When the locked target dies, the lock moves to the enemy nearest the view (the same pick as pressing lock-on), as ER's Auto Lock-On. Out of range still ends the lock |
| `Auto-Target` (off) | Unlocked, an attack press picks the nearest enemy within 6 m and 75° of the stick (or the facing) and passes it to the sim as the target until the attack's last hit, so the attack turns to it within its own turn windows, as when locked on; dodges after the hits stay free. No lock, no dot. Throws aim at it too |
| Fist charged heavy sounds partly late | Valheim's fist swing recordings (Player_Movement_FistMelee_M_01-04) peak 460-500 ms in; the charged copies at pitch 0.8/0.5 at ~600/960 ms. The lead was capped at 0.4 s, so the normal copy was ~0.1 s late and the deep ones 0.2/0.56 s. No cap now; a charged attack's sounds wait for the charge to be kept (ER's charge end, so a tap still sounds as the uncharged heavy), and a sound that starts after its ideal moment is started that far into its recording (`AudioSource.time`), so the peak still lands on the hit. Unarmed lights, too short for the fist sound's lead, get the same |

## 2026-10-29 — play-test fixes: rocks, slopes, sledge hit sounds, lock-on

Built and deployed. Replay exact. Not checked in game.

| Report | Cause and fix |
|---|---|
| Sledges missing Valheim's attack sounds besides the AoE | From the bundle: a sledge's `m_hitEffect` is empty; its hit sound (`sfx_sledge_iron_hit`) sits in the trigger list with the shockwave, which only heavies play. Swings that don't slam now play the trigger list's `sfx_` entries at their first contact, once |
| Small rocks couldn't be walked over; slopes steeper than 38° couldn't be climbed | The 38° hard limit is gone. Climbing now follows the player's capsule (r 0.49, from the bundle): a rise is taken when the contact normal of a 0.49 m sphere is within 70° (small rounded rocks), not logs, sharp rocks or cliffs. On ground steeper than 38° the player slides down as `Character.ApplySlide` (slippage to 1 over a second, towards 5 m/s down the slope) |
| `Auto-Target` | Renamed `UnlockedAimAssist` (an earlier value isn't carried over) |
| `ToggleAutoLock-On` didn't move the lock | `Character.OnDeath` destroys a killed creature at once, so the lock saw a destroyed object, never `IsDead()`. A lock that turns into a destroyed object now counts as a kill |
| Controls menu order | The Controls section lists in a fixed order: ToggleAutoLock-On, UnlockedAimAssist together, then LeftJoystickInventory, DodgeInInventory |

## 2026-10-29 — play-test fixes: Valheim's movement where it belongs, traps, ladders, harpoon, slows

Built and deployed. Replay exact. Not checked in game.

| Report | Cause and fix |
|---|---|
| Sprinting up steep slopes | As Valheim's running (`ApplySlide`'s wall run): no slope slide while sprinting and moving; stop sprinting and the slide builds again |
| Catapult, carts and battering ram, ships, workbenches | Valheim moves the player (Eldenheim lets go, as when swimming): from a catapult's `SetTempParent` until a move input once released; while a `Vagon` is attached to the player; in a ship volume; at a crafting station with the inventory open |
| Traps hitting over and over | Valheim's pushback (`m_pushForce`, 20 m/s its way while it decays) now moves the sim, for hits with no attacker only (spikes, fire); creatures' blows react through the sim as before |
| Wood ladder, Grausten steep stairs | On foot, a piece named "ladder" or holding an `AutoJumpLedge` is walked up rung by rung (up to 0.7 m a step; the drawn body eases), and the auto-jump hops are gone |
| Jumps cut short against a rock | A jump only lands while it's no longer rising (the air already worked so) |
| Abyssal Harpoon | The throw's HitData had no status effect, and `Projectile.Setup` replaces the projectile's own with the hit's: the throw never harpooned. It now carries the weapon's `m_attackStatusEffect` (SE_Harpooned: line, pull, guard releases after 2 s as in Valheim); swings don't harpoon. Swings sound as the flint spear (`sfx_spear_poke`, the harpoon has none), throws with its own `sfx_spear_throw`. Never consumed, whatever ThrowSpears |
| Immobilized, Tared, Frost, encumbered | Status speed (`ApplyStatusEffectSpeedMods`, data: Immobilized -1000, Tared/Slimed/Puke -0.5, Frost to 0.1, Moder +0.1, hasty +0.15) applied since 81afdfd, whatever GearPenalties. Now also: Immobilized and encumbered can't jump, dodge or sprint; encumbered moves at Valheim's crouch speed, drains its stamina, and stops on an empty bar |
| Standing on enemies | The ground casts include the character layers (the player's own capsule skipped), and what's underfoot carries the player (`GetPointVelocity`) |

## 2026-10-29 — play-test fixes: footsteps, slopes, carts, ships, ladders, gjall, falls, thrown weapons

Built and deployed. Replay exact. Not checked in game.

| Report | Cause and fix |
|---|---|
| Footprints and step/slide/land sounds missing on most surfaces | `FootStep` takes the material from Valheim's ground contact (`OnCollisionStay`), which a body moved by velocity without gravity rarely gets. Each physics step now writes the contact from the sim's ground cast (collider, normal, point), so steps, landings and fall damage see the real ground; `m_sliding` (slippage > 0.5) plays the slide sound and `m_wallRunning` (sprinting on ground past 38°) the climbing steps |
| Bounce sprinting on steep slopes | On foot the drawn height follows the sim (eased), not the physics body pushing against the slope |
| Rolling out of a cart attacked | Buttons held as Eldenheim takes over (Valheim's block + jump) are ignored until let go, keys as well as the gamepad |
| Moving ships | No longer handed to Valheim: a moving body under the feet (deck, lox) carries the player by its point velocity, and that speed carries on through a jump or fall until the next ground |
| Two sheathed weapons at a workbench | While Valheim moves the player, the second sheathed weapon still hangs mirrored across the spine (before, it sat on top of the first until Eldenheim took over again) |
| Ladders didn't step a rung | The player's capsule (0.49 m) touched the rung before the 0.3 m ground cast saw it, so the body was held back first. A ladder rung the capsule-wide cast touches counts as the ground at once |
| Gjall's push-off | Blows' pushes are dropped (poise) except from traps, which was broken too: `Incoming` zeroed every push before Valheim applied it. Now pushes stay for hits with no attacker and from the creature the player stands on |
| Heavy fall landing | A fall whose damage passes KnockdownPercent plays LandHeavy (ER's long fall, flat on the back) instead of the knockdown |
| Throw any weapon with the Smithscript Spear moveset | Weapons without a projectile borrow the flint spear's (its trail), showing their own model (the item's `attach`) with the tip along the flight; never consumed. Others in multiplayer see a flint spear |

## 2026-10-29 — Deep North: snow and ice

Built and deployed. Replay exact. Not checked in game.

| Report | Cause and fix |
|---|---|
| Deep snow not cleared | Part of the replaced `UpdateWalking`: now the same — the walk object every `m_deepSnowWalkObjDist` (clears the snow) while standing in snow deeper than `m_deepSnowSlowStartHeight`, the slow for its depth (into MoveScale/SprintScale), and snow brushed off pieces |
| No sliding on ice | `ApplySlippery`'s test on the sim's ground (frictionless static solid, no ice shoes): the velocity turns toward the sim's by Valheim's slippery/skating control per physics step, and coasts with no input (`m_slipperyBreaking`) |

0.6.6 packaged (`build/package/Gargantua-Eldenheim-0.6.6-PRIVATE.zip`, earlier zips kept) for the multiplayer test on the second machine.

## 2026-10-29 — 0.6.7: synced thrown models, steady EmptyHandGuard hand, InstantJump, Set button, lock-on sight

Built, deployed and packaged (earlier zips kept). Replay exact. Not checked in game.

| Report | Cause and fix |
|---|---|
| Thrown non-spear weapons looked like flint spears to others | The item's name goes in the projectile's ZDO (`eldenheim.thrown`); a `Projectile.UpdateVisual` prefix on every client swaps in that item's model, once |
| EmptyHandGuard: the left hand wandered along the weapon | The one-handed weapon's guard clip doesn't put ER's left hand on the handle, so the grip followed the clip's free hand. Guarding so, the left hand is held where it first took the handle (or just below the right hand) |
| InstantJump (new, Controls, off) | On: the jump starts at the clip's last frame before it rises, skipping ER's crouch |
| The Set button sometimes missing | A long binding's text field pushed the buttons out of the row; Set and Default now come first |
| Lock-on through walls | Candidates need a clear line from the eyes to their centre (Default, static_solid, piece, terrain), for lock-on, switching and UnlockedAimAssist |

## 2026-10-29 — jump attack landing stutter

Built and deployed. Replay exact (the change is behind the Eldenheim-only `LandInPlace`). Not checked in game.

| Report | Cause and fix |
|---|---|
| Jump attacks stutter on landing (log: air swing at frame 15, landing at frame 14; swing sound twice; stamina 15.5 twice) | Landing mid-swing, the sandbox starts the landing at `min(air frame, landing's first hit)`: a step back to frame 14, and a new act, so the controller charged the landing's first hit again and the swing sound replayed. `LandInPlace` goes on at the air frame; the landing's first tick charges nothing (the air attack paid), and its first window keeps the sound already played |

## 2026-10-29 — Stone Club class; no sheathing mid-swap

Built and deployed. Data rebuilt (extract, bake_anims, bake_sounds, bake_unity --all, simtrace data and demo); replay exact over 26,701 ticks; sandbox tests pass. Not checked in game.

| Report | Cause and fix |
|---|---|
| Power-stanced atgeirs sheathed early, one hanging askew | Valheim's sheathe is refused only mid-attack (`InAttack`); now also while a weapon swap (draw, change of hands) plays, so both poles are in place when they go on the back. Cause inferred, not reproduced |
| Stone Club moveset | New class "Stone Club" (EquipParamWeapon 11140000, hammer category 33, style a180: its own moveset), sandbox weapon 38 (the shield moves to 39); listed under One-handed clubs |

0.6.8 packaged (jump attack landing, Stone Club, no sheathing mid-swap; earlier zips kept).

## 2026-11-03 — guard hand, lock-on range, stamina, atgeir sheath, Guardian's Swordspear, feints, defaults

Built and deployed. Data rebuilt; replay exact over 26,950 ticks; sandbox tests pass. Not checked in game.

| Report | Cause and fix |
|---|---|
| Guarding two-handed: the left hand reached its place, then slid back to the right hand; EmptyHandGuard worse since 0.6.7 | ER's two-handed guard holds the left hand on the line of the weapon about 0.5 m up from the right (measured over a010/a012/a013_000100: steady, 7-8 cm off the line). On guard the left hand now holds the farthest point along the weapon it reaches (ER's) for the whole guard, and the torso check doesn't pull it off the weapon. EmptyHandGuard plays the same two-handed guard, so it is the same. 0.6.7's fallback (just below the right hand, and a state that could stick from an earlier guard) is gone |
| Lock-on range too short | `LockOnRange` (Controls, 10-60 m, default 30; was the sandbox's 15); a lock breaks at 1.5× it |
| Jump attacks still sometimes charged twice | A jump attack landing before its swing's window opened charged the landing's first hit when it came out a few ticks later. A landing's hits within its first swing (the air swing) are never charged |
| Atgeirs: sometimes couldn't draw after 0.6.8; still now and then askew on the back | 0.6.8's no-sheathing-mid-swap reverted. The askew one: `FaceOut` turns the right back item half a turn and was run again every frame `Mirrored` failed, so it could end turned back. Now once per item |
| Guardian's Swordspear | New class (EquipParamWeapon 18110000, halberd category 38, its own style), sandbox weapon 39; listed under Polearms |
| Feints | `Feint1/Feint2` (a0xx_030950/030955, two-handed +2000): a dodge pressed in a heavy's charge window turns it into ER's slash stepping back. The window is TAE 67 type 100290, which these classes have over their charge (100280). Classes with them: Rapier, Heavy Thrusting Sword, Curved Sword, Shamshir (its category's), Great Epee, Great Katana, Rakshasa's |
| Defaults | The user's: InstantJump, ToggleAutoLock-On, LeftJoystickInventory, DodgeInInventory, SneakStamina on; GearPenalties off; LockOnSwitchFlick 30. Whole numbers: LockOnSwitchFlick, StaminaRegenRate (25-400 %), SprintLeanPercent (was SprintLean 0-1, value carried over). Gone settings (KnockdownShare, GuardBlowAt, Auto-Target) removed from the file |

## 2026-11-03 — guard hand (the real cause), lock-on recentre, swordspear grip, feint double dodge, training dummy, offhand torch

Built and deployed. Replay exact over 26,950 ticks; sandbox tests pass. Not checked in game.

| Report | Cause and fix |
|---|---|
| Guard: the left hand still slid to the wrong place with EmptyHandGuard, and was wrong two-handed | The guard is an arm layer of rotations only (`Chooser.Overlay`), so the hand and weapon positions the grip read were still the body clip's (idle, walk), not the guard's. The chooser now measures the guard clip's own left wrist along the weapon line (`GuardAlong`) and the grip goes there as the guard layer comes in (`GuardWeight`). The 0.6.9 "farthest reached" hold is gone |
| Lock-on recentring the camera with nothing to lock | `NoLockOnRecentre` (Controls, on by default): the button then does nothing |
| Guardian's Swordspear held near the tip | It had no entry in `PoleTip`: now slid to ER's 1.9 m grip-to-tip |
| Feints queued a second dodge | A dodge buffers on release; the press that feinted left it armed. The feint disarms it (sandbox too) |
| Training dummy can't be locked | Its Character's faction is `TrainingDummy`, which `BaseAI.IsEnemy` doesn't count; lock candidates take it. The sight check ignores a hit on the target itself (it is a piece) |
| Offhand torch hit as a bare fist | The left blade took an item only for hand weapons; torches too now (fire damage, Clubs) |

0.6.9 packaged (guard hand from the guard clip, NoLockOnRecentre, Swordspear grip, feint dodge, training dummy lock, offhand torch; plus the earlier 0.6.9 round; earlier zips kept).

## 2026-11-03 — weapons askew with the mod switched off

| Report | Cause and fix |
|---|---|
| With Eldenheim off, most weapons held backwards or oddly | The pose turns the hands' attach points to hold items as ER does. Valheim's animator never drives those transforms, so after the fade-out they kept the last turn. `PoseOutput` now blends them from their rest and puts them back at rest when the pose fades out. Built; not checked in game |

## 2026-11-03 — Splitnir and wooden spear backwards, swordspear trails

| Report | Cause and fix |
|---|---|
| Splitnir (all four) and the wooden spear held backwards | A spear's head was taken to be its shorter half from the hand. These two sit centred on the hand (Splitnir -1.49..1.48 m along the attach's Z, wood -1.45..1.39), so that picked the butt. Measured with UnityPy over all 14 spear prefabs (bundle c4210710): every head is towards the attach point's -Z. Spears now take that end |
| Swing trails for spears on the Guardian's Swordspear moveset | Valheim's spears have no `MeleeWeaponTrail`. On its first swing as a swordspear, a spear gets one on child points of the item over the outer 40% of its reach, looking like the iron sword's (its serialized fields copied) |

Built; not checked in game. 0.6.10 packaged (with the attach-at-rest fix; earlier zips kept).
