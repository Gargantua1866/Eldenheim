# Eldenheim — plan

A standalone Valheim mod that puts the Elden Ring sandbox's player into Valheim:
it moves, dodges, jumps, attacks, guards and reacts the way the sandbox does,
with every feature the sandbox has.

- **Sandbox (source of truth):** `C:\Users\G\source\repos\Elden Ring combat`
  (GitHub `Funny-Bones/ELDEN-RING-Combat-Rewrite`, at `177a3b5`).
- **Target:** Valheim at `C:\Games\Valheim`. Unity 6000.0.75f1, Mono, BepInEx
  5.4.23.x, Jotunn 2.30.x.
- **Shared guide:** `C:\Users\G\ValheimModding\VALHEIM_MODDING_GUIDE.md`. Read
  it first, and add verified lessons tagged `(Eldenheim)`.

Status (2026-10-27): milestones 0–1 done (the C# sim matches the Rust sim exactly over the whole demo); the plugin plays the full sandbox moveset in Valheim (milestones 2–4 largely in place). See `docs/MODLOG.md`.

---

## 1. Decisions taken (2026-10-27)

| Question | Decision |
|---|---|
| How much Eldenheim owns | **Full takeover.** While an ER-mapped melee weapon or bare fists are out, the ported sim owns movement, dodge, jump, fall, attacks, guard, hit reactions and stamina use. Valheim keeps inventory, building, interaction, swimming, sailing, riding, sitting, bows, magic and tools. |
| How animations get onto the character | **A runtime animation job.** Re-bake full bone rotations from the HKX data and play them through an `IAnimationJob` on the player's Animator. No Unity editor and no asset bundles. |
| Multiplayer | **Co-op, no PvP** (revised 2026-10-28). Other modded clients see your ER pose and hear your ER sounds (synced through the ZDO, §3.7). Damage follows Valheim's owner rules. |
| Balance | **Hybrid.** Timing comes from ER: frames, i-frames, cancel windows, root motion, hit-stop and motion-value ratios. Base damage, HP, the stamina pool and enemy stagger stay Valheim's, with ER stamina costs scaled to Valheim's bar. |

Why full takeover: most of what went wrong in `mod/EldenRingCombat` (PORTING.md
§9.6, §9.11) came from Valheim's state machine still running underneath. Root
motion banked up in `m_rootMotion`, `HaveQueuedChain` read only the light
button, the crouch flag fought the mod, and the attack state's length held
movement back. A sim that owns the character while it is active removes that
whole class of bug instead of patching each one.

---

## 2. What is reused, and what is not

### Reused from the sandbox

- **`src/sim/`, ported line for line to C#** (about 2,100 lines of logic plus
  1,170 lines of tests). It is engine-free and deterministic at 60 Hz. Its only
  contact with the world is `Level::height(x, z)` and `Level::slide(pos, delta)`.
- **`rig.rs::playing()` and the layer rules** (about 90 lines): which clip, which
  frame, the upper-body layer and the cross-fades. These choose what to show
  from the sim's state. Port them, don't reinvent them.
- **`tools/`**: the readers for DCX/BND/TAE/HKX/params. Eldenheim's own
  generators *import* them (pointed at with `ER_SANDBOX`) rather than copying
  them.
- **`demo.rs`**: its scripted inputs become a test fixture (see §5).

### Reference only (both existing ports have serious flaws)

| Port | Use it for | Don't copy |
|---|---|---|
| `mod/EldenRingCombat` (in the sandbox repo, untracked) | PORTING.md §9: Valheim hook facts, the rig map, root-motion banking, chain traps, the rest-pose and armature-space pitfalls, sound grouping by the last 3 id digits | Its architecture: hook-by-hook overrides of vanilla `Attack`, a position-only bake retargeted by bone direction (causes hand roll and deformation), `LateUpdate` bone writes (capes a frame late), Krom only, F8 toggle (clashes with ReflexValheim) |
| Soulsheim (`repos\soulsheim`) | Its guide entries: Humanoid map of the HKX skeleton, TAE layout, the animator stepping on the physics tick, `animator.speed` vs state speed, saving parameters across controller swaps, numeric retarget checks in Blender | Its premise: it never saw the sandbox, so it reparses TAE on its own, uses `AnimatorOverrideController`s that inherit vanilla state settings, and covers one longsword |

---

## 3. Architecture

```
            ┌────────────── Eldenheim.Sim (pure C#, no UnityEngine) ──────────────┐
 ZInput ──► │ Input ─► Player.Step @60Hz ─► State, pose request, ActiveHit       │
            │            ▲  IWorld (ground, slide, targets)                       │
            └────────────┼───────────────────────────┬─────────────────────────────┘
                         │                           │
          Eldenheim.Valheim (BepInEx plugin)         ▼
   ┌───────────────┬───────────────┬──────────────────────────┬──────────────────┐
   │ Body driver   │ Combat bridge │ Animation                │ Net              │
   │ Rigidbody move│ blade capsule │ clip chooser (rig.rs)    │ ZDO pose fields  │
   │ ground/slide  │ → HitData     │ IAnimationJob on Animator│ remote playback  │
   │ via PhysX     │ incoming hits │ foot IK, weapon attach   │                  │
   └───────────────┴───────────────┴──────────────────────────┴──────────────────┘
```

### 3.1 Eldenheim.Sim

- `netstandard2.1` or `net472` class library with **no Unity reference**, so its
  tests run with `dotnet test` outside the game. It uses `System.Numerics.Vector3`.
  Conversion to Unity happens only at the bridge.
- `Level` becomes an interface:
  `IWorld { float Height(x, z); void Slide(ref pos, delta); IEnumerable<Target> Targets }`.
  The sandbox arena implements it for tests, and Valheim physics implements it in game.
- The sparring `Dummy` stays in the sim only, for the differential replay
  test; it has no in-game form (removed 2026-10-28, with the sky-arena demo).
- Data loads from `eldenheim_actions.bin` (§4) instead of the generated `extracted.rs`.

### 3.2 Body driver (Valheim side)

- **Tick:** Valheim's physics runs at 50 Hz. The sim runs 60 Hz ticks from an
  accumulator inside the player's `FixedUpdate`, and rendering interpolates with
  `alpha`, as the sandbox does. Never resample the sim to 50 Hz.
- **Movement (verified in spike 0c):** a prefix on `Character.UpdateMotion`
  for the local player in Eldenheim mode skips Valheim's walking and root
  motion and clears `m_rootMotion`. The sim's per-tick displacement becomes the
  body's velocity, so PhysX handles collision. The next tick reads the real
  position back into the sim. When grounded, the step walks **along the
  ground's plane**, with the normal taken at the capsule's front edge and
  scaled to keep the sim's horizontal speed. A **step-up sweep** handles
  discrete steps up to `STEP_HEIGHT`. `IWorld.Height` is a downward
  `SphereCast` on Valheim's walkable layer mask.
- **Noise:** the takeover leaves Valheim's noise at 0 (verified), so Eldenheim
  sets it per state: walk, run, sprint, roll, attack.
- **Gravity:** the sim's own (`GRAVITY = 18`) while it is in `Air`, not
  Valheim's −20.
- **Hand back to vanilla** (exit Eldenheim mode cleanly) for: swimming,
  attached/riding/sitting, debug fly, death, teleport, a non-ER item equipped,
  and the toggle key. Re-entry waits for the ground.
- **Stamina and HP:** the sim's numbers are a *view* of Valheim's.
  `stamina_er = m_stamina × (ER_MAX / GetMaxStamina())`, and spending goes
  through `UseStamina`, so food, rested and status effects still apply.
- **Equip load:** Light/Medium/Heavy from the total `m_movementModifier` of the
  armour worn. Cutoffs are configurable.
- **Fall damage:** the sim's thresholds replace `m_maxAirAltitude` damage while
  in Eldenheim mode.

### 3.3 Combat bridge

- **Outgoing:** the sim's `ActiveHit` gives blade segments in the character's own
  space for each sweep step. Transform them to world space and test each tick
  with `Physics.OverlapCapsule` against Valheim's melee mask: characters, pieces,
  trees, rocks, destructibles.
- **HitData is built the way `Attack.DoMeleeAttack` builds it.** Damage comes
  from the item, scaled by ER motion value relative to the chain's first hit;
  also tool tier, skill, push force, backstab, stagger, durability and skill XP.
  This keeps woodcutting, mining tiers, PvP flags and skill progress working.
  Send it with `IDestructible.Damage`.
- **Once per target per hit**, as the sim's `landed` bitmask already does.
- **Hit-stop:** the sim's `hit_stop` on the player, and `Character.FreezeFrame(stop)`
  on the target.
- **Poise to stagger:** ER `guard_damage` × config factor → `m_staggerMultiplier`.
- **Incoming:** prefix `Character.RPC_Damage` on the local player (it runs on the
  owner). The sim judges the hit through `receive_hit`: i-frames, a jumped low
  sweep, guard (arc and stamina) or guard break. Its hurt level comes from
  Valheim's damage and stagger relative to max HP. Then:
  - Dodged: drop the hit. Postfix `IsDodgeInvincible` too, for other callers.
  - Blocked: reduce it the way Valheim's block does, with ER guard stamina.
  - Otherwise: let Valheim apply health damage, and the sim plays the reaction.
- **Lock-on:** the nearest hostile `Character` within `LOCK_ON_RANGE`, in view.
  A camera postfix on `GameCamera.UpdateCamera` provides the lock-on camera.

### 3.4 Animation

- **New bake (`tools/bake_unity.py`, Eldenheim's own):** for every clip the sim
  can request, sample *local rotations* (quaternions) of the 150-bone
  `Skeleton.hkx` at 30 fps, plus the root/pelvis translation and the rest pose.
  The sandbox's `player_anims.bin` stays untouched; that format belongs to the
  sandbox.
- **Retarget at load**, per mapped bone, in the character's own space (not the
  Armature's, which carries −90° X):
  `q_tgt = q_tgt_rest · (q_src_rest⁻¹ · q_src)`, after converting ER axes (X
  mirrored) into Unity's. Fingers included. The rest reference is Valheim's bind
  pose read from the SkinnedMeshRenderer's bindposes, never the spawn pose.
- **Playback (verified in spike 0a):** an `AnimationScriptPlayable` running an
  `IAnimationJob`, in a second `PlayableGraph` whose `AnimationPlayableOutput`
  targets the player's Animator. That output starts from *defaults*, not from
  Valheim's pose, so the job writes the **whole pose**: every mapped bone, and
  every other transform under the Armature at its bind pose (fingers until they
  are animated, jaw, attach points). Output weight 1 means ER pose, 0 means
  vanilla, 0.5 blends; fading the weight gives the hand-off. Valheim's
  controller keeps running, so `ZSyncAnimation`, equipment and effects stay
  sane. Injecting a job into `animator.playableGraph` was tried and has no
  effect.
- **Foot IK:** Valheim's humanoid foot IK runs after the output and re-plants
  the feet, so it must be turned off for the local player while Eldenheim owns
  the pose. Eldenheim places the feet itself.
- **Hips height and foot placement:** port the sandbox's ground-following feet.
  Two-bone IK in the job, from ground raycasts made on the main thread.
- **Weapons:** keep `VisEquipment`'s attachment to `RightHand_Attach` /
  `LeftHand_Attach`. The ER `R_Weapon`/`L_Weapon` orientation drives the attach
  bone, relative to its rest pose (the trick §9.4 found for the head).

### 3.5 Weapon mapping

| Valheim | ER class |
|---|---|
| Swords (1H) | Longsword. Per-item overrides: Mistwalker → Longsword, curved/rapier-like modded items → Curved Sword or Rapier |
| Swords (2H) | Claymore / Greatsword (by weight) |
| Knives | Dagger. Dual knives (Skoll and Hati) → paired Dagger |
| Clubs (1H) / sledges (2H) | Club / Great Hammer. Very heavy ones → Colossal Weapon |
| Axes (1H) / battleaxes (2H) / Berserkir | Battle Axe / Greataxe / paired Battle Axe |
| Polearms (atgeirs) | Halberd |
| Spears | Short Spear. Heavy spears → Great Spear |
| Unarmed | Fist |
| Shields, Torch | Shield, Torch (left hand) |
| Bows, crossbows, staves, pickaxes, fishing rod, hammer, hoe | Vanilla (Eldenheim mode off) |

The table is a config file (`eldenheim_weapons.yaml`, keyed by item prefab or
skill+type), so modded weapons can be mapped without code. ER classes that
Valheim lacks (Twinblade, Reaper, Whip, Flail, Claw, Heavy Thrusting Sword,
Uchigatana, Curved Greatsword) are available to the config.

**Off-hand weapons and power stance:** Valheim won't put a 1H weapon in the
left hand. Eldenheim adds a left-hand weapon slot: a hotbar modifier equips it,
the mod attaches it to `LeftHand_Attach`, and the item is synced in the ZDO.
This comes last (milestone 6), because it touches inventory and multiplayer.

### 3.6 Input

Read Valheim's own button names through `ZInput`, so the user's bindings apply.
Mod-only keys are BepInEx config entries, so they can be rebound in F1. The
bindings on this PC are not stock: Use = F (default E), Run = LeftShift,
SecondaryAttack = Mouse forward, Crouch = LeftCtrl, AutoRun = Q, ToggleWalk = M.

Settled 2026-10-27:

| Sim input | Binding | Note |
|---|---|---|
| move, camera | Valheim's | |
| dodge: tap = roll/backstep, hold = sprint | Valheim **Run** (LeftShift here) | ER's one-button dodge/sprint on the key Valheim already sprints with. Roll/sprint is decided on release, as in the sim |
| jump | Valheim **Jump** (Space) | |
| light attack | Valheim **Attack** (LMB) | |
| heavy attack (hold to charge) | Valheim **SecondaryAttack** (Mouse forward here) | Shift + LMB is *not* heavy, because Shift is now dodge |
| guard / left-hand attack | Valheim **Block** (RMB) | |
| guard counter | heavy after a blocked hit | |
| walk | Valheim **ToggleWalk** (M here) | |
| crouch | Valheim **Crouch** (LeftCtrl here) | |
| lock-on | **Caps Lock** (mod config) | free in Valheim and in the guide's list of taken keys; with nothing to lock onto it recenters the camera |
| two-hand right / left | **hold Valheim Use + Attack / + Block** | follows Use's binding (F here, E by default); see the note below |
| next weapon / next left | Valheim hotbar; D-pad on gamepad | |

**Use as a modifier:** Valheim interacts on Use's press, so pressing Use next to
a door or chest still interacts before the attack button turns it into a
two-hand. Accept that in milestone 4, and if it bites in play, defer the
interaction until Use is released without an attack.

Gamepad mapping follows the sandbox's column. Check the guide's list of taken
keys before adding any new mod key.

**Rebinding in the config menu (F1).** Every input above is a config entry,
in two sections, so keyboard and gamepad can be rebound independently:

- `Input - Keyboard & Mouse`: one entry per action. The default for each is
  `Valheim` (follow Valheim's own button, so in-game rebinding keeps working)
  or a mod key such as Caps Lock. Setting a key there overrides Valheim's
  binding *for Eldenheim only*.
- `Input - Gamepad`: the same actions, with defaults from the sandbox's gamepad
  column (B dodge/sprint, A jump, RB/RT light/heavy, LB guard, R3 lock-on,
  Y + RB/LB two-hand, D-pad swaps, L3 crouch). Input System paths, e.g.
  `<Gamepad>/buttonEast`.

Both sections are read through one `Bindings` layer that produces the sim's
`Input`. Nothing else in the mod reads keys directly.

**Dodge options** (in `Input - Behaviour`, shared by both devices):

- `SeparateSprint` (default **off**): off is ER's one button, tap to dodge and
  hold to sprint. On splits them: the dodge key only dodges, and a separate
  `Sprint` binding (keyboard and gamepad entries in their own sections) holds
  to sprint. Defaults when split: dodge = Left Alt / B, sprint = Left Shift /
  L3. With the split, `SPRINT_HOLD_FRAMES` no longer applies.
- `DodgeTrigger` = `Release` (default, as in ER) or `Press`. Press dodges on
  the frame the button goes down. In one-button mode, press means you can't
  sprint from that button, so `Press` with `SeparateSprint` off is allowed but
  the config description says sprint then needs the split.

The sim gains these two flags in `Input` or its settings. The Rust sim behaves
as `Release` + one-button, which is what the differential tests compare.

**On/off:** no default hotkey. Eldenheim mode is on whenever an ER-mapped
melee weapon or fists are out. The F1 Configuration Manager has a master
`Enabled` switch and an optional `ToggleKey`, **unbound by default** (F8 is
ReflexValheim's).

**Sparring dummy:** none in game (removed 2026-10-28); use Valheim's own
training dummies and creatures.

### 3.7 Multiplayer (co-op; revised 2026-10-28)

Scope (the user, 2026-10-28): co-op only, **PvP is not a concern**; other
players must **hear the ER sounds**; the sky-arena demo and the in-game
sparring dummy are gone.

**Already right by construction (no code):**
- Damage: our swings go `IDestructible.Damage` → the target's `RPC_Damage` on
  its owner; hits on us are judged by our own sim (guard, i-frames) in our
  `RPC_Damage` prefix.
- Effects made with `EffectList.Create(..., vp.GetZDOID())` (Valheim swing,
  hit, jump, dodge effects) are networked objects; footsteps go out as
  Valheim's routed `Step` RPC. Position and rotation: `ZSyncTransform`. Hand
  items: `VisEquipment`'s ZDO hashes.

**Remote pose (built 2026-10-28, not yet seen in game): sync the chooser's
inputs, not the pose.** `Chooser.Update(SimPlayer, …)` reads only a few dozen
sim fields, so the owner writes those and remotes run the same `Chooser` →
`Retarget` → `PoseOutput` on that player's rig:
- `Eldenheim.Sim.PoseState` packs them (~50 B: version, state, act id and
  frame, air, death time, speed, move direction, sprint/crouch/guard, weapon,
  left, grip, hit-stop, air attack, swap). The replay test round-trips it on
  every tick of the demo. The class is the owner's choice (F1 menus differ per
  client), so it travels; it is never looked up from the remote's config.
- The owner sets ZDO `eh_pose` (empty while Eldenheim is off) and `eh_data`
  (the data hash) each physics step when the bytes changed; `ZDOMan` sends
  every 0.05 s.
- A remote's `Controller.FixedUpdate` fills a stand-in `SimPlayer` from it (no
  sim step), moves its frames on at 30 fps between updates (`PoseState.Advance`,
  not in hit-stop), takes the yaw from the synced transform, and keeps its own
  blend state, so 20 Hz updates still play smoothly. Foot IK runs on the
  remote's own ground raycasts. Its hand items come from `VisEquipment`'s
  synced hashes (a remote Humanoid holds none). Grip rotations, two-handing,
  back-sheathing and pole slides run as for the local player; Valheim's foot and
  head IK are skipped for any posed player (`Controller.Posing`).
- Rejected: streaming the finished pose (~300 B per update, plus held-item
  rotations and IK; exact but heavy) and running the whole sim on remotes from
  inputs (diverges under PhysX).
- Weapon trails (built 2026-10-28): a remote tracks its own blades and runs
  `Combat.Swings` on the stand-in sim without swing sounds (the owner's are
  networked effects), so trails start and stop on the same hit windows;
  paired weapons split their halves on the remote as on the owner. A late
  update never steps a frame back within an action (it would read as a new
  swing and flash a finished trail).
- Still to do: a swap's new item shows on remotes from the swap's start, not
  its change-over frame.

**ER sounds for everyone (built 2026-10-28, not yet heard in game):**
`ErSounds` plays through a local 3D `AudioSource`, so nobody else heard it. A
remote now plays both ER sounds from the synced state as it changes
(`Controller.RemoteSounds`): the guard counter's own sounds when a guard
counter starts (shifted by how far into the clip the update arrived), and the
guard break when the `GuardBreak` act starts. Volumes and the on/off switch are
the listener's settings.

**Gaps that only show in multiplayer:**
- **Every player needs Elden Ring and the data chain**; nothing from ER can be
  shipped. Weapon and action numbers index the data build, so the ZDO carries
  `Plugin.DataHash` (FNV of `eldenheim_data.bin`, xor the anims file's length);
  a remote with a different build shows that player in vanilla animation and
  logs one line (done with the pose sync). The codec's version byte covers
  plugin version mismatches.
- **Stagger (done 2026-10-28):** `Incoming` zeroes `m_staggerMultiplier` on
  unblocked hits (infinite poise), so Valheim staggers the player only when a
  blocked hit fills the block meter, which already plays ER's GuardBreak.
  Vanilla's `RPC_Stagger` still turned the body to the blow and played a
  hidden stagger under the pose; it is now skipped while Eldenheim is active.
  A parry by another player (`attacker.Stagger`) is PvP, out of scope.
- **Balance settings (built 2026-10-28, not yet tried on a server):** the F1
  Balance section changes a player's own sim and hits, so it is the server's.
  `BalanceSync` (no Jotunn: Valheim's peer RPC) sends the server's or host's
  Balance values to each client after its peer info and again whenever the
  admin changes one. A client uses them for the session with its config file
  unsaved, and gets its own values back (and the file saved) when it leaves.
  A server without Eldenheim sends nothing, so clients keep their own. Every
  other section (input, visuals, sound, the weapon classes, ThrowSpears) stays
  per client.
- **Dedicated server (reviewed 2026-10-28 from the code; not run: no server
  is installed on the dev PC).** The server needs Eldenheim only for the
  Balance sync; without it clients just keep their own settings. On a server:
  - no ER data → "missing … stays off" in the log, `DataHash` 0;
  - the server instantiates player objects around its peers, so each gets a
    `Controller`: `Update` returns (not the local player), `FixedUpdate` takes
    the remote path and stays off (no clips), `LateUpdate` has no pose;
  - every patch goes through `Owning` (needs the local player) or
    `Controller.Posing` (needs an active controller), so creatures, damage and
    blocking run vanilla; `Hands` checks the local player too;
  - `MagicaClothV2.dll` (referenced by `Controller`) ships with any Valheim
    build, since `assembly_valheim`'s own `Player` and `VisEquipment` use it;
  - no input, GUI or audio runs without a local player (`OnGUI` doesn't run
    in batch mode).
  To try it: install "Valheim Dedicated Server" (Steam, Tools), BepInEx and
  Eldenheim's DLLs, start it with `start_server_bepinex.sh`'s equivalent, and
  look for `[Eldenheim] loaded` and no exceptions in its LogOutput.log; then
  join and change a Balance value on the server. A hosted world (Start
  server in the menu) exercises the same server side of `BalanceSync`.

### 3.8 Sound

- **Vanilla:** play the item's own `m_triggerEffect` / `m_hitEffect` /
  `m_trailStartEffect` on the sim's frames, because the vanilla clip events
  that would fire them are no longer what is on screen.
- **Optional ER sounds:** `player_sounds.bin` events, with §9.16's rule. Play
  only recordings that are unique to an action family, through the SFX mixer
  group, with Valheim's reverb rule (from the guide).

---

## 4. Data pipeline (nothing from either game is shipped)

`tools/` in Eldenheim, Python, importing the sandbox's readers via `ER_SANDBOX`:

| Script | Output (next to the deployed DLL) |
|---|---|
| `export_actions.py` | `eldenheim_actions.bin`: everything `extract.gather()` produces (ActionDefs, hits, blades, motion, weapons, air attacks, reactions, swaps), versioned, little-endian |
| `bake_unity.py` | `eldenheim_anims.bin`: local rotations of each clip, plus the rest pose and the clip list |
| `export_sounds.py` (later) | the sandbox's sound bake, repackaged |

The mod runs without these files: it logs what is missing and stays in vanilla
mode. The package contains code only. The README walks each user through
generating the files from their own ER install (README §Setup in the sandbox,
including `c0000_a5x`, `a6x` and `a9x`).

---

## 5. Testing

- **Differential tests (the main safety net):** a small Rust harness compiles
  the sandbox's `src/sim` (through a `#[path]` module and a stub `bevy::math`
  crate over `glam`). It records `Input` plus the state each tick for:
  - the full `demo.rs` script (about 5 minutes, which covers every feature);
  - each `tests.rs` scenario.
  
  The C# sim replays the same inputs and must match within float tolerance. Any
  divergence names the tick and the field.
- **Port `tests.rs`** to xUnit as well, for readable failures.
- **Test character:** the Valheim test character already holds a straight
  sword and shield (→ Longsword + Shield), ready for automated in-game tests.
- **In game:** the user also tests. Build, deploy and report. A
  command-file test harness (ReflexValheim pattern) drives scripted checks only
  when asked.
- **Numeric retarget checks** (Soulsheim pattern): log
  `InverseTransformPoint` of hips, feet and hands for chosen frames, and compare
  them with the bake's FK.

---

## 6. Milestones

Each one ends in something you can play, packaged as a Gale zip.

0. **Spikes (de-risk before porting).** Each is a throwaway plugin.
   - a. An animation job output on Valheim's player overrides the controller at
     weight 1, hands back at 0, and leaves capes, equipment and `ZSyncAnimation`
     intact.
   - b. A rotation bake plus retarget of `a000_000000` (idle) and one walk
     looks right, with feet on the ground.
   - c. A body takeover: the vanilla motion is suppressed, and a constant-speed
     60 Hz mover walks over terrain, stairs and building pieces without
     tunnelling or jitter.
   - d. Shift as dodge: with Run's sprint suppressed in Eldenheim mode, nothing
     else in Valheim reacts to the held Run button (stamina drain, run
     animations, noise).
1. **Sim port + differential tests.** The C# sim matches the Rust sim over the
   whole demo script. No Valheim yet.
2. **Locomotion in game:** walk, run, sprint, crouch (including enter/exit),
   jump in every direction, fall and landings, roll and backstep by load,
   lock-on strafing and the camera, stamina through Valheim's bar.
3. **One weapon end to end (Swords → Longsword):** light/heavy/charged chains,
   running, rolling, backstep, crouch and jump attacks, blade hits building real
   HitData, hit-stop, stagger, the guard and guard counter, incoming hits with
   i-frames, the four reaction levels and knockdown.
4. **Every Valheim melee class** through the mapping table, two-handing, grip
   and weapon swaps on the upper body, shield and torch.
5. **Co-op (§3.7):** remote pose sync, data hash, ER sounds,
   weapon trails and server balance (built); the dedicated server
   reviewed from the code, not yet run.
6. **Left-hand weapon slot and paired movesets** (all 22).
7. **Sound, config polish, README and Gale package**, and lessons added to the
   guide.

---

## 7. Risks

| Risk | Mitigation |
|---|---|
| A second graph's output may not override the controller cleanly in Valheim's build, or may break `FreezeFrame`/`animator.speed` tricks | Spike 0a decides it. The fallback is the job inside a graph built from the controller (`AnimatorControllerPlayable`), with Valheim's `SetTrigger` calls forwarded |
| Retarget errors (axis conventions per bone family) | Rotations, not directions. Rest from bindposes. Numeric checks at fixed frames |
| PhysX vs the sim's height-field assumptions (steps, slopes, moving ships) | Spike 0c. On ships, hand back to vanilla |
| Valheim systems that read vanilla state (`InAttack`, `IsBlocking`, `m_currentAttack`, AI that reacts to blocking) | Postfix the readers to report the sim's state while in Eldenheim mode, listed and tested at milestone 3 |
| Animation events that vanilla gameplay relies on (footstep sounds, noise for AI) | Fire `CharacterAnimEvent.FootStep` from the sim's step frames. `UpdateNoise` stays vanilla |
| Mod conflicts (CameraMod, Soulsheim, EldenRingCombat in the same install) | Detect them and log. Never run with Soulsheim or EldenRingCombat loaded. Use one Harmony ID |
| Legal | Generated data only, per user, never committed or shipped. Lint the package |
| ER's animation speed events (TAE 608 `AnimSpeedGradient`) | Done 2026-10-29: `extract.py` reads them into `ActionDef.speed`/`AirAttackDef.speed`; the Rust sim and the C# port advance the action's frame by `DF * speed_at(frame)` (sweeps over the same step); replay exact |

---

## 8. Open questions for the user

Settled 2026-10-27, recorded in §3.6: jump, dodge and sprint; lock-on;
two-hand; on/off; the sparring dummy.

Still open:

1. New Forgejo repo for Eldenheim: you create an empty repo named `Eldenheim`
   in the web UI when you want it pushed.
