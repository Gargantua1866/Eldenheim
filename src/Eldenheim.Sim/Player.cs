using System;

namespace Eldenheim.Sim
{
    // Port of src/sim/player.rs.

    public static class SimMath
    {
        /// <summary>Yaw 0 faces +Z; positive yaw turns toward +X.</summary>
        public static Vec3 DirOf(float yaw) => new Vec3(F.Sin(yaw), 0f, F.Cos(yaw));
        public static float YawOf(Vec3 dir) => F.Atan2(dir.X, dir.Z);
        public static float AngleDiff(float from, float to) => F.RemEuclid(to - from + F.PI, F.TAU) - F.PI;
        public static float TurnToward(float yaw, float goal, float maxStep) => yaw + F.Clamp(AngleDiff(yaw, goal), -maxStep, maxStep);
        public static float Approach(float value, float goal, float maxStep) => value + F.Clamp(goal - value, -maxStep, maxStep);

        /// <summary>Shortest distance between the segments a-b and c-d.</summary>
        public static float SegmentDistance(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
        {
            Vec3 u = b - a, v = d - c, w = a - c;
            float uu = u.Dot(u), uv = u.Dot(v), vv = v.Dot(v), uw = u.Dot(w), vw = v.Dot(w);
            float denom = uu * vv - uv * uv;
            float s = denom > 1e-8f ? F.Clamp((uv * vw - vv * uw) / denom, 0f, 1f) : 0f;
            float t = vv > 1e-8f ? F.Clamp((uv * s + vw) / vv, 0f, 1f) : 0f;
            if (uu > 1e-8f)
            {
                s = F.Clamp((uv * t - uw) / uu, 0f, 1f);
            }
            return (a + u * s).Distance(c + v * t);
        }
    }

    public struct Button
    {
        public bool Held, Pressed, Released;
    }

    public struct Input
    {
        /// <summary>Movement stick: x right, y forward, relative to the camera.</summary>
        public Vec2 Mv;
        public float CamYaw;
        public Button Dodge, Jump, Light, Heavy, Guard;
        public bool Crouch, Lock, Walk, TwoHandRight, TwoHandLeft, NextWeapon, NextLeft;

        public float Tilt() => F.Min(Mv.Length(), 1f);

        public bool Wish(out Vec3 dir)
        {
            dir = Vec3.Zero;
            if (Mv.Length() < Consts.STICK_DEADZONE)
            {
                return false;
            }
            Vec3 forward = SimMath.DirOf(CamYaw);
            var right = new Vec3(-forward.Z, 0f, forward.X);
            dir = (forward * Mv.Y + right * Mv.X).Normalize();
            return true;
        }
    }

    public enum Req : byte { Light, Heavy, Left, Dodge, Jump }

    public enum LeftButton { Guard, Attack }

    public struct Act
    {
        public ActionId Id;
        public float F;
        public uint Landed;
        public uint Paid;
    }

    public struct Swap
    {
        public SwapKind Kind;
        public float F;
        public Grip Grip;
        public int Weapon;
        public int Left;
        public bool Applied;
    }

    public struct AirAttack
    {
        public bool Heavy;
        public bool Paired;
        public Moveset Moveset;
        public AirAttackDef Def;
        public float F;
        public bool HitDone;
        /// <summary>Carried back into the air from its landing (ResumeAirAttacks): already paid for.</summary>
        public bool Resumed;
    }

    public struct Air
    {
        public Vec3 Vel;
        public bool Jumped;
        public float F;
        /// <summary>Knocked off a ledge by a hit (flinch, guard blow or break): tumbles back, lands in the sprawl.</summary>
        public bool Thrown;
        public bool Falling => !Jumped || F >= Consts.JUMP_BECOMES_FALL;
    }

    public enum StateKind : byte { Ground, Act, Air, Dead }

    public class Incoming
    {
        public float Damage, Stamina;
        public Vec3 From;
        public bool Low;
        public HurtLevel Level;
    }

    public enum HitResult { Ignored, Dodged, Jumped, Blocked, GuardBroken, Hit, Killed }

    public class ActiveHit
    {
        public float Attack, Mv, GuardDamage, Stop, Radius;
        public Vec3[] SweepA = new Vec3[Player.SWEEP_STEPS];
        public Vec3[] SweepB = new Vec3[Player.SWEEP_STEPS];
    }

    public class Player
    {
        public const int SWEEP_STEPS = 5;
        private const float RESPAWN_FRAMES = 90f;

        public Vec3 Pos;
        public float Yaw;
        public float Hp;
        public float Stamina;
        public StateKind State;
        public Act Act;
        public Air Air;
        public float DeadT;
        public float Speed;
        public Vec3 MoveDir;
        public bool Sprinting, Crouching, Guarding;
        public Load Load;
        public int Weapon;
        public int Left;
        public Grip Grip;
        public bool InCombat;
        public Req? Buffer;
        public AirAttack? AirAttackState;
        public Swap? SwapState;
        public float HitStop;
        public Vec3 Spawn;
        private float _guardT;
        private float _guardCounter;
        private float _dodgeHold;
        private bool _dodgeArmed;
        private bool _sprintSpent;
        private float _jumpBase;
        private float _peak;

        // Eldenheim's departures from the sandbox. The defaults are the
        // sandbox's behaviour (what the replay test checks); Eldenheim sets them.
        /// <summary>The guard can be raised while sprinting, without ending the sprint.</summary>
        public bool GuardWhileSprinting;
        /// <summary>With nothing in the left hand, the guard button guards with the right weapon instead of punching.</summary>
        public bool EmptyHandGuards;
        /// <summary>Falls never hurt or kill here: the host applies its own fall damage.</summary>
        public bool ExternalFallDamage;
        /// <summary>Move straight where the stick points while the body turns towards it (instead of along the facing).</summary>
        public bool MoveAlongWish;
        public float TurnRun = Consts.TURN_RUN, TurnSprint = Consts.TURN_SPRINT;
        /// <summary>The drop that ends in the heavy (knocked-down) landing.</summary>
        public float HeavyLandingFall = Consts.FALL_HEAVY_LANDING;
        /// <summary>Asked before an attack starts; false and it doesn't (the host's stamina rules).</summary>
        public Func<ActionId, bool> CanAfford;
        public Func<Moveset, AirAttackDef, bool> CanAffordAir;
        /// <summary>Crouched, the left button does the power-stance crouch attack (the rolling one, as in ER).</summary>
        public bool PairedCrouchAttack;
        /// <summary>Attacks pressed any time in a roll or backstep are kept for its attack window (not only from its input frame).</summary>
        public bool EarlyRollAttacks;
        /// <summary>Crouch pressed during an action takes effect as it ends (instead of being lost).</summary>
        public bool QueueCrouch;
        /// <summary>Jump pressed while already in the air is ignored (no second jump on landing).</summary>
        public bool NoAirJumpBuffer;
        /// <summary>As in ER: after a jumping attack the light chain goes on at its second attack.</summary>
        public bool JumpAttackChains;
        /// <summary>A heavy press this many frames or fewer after the heavy button came up is ignored (a bouncing button after a charge).</summary>
        public float HeavyDebounce;
        /// <summary>Paired weapons (one item in both hands, like claws): the light button plays the power-stance attacks and the guard button guards.</summary>
        public bool PairedOnLight;
        /// <summary>Rolls and backsteps are invincible from their start for this many frames instead of ER's own windows (0: ER's).</summary>
        public float DodgeIframes;
        /// <summary>Infinite poise: hits hurt but never stagger or knock down (guarding still reacts).</summary>
        public bool NoFlinch;
        /// <summary>Walking off a drop smaller than this lands without a landing animation (metres; 0: always one).</summary>
        public float MinLandingFall;
        /// <summary>A drop walked or run off lands in the fall landing (which stops) only from this high; a shorter one lands as a jump does, running on (0: every one).</summary>
        public float StopLandingFall;
        /// <summary>A drop this small (metres) while walking is followed down instead of falling.</summary>
        public float StepDown;
        /// <summary>A jump that lands while moving but not sprinting plays the light landing (the running one is for sprints).</summary>
        public bool LightLandings;
        /// <summary>Steering in the air: the speed (m/s) the stick can push towards, and how fast (m/s²). 0: none.</summary>
        public float AirSpeed, AirAccel;
        /// <summary>Falls no faster than this (m/s; 0: no limit), and never end in a heavy landing (a slow-fall effect).</summary>
        public float MaxFallSpeed;
        /// <summary>For this many frames after take-off a jump turns at once to where the stick points.</summary>
        public float JumpTurnFrames;
        /// <summary>Eldenheim (Valheim's jump): a jump leaves the ground at once, from the clip's first rising frame.</summary>
        public bool InstantJump;
        /// <summary>Sprint speed and jump height multipliers (the host's skills).</summary>
        public float SprintScale = 1f, JumpScale = 1f;
        /// <summary>Walking, running and crouched speed multiplier (the host's gear).</summary>
        public float MoveScale = 1f;
        /// <summary>Eldenheim: a held dodge button sprints from the first frame (its separate sprint button).</summary>
        public bool InstantSprint;
        /// <summary>Attack presses during an air attack are ignored (nothing queued after it lands).</summary>
        public bool NoAirAttackBuffer;
        /// <summary>
        /// Eldenheim: a jump attack's air swing stays live after it strikes (the
        /// host remembers what each blade struck), and on landing only the
        /// landing's hits that fall within that swing are skipped, not all of
        /// them: a later blow (the power-stance battle axe's second chop) still strikes.
        /// </summary>
        public bool LandFollowUps;
        /// <summary>Eldenheim: a jump attack landing mid-swing goes on at the swing's frame, never stepping back to the landing's first hit.</summary>
        public bool LandInPlace;
        /// <summary>
        /// Eldenheim: a jump attack's landing that walks off a ledge (or a creature's
        /// back) goes on in the air and lands again (in its short landing once the
        /// swing is over), instead of ending in a plain fall.
        /// </summary>
        public bool ResumeAirAttacks;
        /// <summary>
        /// Eldenheim: Elden Ring's free aim, unlocked: how far (radians, up positive)
        /// the attack's blade path is tilted about the hips, as the drawn upper body is.
        /// </summary>
        public float AimPitch;
        /// <summary>Height above the feet the aim tilts about (about the hips).</summary>
        public const float AIM_PIVOT = 1f;
        /// <summary>
        /// Eldenheim: Elden Ring's reverse backstep (which the sandbox lacks). A
        /// move input within this many frames of a backstep's start, not locked
        /// on, turns the body to face it, so the backstep carries the other way
        /// (back: forward; left: right; forward: a plain backstep). 0: off.
        /// </summary>
        public float ReverseBackstepFrames;
        /// <summary>
        /// Eldenheim: a backstep attack, not locked on, turns this fast (°/s)
        /// towards the stick until its first hit, so it can follow a reverse
        /// backstep the way it went. 0: the sandbox's turning.
        /// </summary>
        public float BackstepAttackTurn;
        /// <summary>Whether stamina regenerated on the last step (the host pauses its own regeneration otherwise).</summary>
        public bool Regenerating { get; private set; }
        private bool _crouchQueued;
        private float _sinceHeavyUp = 999f;

        public Player(Vec3 spawn)
        {
            Reset(spawn);
        }

        private void Reset(Vec3 spawn)
        {
            Pos = spawn;
            Yaw = 0f;
            Hp = ErData.MAX_HP;
            Stamina = ErData.MAX_STAMINA;
            State = StateKind.Ground;
            Act = default;
            Air = default;
            DeadT = 0f;
            Speed = 0f;
            MoveDir = Vec3.UnitZ;
            Sprinting = Crouching = Guarding = false;
            Load = Load.Medium;
            Weapon = ErData.DEFAULT_WEAPON;
            Left = ErData.SHIELD;
            Grip = Grip.OneHand;
            InCombat = false;
            Buffer = null;
            AirAttackState = null;
            SwapState = null;
            HitStop = 0f;
            Spawn = spawn;
            _guardT = _guardCounter = _dodgeHold = 0f;
            _dodgeArmed = _sprintSpent = _crouchQueued = false;
            _jumpBase = _peak = spawn.Y;
        }

        public Moveset Moveset()
        {
            switch (Grip)
            {
                case Grip.OneHand: return new Moveset(Weapon, false);
                case Grip.TwoHandRight: return new Moveset(Weapon, true);
                default: return new Moveset(Left, true);
            }
        }

        public bool Paired() => Grip == Grip.OneHand && Left == Weapon && Moveset().Has(AttackKind.PairedLight1);

        public LeftButton LeftButtonDoes() =>
            Grip == Grip.OneHand && Left != ErData.SHIELD && !GuardsWithRight() ? LeftButton.Attack : LeftButton.Guard;

        /// <summary>
        /// The right weapon guards: one-handed with nothing in the left hand and
        /// EmptyHandGuards on, or paired weapons (PairedOnLight).
        /// </summary>
        public bool GuardsWithRight() => Grip == Grip.OneHand && Weapon != ErData.FIST &&
            (EmptyHandGuards && Left == ErData.FIST || PairedOnLight && Left == Weapon);

        private ActionId? PickLeft(AttackKind[] paired, AttackKind[] single)
        {
            if (Paired())
            {
                return Pick(paired);
            }
            var moveset = new Moveset(Left, false);
            foreach (AttackKind kind in single)
            {
                if (moveset.Has(kind))
                {
                    return ActionId.Attack(moveset, kind);
                }
            }
            return null;
        }

        private ActionId? Pick(params AttackKind[] kinds)
        {
            Moveset moveset = Moveset();
            foreach (AttackKind kind in kinds)
            {
                if (moveset.Has(kind))
                {
                    return ActionId.Attack(moveset, kind);
                }
            }
            return null;
        }

        public Vec3 Facing() => SimMath.DirOf(Yaw);

        private Vec3 LeftDir()
        {
            Vec3 f = Facing();
            return new Vec3(f.Z, 0f, -f.X);
        }

        public bool IsDead => State == StateKind.Dead;

        public bool Airborne()
        {
            switch (State)
            {
                case StateKind.Air: return true;
                case StateKind.Act: return Act.Id.Tag == ActionTag.Jump && Act.Id.Def().UpAt(Act.F) > 0f;
                default: return false;
            }
        }

        public bool Invincible()
        {
            switch (State)
            {
                case StateKind.Act:
                    if (DodgeIframes > 0f && (Act.Id.Tag == ActionTag.Roll || Act.Id.Tag == ActionTag.CrouchRoll || Act.Id.Tag == ActionTag.Backstep))
                    {
                        return Act.F < DodgeIframes;
                    }
                    ActionDef d = Act.Id.Def();
                    return Act.F >= d.IframesFrom && Act.F < d.IframesTo;
                case StateKind.Dead:
                    return true;
                default:
                    return false;
            }
        }

        public bool GuardUp() => Guarding && _guardT >= Consts.GUARD_RAISE_FRAMES;
        /// <summary>Guarding through an attack's own window: a blocked hit doesn't stop it.</summary>
        public bool AttackGuard()
        {
            if (State != StateKind.Act)
            {
                return false;
            }
            ActionDef def = Act.Id.Def();
            return Act.F >= def.GuardFrom && Act.F < def.GuardTo;
        }

        public bool GuardCounterReady() => _guardCounter > 0f;
        public float GuardT => _guardT;
        public float DodgeHold => _dodgeHold;

        public ActiveHit ActiveHitNow()
        {
            if (AirAttackState is AirAttack attack)
            {
                AirAttackDef def = attack.Def;
                bool live = (LandFollowUps || !attack.HitDone) && attack.F >= def.From && attack.F < def.To;
                Hit hit = attack.Moveset.Attack(LandingOf(attack))?.FirstHit;
                if (hit == null)
                {
                    return null;
                }
                var ah = new ActiveHit { Attack = attack.Moveset.Info.Attack, Mv = hit.Mv, GuardDamage = hit.GuardDamage, Stop = hit.Stop, Radius = def.Radius };
                if (!Sweep(def.Blade, def.From, attack.F, Consts.DF * def.SpeedAt(attack.F), ah))
                {
                    return null;
                }
                return live ? ah : null;
            }
            if (State != StateKind.Act || !Act.Id.IsAttack)
            {
                return null;
            }
            int i = LiveHit(Act, out Hit h);
            if (i < 0)
            {
                return null;
            }
            var result = new ActiveHit { Attack = Act.Id.Moveset.Info.Attack, Mv = h.Mv, GuardDamage = h.GuardDamage, Stop = h.Stop, Radius = h.Radius };
            return Sweep(h.Blade, h.From, Act.F, Consts.DF * Act.Id.Def().SpeedAt(Act.F), result) ? result : null;
        }

        /// <summary>
        /// Eldenheim: hit i of the current attack while its window is open,
        /// landed or not (a host with many targets keeps sweeping the whole
        /// window and remembers whom it struck itself).
        /// </summary>
        public ActiveHit WindowHit(int i)
        {
            if (State != StateKind.Act || !Act.Id.IsAttack)
            {
                return null;
            }
            Hit[] hits = Act.Id.Def().Hits;
            // Open for the tick that crosses the window's end too, swept up to that end, so its last frames strike.
            float step = Consts.DF * Act.Id.Def().SpeedAt(Act.F);
            if (i < 0 || i >= hits.Length || Act.F < hits[i].From || Act.F - step >= hits[i].To)
            {
                return null;
            }
            Hit h = hits[i];
            var result = new ActiveHit { Attack = Act.Id.Moveset.Info.Attack, Mv = h.Mv, GuardDamage = h.GuardDamage, Stop = h.Stop, Radius = h.Radius };
            return Sweep(h.Blade, h.From, F.Min(Act.F, h.To), step, result) ? result : null;
        }

        /// <summary>The blade's path over the tick that ended at frame, which advanced the animation by step frames.</summary>
        private bool Sweep(float[][] blade, float from, float frame, float step, ActiveHit into)
        {
            Vec3 forward = Facing(), left = LeftDir();
            float start = F.Max(frame - step, from);
            for (int i = 0; i < SWEEP_STEPS; i++)
            {
                float at = start + (frame - start) * i / (SWEEP_STEPS - 1);
                if (!ActionDef.BladeAt(blade, from, at, out Vec3 a, out Vec3 b))
                {
                    return false;
                }
                if (AimPitch != 0f)
                {
                    a = Pitched(a);
                    b = Pitched(b);
                }
                into.SweepA[i] = Pos + left * a.X + Vec3.UnitY * a.Y + forward * a.Z;
                into.SweepB[i] = Pos + left * b.X + Vec3.UnitY * b.Y + forward * b.Z;
            }
            return true;
        }

        /// <summary>A blade point (left, up, forward from the feet) tilted by AimPitch about the hips.</summary>
        private Vec3 Pitched(Vec3 v)
        {
            float c = F.Cos(AimPitch), sn = F.Sin(AimPitch), y = v.Y - AIM_PIVOT;
            return new Vec3(v.X, AIM_PIVOT + y * c + v.Z * sn, v.Z * c - y * sn);
        }

        /// <summary>Index of the hit whose window is open and has not connected yet, or -1.</summary>
        public static int LiveHit(Act a, out Hit hit)
        {
            Hit[] hits = a.Id.Def().Hits;
            for (int i = 0; i < hits.Length; i++)
            {
                if (a.F >= hits[i].From && a.F < hits[i].To && (a.Landed & (1u << i)) == 0)
                {
                    hit = hits[i];
                    return i;
                }
            }
            hit = null;
            return -1;
        }

        /// <summary>The hits of a jump attack's landing that start within its first hit (the air swing): as a mask.</summary>
        public static uint SwingHits(ActionDef landing)
        {
            Hit first = landing.FirstHit;
            uint mask = 0;
            for (int i = 0; i < landing.Hits.Length && i < 32; i++)
            {
                if (first != null && landing.Hits[i].From < first.To)
                {
                    mask |= 1u << i;
                }
            }
            return mask;
        }

        /// <summary>A jump attack's landing (it carries on the air swing).</summary>
        public static bool IsJumpLanding(AttackKind k) =>
            k == AttackKind.JumpLightLand || k == AttackKind.JumpLightLandShort || k == AttackKind.JumpHeavyLand || k == AttackKind.JumpHeavyLandShort
            || k == AttackKind.PairedJumpLand || k == AttackKind.PairedJumpLandShort;

        /// <summary>The landing attack an air attack ends in.</summary>
        public static AttackKind LandingOf(AirAttack attack) =>
            attack.Paired ? AttackKind.PairedJumpLand : attack.Heavy ? AttackKind.JumpHeavyLand : AttackKind.JumpLightLand;

        public void MarkHit()
        {
            if (AirAttackState is AirAttack attack)
            {
                attack.HitDone = true;
                AirAttackState = attack;
            }
            else if (State == StateKind.Act)
            {
                int i = LiveHit(Act, out _);
                if (i >= 0)
                {
                    Act.Landed |= 1u << i;
                }
            }
        }

        public void Step(Input inp, ILevel level, Vec3? target, bool inCombat)
        {
            InCombat = inCombat;
            if (State == StateKind.Dead)
            {
                DeadT += Consts.DF;
                if (DeadT >= RESPAWN_FRAMES)
                {
                    Load load = Load;
                    Reset(Spawn);
                    Load = load;
                }
                return;
            }

            ReadButtons(inp);
            if (HitStop > 0f)
            {
                HitStop -= Consts.DT;
                return;
            }
            _guardCounter = F.Max(_guardCounter - Consts.DF, 0f);
            if (AirAttackState is AirAttack air)
            {
                air.F += Consts.DF * air.Def.SpeedAt(air.F);
                AirAttackState = air;
            }
            if (SwapState is Swap swap)
            {
                SwapDef def = ErData.Swap(swap.Kind);
                swap.F += Consts.DF;
                if (!swap.Applied && swap.F >= def.Apply)
                {
                    Grip = swap.Grip;
                    Weapon = swap.Weapon;
                    Left = swap.Left;
                    swap.Applied = true;
                }
                SwapState = swap.F < def.Total ? swap : (Swap?)null;
            }

            bool regen;
            switch (State)
            {
                case StateKind.Ground:
                    regen = Ground(inp, level, target);
                    break;
                case StateKind.Act:
                    regen = DoAct(Act, inp, level, target);
                    break;
                case StateKind.Air:
                    DoAir(Air, inp, level, target.HasValue);
                    regen = false;
                    break;
                default:
                    regen = false;
                    break;
            }

            Regenerating = regen;
            if (regen)
            {
                float mult = Guarding ? Consts.GUARD_REGEN_MULT : 1f;
                Stamina = F.Min(Stamina + Consts.STAMINA_REGEN * mult * Consts.DT, ErData.MAX_STAMINA);
            }
        }

        private void ReadButtons(Input inp)
        {
            bool listening = true, listeningDodge = true;
            if (State == StateKind.Act && Act.Id.Tag != ActionTag.Jump)
            {
                ActionDef def = Act.Id.Def();
                bool rolling = EarlyRollAttacks && (Act.Id.IsRollLike || Act.Id.Tag == ActionTag.Backstep);
                listening = Act.F >= def.InputFrom || rolling;
                listeningDodge = Act.F >= def.InputDodgeFrom;
            }
            if (QueueCrouch && inp.Crouch && State != StateKind.Ground)
            {
                _crouchQueued = !_crouchQueued;
            }
            bool inAir = State == StateKind.Air || State == StateKind.Act && Act.Id.Tag == ActionTag.Jump;
            _sinceHeavyUp = inp.Heavy.Released ? 0f : _sinceHeavyUp + Consts.DF;
            bool heavyPressed = inp.Heavy.Pressed && !(HeavyDebounce > 0f && _sinceHeavyUp <= HeavyDebounce);

            if (inp.Dodge.Pressed)
            {
                // A separate sprint button (Eldenheim) sprints at once; a tap (down and up together) still dodges.
                _dodgeHold = InstantSprint && !inp.Dodge.Released ? Consts.SPRINT_HOLD_FRAMES : 0f;
                _dodgeArmed = true;
            }
            if (inp.Dodge.Held)
            {
                _dodgeHold += Consts.DF;
            }
            if (inp.Dodge.Released)
            {
                if (_dodgeArmed && _dodgeHold < Consts.SPRINT_HOLD_FRAMES && listeningDodge)
                {
                    Buffer = Req.Dodge;
                }
                _dodgeArmed = false;
                _dodgeHold = 0f;
                _sprintSpent = false;
            }
            bool attacksHeard = !(NoAirAttackBuffer && AirAttackState.HasValue);
            if (listening)
            {
                if (inp.Jump.Pressed && !(NoAirJumpBuffer && inAir))
                {
                    Buffer = Req.Jump;
                }
                if (heavyPressed && attacksHeard)
                {
                    Buffer = Req.Heavy;
                }
                if (inp.Guard.Pressed && LeftButtonDoes() == LeftButton.Attack && attacksHeard)
                {
                    Buffer = Req.Left;
                }
                if (inp.Light.Pressed && attacksHeard)
                {
                    Buffer = PairedOnLight && Paired() ? Req.Left : Req.Light;
                }
            }
        }

        public bool SwapBusy()
        {
            if (!(SwapState is Swap swap))
            {
                return false;
            }
            SwapDef def = ErData.Swap(swap.Kind);
            return swap.F < def.StartLen + def.FreeFrom;
        }

        private bool SprintHeld(Input inp) => inp.Dodge.Held && _dodgeHold >= Consts.SPRINT_HOLD_FRAMES;

        private bool Ground(Input inp, ILevel level, Vec3? target)
        {
            bool hasWish = inp.Wish(out Vec3 wish);
            if (_crouchQueued)
            {
                // A crouch pressed during the last action.
                _crouchQueued = false;
                Crouching = !Crouching;
            }

            if (SwapBusy())
            {
            }
            else if (Buffer is Req req)
            {
                Buffer = null;
                if (Stamina > 0f)
                {
                    ActionId? attack;
                    switch (req)
                    {
                        case Req.Dodge:
                            StartDodge(hasWish ? wish : (Vec3?)null, target);
                            return false;
                        case Req.Jump:
                            StartJump(inp, target);
                            return false;
                        case Req.Left when Sprinting:
                            attack = PickLeft(new[] { AttackKind.PairedRun, AttackKind.PairedLight1 }, new[] { AttackKind.LeftLight1 });
                            break;
                        case Req.Left when Crouching && PairedCrouchAttack:
                            // ER's power-stance crouch attack is its rolling attack.
                            attack = PickLeft(new[] { AttackKind.PairedRoll, AttackKind.PairedLight1 }, new[] { AttackKind.LeftLight1 });
                            break;
                        case Req.Left:
                            attack = PickLeft(new[] { AttackKind.PairedLight1 }, new[] { AttackKind.LeftLight1 });
                            break;
                        case Req.Light when Sprinting:
                            attack = Pick(AttackKind.RunLight, AttackKind.Light1);
                            break;
                        case Req.Light when Crouching:
                            attack = Pick(AttackKind.CrouchAttack, AttackKind.RollAttack, AttackKind.Light1);
                            break;
                        case Req.Light:
                            attack = Pick(AttackKind.Light1);
                            break;
                        case Req.Heavy when Sprinting:
                            attack = Pick(AttackKind.RunHeavy, AttackKind.Heavy1Charge);
                            break;
                        case Req.Heavy when GuardCounterReady():
                            attack = Pick(AttackKind.GuardCounter, AttackKind.Heavy1Charge);
                            break;
                        default:
                            attack = Pick(AttackKind.Heavy1Charge, AttackKind.Heavy1);
                            break;
                    }
                    if (attack is ActionId id)
                    {
                        Start(id);
                        return false;
                    }
                }
            }

            if (SwapState == null)
            {
                SwapKind back = Grip == Grip.TwoHandLeft ? SwapKind.ToOneHandFromLeft : SwapKind.ToOneHandFromRight;
                bool change = true;
                SwapKind kind = default;
                Grip grip = Grip;
                int weapon = Weapon, left = Left;
                if (inp.TwoHandRight)
                {
                    if (Grip == Grip.TwoHandRight)
                    {
                        kind = back;
                        grip = Grip.OneHand;
                    }
                    else
                    {
                        kind = SwapKind.ToTwoHandRight;
                        grip = Grip.TwoHandRight;
                    }
                }
                else if (inp.TwoHandLeft)
                {
                    if (Grip == Grip.TwoHandLeft)
                    {
                        kind = back;
                        grip = Grip.OneHand;
                    }
                    else
                    {
                        kind = SwapKind.ToTwoHandLeft;
                        grip = Grip.TwoHandLeft;
                    }
                }
                else if (inp.NextWeapon)
                {
                    kind = SwapKind.NextWeapon;
                    weapon = (Weapon + 1) % ErData.SHIELD;
                }
                else if (inp.NextLeft && Grip == Grip.OneHand)
                {
                    kind = SwapKind.NextLeft;
                    left = NextLeftHand(Left);
                }
                else
                {
                    change = false;
                }
                if (change)
                {
                    SwapState = new Swap { Kind = kind, F = 0f, Grip = grip, Weapon = weapon, Left = left, Applied = false };
                }
            }

            if (inp.Crouch)
            {
                Crouching = !Crouching;
            }

            bool wasSprinting = Sprinting;
            Sprinting = SprintHeld(inp) && hasWish && !_sprintSpent && Stamina > 0f;
            if (Sprinting)
            {
                Crouching = false;
                if (InCombat)
                {
                    Stamina -= Consts.SPRINT_DRAIN * Consts.DT;
                    if (Stamina <= 0f)
                    {
                        Stamina = 0f;
                        Sprinting = false;
                        _sprintSpent = true;
                    }
                }
            }
            // Eldenheim: guarding at a sprint never brakes, so reversing (a tick with no direction between the keys) doesn't pivot.
            if (wasSprinting && !hasWish && Speed > ErData.RUN_SPEED + 0.5f && !(GuardWhileSprinting && inp.Guard.Held))
            {
                Start(ActionId.SprintStop);
                return true;
            }

            if (inp.Guard.Held && (!Sprinting || GuardWhileSprinting) && LeftButtonDoes() == LeftButton.Guard)
            {
                Guarding = true;
                _guardT += Consts.DF;
            }
            else
            {
                Guarding = false;
                _guardT = 0f;
            }

            bool strafing = target.HasValue && !Sprinting;
            bool slow = inp.Walk || inp.Tilt() < Consts.WALK_TILT;
            float targetSpeed;
            if (!hasWish)
            {
                targetSpeed = 0f;
            }
            else if (Sprinting)
            {
                targetSpeed = ErData.SPRINT_SPEED * SprintScale;
            }
            else if (Crouching && slow)
            {
                targetSpeed = ErData.CROUCH_WALK_SPEED;
            }
            else if (Crouching)
            {
                targetSpeed = ErData.CROUCH_RUN_SPEED;
            }
            else if (slow)
            {
                targetSpeed = ErData.WALK_SPEED;
            }
            else if (strafing)
            {
                float along = wish.Dot(Facing());
                float c = F.Cos(F.FRAC_PI_4);
                targetSpeed = along > c ? ErData.RUN_SPEED : along < -c ? ErData.RUN_BACK_SPEED : ErData.RUN_SIDE_SPEED;
            }
            else
            {
                targetSpeed = ErData.RUN_SPEED;
            }
            if (!Sprinting)
            {
                targetSpeed *= MoveScale;
            }
            float rate = targetSpeed > Speed ? Consts.ACCEL : Consts.DECEL;
            Speed = SimMath.Approach(Speed, targetSpeed, rate * Consts.DT);

            if (target is Vec3 t && strafing)
            {
                float? goal = YawTo(t);
                if (goal.HasValue)
                {
                    Yaw = SimMath.TurnToward(Yaw, goal.Value, F.ToRadians(Consts.TURN_LOCKED) * Consts.DT);
                }
                if (hasWish)
                {
                    MoveDir = wish;
                }
            }
            else if (hasWish)
            {
                float turn = Sprinting ? TurnSprint : TurnRun;
                // Eldenheim: guarding while sprinting (GuardWhileSprinting) turns on a dime, no pivot.
                Yaw = Sprinting && Guarding ? SimMath.YawOf(wish) : SimMath.TurnToward(Yaw, SimMath.YawOf(wish), F.ToRadians(turn) * Consts.DT);
                MoveDir = MoveAlongWish ? wish : Facing();
            }

            level.Slide(ref Pos, MoveDir * Speed * Consts.DT);
            if (!FollowGround(level, MoveDir * Speed))
            {
                return false;
            }
            return !Sprinting;
        }

        private bool DoAct(Act a, Input inp, ILevel level, Vec3? target)
        {
            ActionDef def = a.Id.Def();
            bool hasWish = inp.Wish(out Vec3 wish);
            float prev = a.F;

            // A dodge in the charge window, for the classes that have one, feints.
            if (def.HasCharge && a.F >= def.ChargeFrom && a.F < def.ChargeTo && inp.Dodge.Pressed && a.Id.IsAttack
                && (a.Id.Kind == AttackKind.Heavy1Charge || a.Id.Kind == AttackKind.Heavy2Charge))
            {
                AttackKind feint = a.Id.Kind == AttackKind.Heavy2Charge ? AttackKind.Feint2 : AttackKind.Feint1;
                if (a.Id.Moveset.Has(feint))
                {
                    Start(ActionId.Attack(a.Id.Moveset, feint));
                    // The press was the feint: its release is no dodge too.
                    _dodgeArmed = false;
                    return false;
                }
            }
            if (def.HasCharge && a.F >= def.ChargeFrom && a.F < def.ChargeTo && !inp.Heavy.Held && a.Id.IsAttack)
            {
                AttackKind release = a.Id.Kind == AttackKind.Heavy2Charge ? AttackKind.Heavy2 : AttackKind.Heavy1;
                Moveset moveset = a.Id.Moveset;
                if (moveset.Has(release))
                {
                    Start(ActionId.Attack(moveset, release));
                    return false;
                }
            }
            // The game's speed windows (TAE 608): faster through the animation's own frames.
            a.F += Consts.DF * def.SpeedAt(a.F);

            for (int i = 0; i < def.Hits.Length; i++)
            {
                if ((a.Paid & (1u << i)) == 0 && a.F >= def.Hits[i].From)
                {
                    Spend(def.Hits[i].Stamina);
                    a.Paid |= 1u << i;
                }
            }

            bool fixedFacing = target.HasValue && (a.Id.IsRollLike || a.Id.Tag == ActionTag.Backstep);
            if (a.Id.Tag == ActionTag.Jump && a.F <= JumpTurnFrames && hasWish && !target.HasValue)
            {
                Yaw = SimMath.YawOf(wish);
            }
            if (def.CanTurn(a.F) && !fixedFacing)
            {
                float? goal = target.HasValue ? YawTo(target.Value) : hasWish ? SimMath.YawOf(wish) : (float?)null;
                if (goal.HasValue)
                {
                    Yaw = SimMath.TurnToward(Yaw, goal.Value, F.ToRadians(def.TurnRate(a.F)) * Consts.DT);
                }
            }

            if (a.Id.Tag == ActionTag.Backstep && a.F <= ReverseBackstepFrames && hasWish && !target.HasValue)
            {
                Yaw = SimMath.YawOf(wish);
            }
            if (BackstepAttackTurn > 0f && a.Id.IsAttack && a.Id.Kind == AttackKind.BackstepAttack && hasWish && !target.HasValue
                && a.F < (def.FirstHit?.From ?? 0f))
            {
                Yaw = SimMath.TurnToward(Yaw, SimMath.YawOf(wish), F.ToRadians(BackstepAttackTurn) * Consts.DT);
            }

            def.MotionAt(prev, out float l0, out _, out float f0);
            def.MotionAt(a.F, out float l1, out _, out float f1);
            Vec3 step = Facing() * (f1 - f0) + LeftDir() * (l1 - l0);
            level.Slide(ref Pos, step);

            if (a.Id.Tag == ActionTag.Jump)
            {
                if (AirSpeed > 0f && hasWish && def.UpAt(a.F) > 0f)
                {
                    // A little steering while off the ground.
                    level.Slide(ref Pos, wish * F.Min(AirSpeed, AirAccel * 0.5f) * Consts.DT);
                }
                DoJump(a, def, step, inp, level, target.HasValue);
                return false;
            }
            if (!FollowGround(level, (step / Consts.DT).ClampLengthMax(ErData.SPRINT_SPEED)))
            {
                ResumeAirAttack(a);
                return false;
            }

            if (Buffer is Req req)
            {
                float opens;
                switch (req)
                {
                    case Req.Light: opens = def.CancelLight; break;
                    case Req.Heavy: opens = def.CancelHeavy; break;
                    case Req.Left: opens = def.CancelLeft; break;
                    case Req.Dodge: opens = def.CancelDodge; break;
                    default: opens = def.CancelJump; break;
                }
                if (a.F >= opens)
                {
                    Buffer = null;
                    if (Stamina > 0f)
                    {
                        ActionId? next;
                        switch (req)
                        {
                            case Req.Dodge:
                                StartDodge(hasWish ? wish : (Vec3?)null, target);
                                return false;
                            case Req.Jump:
                                StartJump(inp, target);
                                return false;
                            case Req.Light: next = NextLight(a.Id, a.F); break;
                            case Req.Heavy: next = NextHeavy(a.Id, a.F); break;
                            default: next = NextLeft(a.Id, a.F); break;
                        }
                        if (next is ActionId n)
                        {
                            Start(n);
                            return false;
                        }
                    }
                }
            }

            bool regen = a.F >= def.CancelMove;
            if (inp.Guard.Held && a.F >= def.CancelGuard && LeftButtonDoes() == LeftButton.Guard)
            {
                State = StateKind.Ground;
                Speed = 0f;
            }
            else if (hasWish && a.F >= def.CancelMove)
            {
                State = StateKind.Ground;
                MoveDir = wish;
                Speed = a.Id == ActionId.LandSprint && !SprintHeld(inp) ? ErData.RUN_SPEED : a.Id.ExitSpeed();
            }
            else if (a.F >= def.Total)
            {
                State = StateKind.Ground;
                Speed = 0f;
            }
            else
            {
                State = StateKind.Act;
                Act = a;
            }
            return regen;
        }

        private void DoJump(Act a, ActionDef def, Vec3 step, Input inp, ILevel level, bool locked)
        {
            float up = def.UpAt(a.F);
            float ground = level.Height(Pos.X, Pos.Z);
            TryAirAttack(a.F >= def.CancelLight);

            if (up <= 0f)
            {
                if (Pos.Y - ground <= Consts.STEP_HEIGHT)
                {
                    Pos.Y = ground;
                }
                _jumpBase = Pos.Y;
                _peak = Pos.Y;
                State = StateKind.Act;
                Act = a;
                return;
            }
            float y = _jumpBase + up * JumpScale;
            _peak = F.Max(_peak, y);
            // Eldenheim: still rising, a ledge or rock met on the way up doesn't end the jump (as in the air).
            if (y <= ground && y <= Pos.Y)
            {
                Land(ground, inp, true, locked);
                return;
            }
            Pos.Y = y;
            if (a.F < def.Total)
            {
                State = StateKind.Act;
                Act = a;
                return;
            }
            int n = def.Motion.Length;
            // Speed goes with the root of the height: the climb after the clip grows by JumpScale too.
            float rise = (def.Motion[n - 1][1] - def.Motion[n - 2][1]) * Consts.ANIM_FPS * F.Sqrt(JumpScale);
            Vec3 flat = step / Consts.DT;
            State = StateKind.Air;
            Air = new Air { Vel = new Vec3(flat.X, rise, flat.Z), Jumped = true, F = 0f };
        }

        private void DoAir(Air a, Input inp, ILevel level, bool locked)
        {
            a.F += Consts.DF;
            a.Vel.Y = F.Max(a.Vel.Y - Consts.GRAVITY * Consts.DT, -Consts.TERMINAL_VELOCITY);
            if (MaxFallSpeed > 0f && a.Vel.Y < -MaxFallSpeed)
            {
                // Slow fall: the drop so far doesn't count towards the landing.
                a.Vel.Y = -MaxFallSpeed;
                _peak = Pos.Y;
            }
            if (AirAccel > 0f && inp.Wish(out Vec3 steer))
            {
                var flat = new Vec3(a.Vel.X, 0f, a.Vel.Z);
                Vec3 goal = steer * F.Max(AirSpeed, F.Min(flat.Length(), ErData.SPRINT_SPEED));
                Vec3 change = (goal - flat).ClampLengthMax(AirAccel * Consts.DT);
                a.Vel.X += change.X;
                a.Vel.Z += change.Z;
                float yaw = SimMath.YawOf(steer);
                Yaw = SimMath.TurnToward(Yaw, yaw, F.ToRadians(Consts.TURN_RUN) * Consts.DT);
            }
            level.Slide(ref Pos, new Vec3(a.Vel.X, 0f, a.Vel.Z) * Consts.DT);
            Pos.Y += a.Vel.Y * Consts.DT;
            _peak = F.Max(_peak, Pos.Y);
            TryAirAttack(a.Jumped);

            float ground = level.Height(Pos.X, Pos.Z);
            if (a.Vel.Y > 0f || Pos.Y > ground)
            {
                State = StateKind.Air;
                Air = a;
            }
            else
            {
                Land(ground, inp, a.Jumped && !a.Falling, locked, a.Thrown);
            }
        }

        /// <summary>ResumeAirAttacks: a jump attack's landing gone over an edge mid-swing is its air attack again, at the same frame.</summary>
        private void ResumeAirAttack(Act a)
        {
            AttackKind k = a.Id.IsAttack ? a.Id.Kind : AttackKind.Light1;
            bool heavy = k == AttackKind.JumpHeavyLand, paired = k == AttackKind.PairedJumpLand;
            if (!ResumeAirAttacks || !(heavy || paired || k == AttackKind.JumpLightLand))
            {
                return;
            }
            Moveset m = a.Id.Moveset;
            AirAttackDef air = paired ? m.AirPaired() : m.Air(heavy);
            // Past the swing too: it then lands in its short landing, not a plain fall's.
            if (air != null)
            {
                AirAttackState = new AirAttack { Heavy = heavy, Paired = paired, Moveset = m, Def = air, F = a.F, HitDone = a.Landed != 0, Resumed = true };
            }
        }

        private void TryAirAttack(bool allowed)
        {
            if (!allowed || AirAttackState.HasValue || Stamina <= 0f)
            {
                return;
            }
            bool heavy, paired;
            if (Buffer == Req.Light)
            {
                heavy = false;
                paired = false;
            }
            else if (Buffer == Req.Heavy)
            {
                heavy = true;
                paired = false;
            }
            else if (Buffer == Req.Left && Paired())
            {
                heavy = false;
                paired = true;
            }
            else
            {
                return;
            }
            Buffer = null;
            Moveset moveset = Moveset();
            AirAttackDef def = paired ? moveset.AirPaired() : moveset.Air(heavy);
            if (def == null || CanAffordAir != null && !CanAffordAir(moveset, def))
            {
                return;
            }
            Spend(def.Stamina);
            AirAttackState = new AirAttack { Heavy = heavy, Paired = paired, Moveset = moveset, Def = def, F = 0f, HitDone = false };
        }

        private void Land(float ground, Input inp, bool jumped, bool locked, bool thrown = false)
        {
            Pos.Y = ground;
            float fall = _peak - ground;
            AirAttack? attack = AirAttackState;
            AirAttackState = null;
            if (fall >= Consts.FALL_DEATH && !ExternalFallDamage)
            {
                Die();
                return;
            }
            if (fall >= Consts.FALL_DAMAGE_START && !ExternalFallDamage)
            {
                float t = (fall - Consts.FALL_DAMAGE_START) / (Consts.FALL_DEATH - Consts.FALL_DAMAGE_START);
                Hp -= ErData.MAX_HP * (0.3f + 0.6f * t);
                if (Hp <= 0f)
                {
                    Die();
                    return;
                }
            }

            if (attack is AirAttack at)
            {
                AttackKind landing, shortKind;
                if (at.Paired)
                {
                    landing = AttackKind.PairedJumpLand;
                    shortKind = AttackKind.PairedJumpLandShort;
                }
                else if (at.Heavy)
                {
                    landing = AttackKind.JumpHeavyLand;
                    shortKind = AttackKind.JumpHeavyLandShort;
                }
                else
                {
                    landing = AttackKind.JumpLightLand;
                    shortKind = AttackKind.JumpLightLandShort;
                }
                bool finished = at.F >= at.Def.To;
                AttackKind kind = finished && at.Moveset.Has(shortKind) ? shortKind : landing;
                ActionDef def = at.Moveset.Attack(kind);
                if (def == null)
                {
                    Start(ActionId.LandLight);
                    return;
                }
                Start(ActionId.Attack(at.Moveset, kind));
                if (State == StateKind.Act)
                {
                    Act.F = finished ? 0f : LandInPlace ? at.F : F.Min(at.F, def.FirstHit?.From ?? 0f);
                    Act.Paid = uint.MaxValue;
                    Act.Landed = finished ? uint.MaxValue : at.HitDone ? (LandFollowUps ? SwingHits(def) : uint.MaxValue) : 0u;
                }
            }
            else if (fall >= HeavyLandingFall || thrown && fall >= MinLandingFall)
            {
                Start(ActionId.LandHeavy);
            }
            else if (!jumped && fall < MinLandingFall)
            {
                // A step or a small drop: walk on.
                State = StateKind.Ground;
            }
            else if (!jumped && fall >= StopLandingFall)
            {
                Start(ActionId.LandFall);
            }
            else if (inp.Wish(out Vec3 w))
            {
                if (locked && !SprintHeld(inp))
                {
                    Dir side = SquareUp(SimMath.YawOf(w), true);
                    bool walking = inp.Walk || inp.Tilt() < Consts.WALK_TILT;
                    Start(walking ? ActionId.LandStrafeWalk(side) : ActionId.LandStrafe(side));
                    return;
                }
                Yaw = SimMath.YawOf(w);
                Start(SprintHeld(inp) ? ActionId.LandSprint : LightLandings ? ActionId.LandLight : ActionId.LandRun);
            }
            else
            {
                Start(ActionId.LandLight);
            }
        }

        private bool FollowGround(ILevel level, Vec3 carry)
        {
            float ground = level.Height(Pos.X, Pos.Z);
            if (Pos.Y - ground > Consts.STEP_HEIGHT && Pos.Y - ground > StepDown)
            {
                Sprinting = false;
                Guarding = false;
                Crouching = false;
                _peak = Pos.Y;
                bool thrown = State == StateKind.Act && (Act.Id.Tag == ActionTag.Hurt || Act.Id.Tag == ActionTag.GuardBlow || Act.Id.Tag == ActionTag.GuardBreak);
                State = StateKind.Air;
                Air = new Air { Vel = new Vec3(carry.X, 0f, carry.Z), Jumped = false, F = 0f, Thrown = thrown };
                return false;
            }
            Pos.Y = ground;
            return true;
        }

        private float? YawTo(Vec3 point)
        {
            var to = new Vec3(point.X - Pos.X, 0f, point.Z - Pos.Z);
            return to.LengthSquared() > 1e-4f ? SimMath.YawOf(to) : (float?)null;
        }

        private void Spend(float cost)
        {
            Stamina = F.Max(Stamina - cost, 0f);
        }

        private void Start(ActionId id)
        {
            if (id.IsAttack && CanAfford != null && !CanAfford(id))
            {
                // The host's stamina can't pay for it: nothing starts.
                Buffer = null;
                return;
            }
            Spend(id.StartCost());
            State = StateKind.Act;
            Act = new Act { Id = id, F = 0f, Landed = 0, Paid = 0 };
            Speed = 0f;
            Sprinting = false;
            Crouching &= id.Tag == ActionTag.CrouchRoll;
            Guarding = false;
            if (id != ActionId.GuardHit)
            {
                _guardT = 0f;
            }
        }

        private void StartDodge(Vec3? wish, Vec3? target)
        {
            if (!(wish is Vec3 dir))
            {
                Start(ActionId.Backstep);
                return;
            }
            Dir side = SquareUp(SimMath.YawOf(dir), target.HasValue);
            Start(Crouching ? ActionId.CrouchRoll(Load, side) : ActionId.Roll(Load, side));
        }

        private Dir SquareUp(float goal, bool locked)
        {
            float off = SimMath.AngleDiff(Yaw, goal);
            Dir side;
            if (!locked || F.Abs(off) <= F.FRAC_PI_4)
            {
                side = Dir.Front;
            }
            else if (F.Abs(off) >= F.PI - F.FRAC_PI_4)
            {
                side = Dir.Back;
            }
            else
            {
                side = off > 0f ? Dir.Left : Dir.Right;
            }
            switch (side)
            {
                case Dir.Front: Yaw = goal; break;
                case Dir.Back: Yaw = goal + F.PI; break;
                case Dir.Left: Yaw = goal - F.FRAC_PI_2; break;
                default: Yaw = goal + F.FRAC_PI_2; break;
            }
            return side;
        }

        private void StartJump(Input inp, Vec3? target)
        {
            JumpKind kind;
            if (!inp.Wish(out Vec3 dir))
            {
                kind = JumpKind.Stand;
            }
            else if (Sprinting)
            {
                Yaw = SimMath.YawOf(dir);
                kind = JumpKind.Sprint;
            }
            else
            {
                Dir side = SquareUp(SimMath.YawOf(dir), target.HasValue);
                bool walking = inp.Walk || inp.Tilt() < Consts.WALK_TILT;
                switch (side)
                {
                    case Dir.Front: kind = walking ? JumpKind.Walk : JumpKind.Run; break;
                    case Dir.Back: kind = walking ? JumpKind.WalkBack : JumpKind.RunBack; break;
                    case Dir.Left: kind = walking ? JumpKind.WalkLeft : JumpKind.RunLeft; break;
                    default: kind = walking ? JumpKind.WalkRight : JumpKind.RunRight; break;
                }
            }
            _jumpBase = Pos.Y;
            _peak = Pos.Y;
            AirAttackState = null;
            Start(ActionId.Jump(kind));
            if (InstantJump && State == StateKind.Act)
            {
                ActionDef def = Act.Id.Def();
                float f = 0f;
                while (f < def.Total && def.UpAt(f + 1f) <= 0f)
                {
                    f += 1f;
                }
                Act.F = f;
            }
        }

        private ActionId? NextLight(ActionId from, float f)
        {
            // The attack's own say first (Milady's chains).
            int swing = from.Def().LightAt(f);
            if (swing != 0)
            {
                return Pick((AttackKind)((int)AttackKind.Light1 + swing - 1), AttackKind.Light1);
            }
            if (from.IsAttack)
            {
                AttackKind k = from.Kind;
                if (k == AttackKind.RunLight || k == AttackKind.RollAttack || k == AttackKind.BackstepAttack || k == AttackKind.CrouchAttack
                    || k == AttackKind.RunHeavy || k == AttackKind.Heavy1 || k == AttackKind.Heavy1Charge || k == AttackKind.GuardCounter)
                {
                    return Pick(AttackKind.Light2, AttackKind.Light1);
                }
                // A feint goes on as a backstep's attack.
                if (k == AttackKind.Feint1 || k == AttackKind.Feint2)
                {
                    return Pick(AttackKind.BackstepAttack, AttackKind.Light1);
                }
                if (JumpAttackChains && (k == AttackKind.JumpLightLand || k == AttackKind.JumpLightLandShort))
                {
                    return Pick(AttackKind.Light2, AttackKind.Light1);
                }
                AttackKind next = NextInChain(k, AttackKind.Light1, AttackKind.Light6) ?? AttackKind.Light1;
                return Pick(next, AttackKind.Light1);
            }
            switch (from.Tag)
            {
                case ActionTag.Roll:
                case ActionTag.CrouchRoll:
                    return Pick(AttackKind.RollAttack, AttackKind.Light1);
                case ActionTag.Backstep:
                    return Pick(AttackKind.BackstepAttack, AttackKind.Light1);
                case ActionTag.SprintStop:
                    return Pick(AttackKind.RunLight, AttackKind.Light1);
                default:
                    return Pick(AttackKind.Light1);
            }
        }

        /// <summary>The kind after k in a chain first..last, if k is one of its first five.</summary>
        private static AttackKind? NextInChain(AttackKind k, AttackKind first, AttackKind last) =>
            k >= first && k < last ? k + 1 : (AttackKind?)null;

        private ActionId? NextLeft(ActionId from, float f)
        {
            AttackKind paired, single;
            int swing = from.Def().LightAt(f);
            if (swing != 0)
            {
                paired = (AttackKind)((int)AttackKind.PairedLight1 + swing - 1);
                single = AttackKind.LeftLight1;
            }
            else if (from.IsAttack && JumpAttackChains && (from.Kind == AttackKind.PairedJumpLand || from.Kind == AttackKind.PairedJumpLandShort))
            {
                paired = AttackKind.PairedLight2;
                single = AttackKind.LeftLight1;
            }
            else if (from.IsAttack)
            {
                paired = NextInChain(from.Kind, AttackKind.PairedLight1, AttackKind.PairedLight6) ?? AttackKind.PairedLight1;
                single = NextInChain(from.Kind, AttackKind.LeftLight1, AttackKind.LeftLight6) ?? AttackKind.LeftLight1;
            }
            else
            {
                switch (from.Tag)
                {
                    case ActionTag.Roll:
                    case ActionTag.CrouchRoll:
                        paired = AttackKind.PairedRoll;
                        break;
                    case ActionTag.Backstep:
                        paired = AttackKind.PairedBackstep;
                        break;
                    case ActionTag.SprintStop:
                        paired = AttackKind.PairedRun;
                        break;
                    default:
                        paired = AttackKind.PairedLight1;
                        break;
                }
                single = AttackKind.LeftLight1;
            }
            return PickLeft(new[] { paired, AttackKind.PairedLight1 }, new[] { single, AttackKind.LeftLight1 });
        }

        private ActionId? NextHeavy(ActionId from, float f)
        {
            if (GuardCounterReady())
            {
                return Pick(AttackKind.GuardCounter, AttackKind.Heavy1Charge);
            }
            if (from.Def().Heavy2At(f) || from.IsAttack && (from.Kind == AttackKind.Heavy1 || from.Kind == AttackKind.Heavy1Charge))
            {
                return Pick(AttackKind.Heavy2Charge, AttackKind.Heavy1Charge);
            }
            if (from == ActionId.SprintStop)
            {
                return Pick(AttackKind.RunHeavy, AttackKind.Heavy1Charge);
            }
            return Pick(AttackKind.Heavy1Charge, AttackKind.Heavy1);
        }

        public HitResult ReceiveHit(Incoming hit, ILevel level)
        {
            if (IsDead)
            {
                return HitResult.Ignored;
            }
            if (Invincible())
            {
                return HitResult.Dodged;
            }
            bool airborne = Airborne();
            if (hit.Low && airborne)
            {
                float clearance = Pos.Y - level.Height(Pos.X, Pos.Z);
                if (clearance > Consts.JUMP_CLEARANCE)
                {
                    return HitResult.Jumped;
                }
            }

            var toward = new Vec3(hit.From.X - Pos.X, 0f, hit.From.Z - Pos.Z);
            bool inArc = toward.NormalizeOrZero().Dot(Facing()) >= F.Cos(F.ToRadians(Consts.GUARD_ARC_DEG));
            if (AttackGuard() && inArc)
            {
                Stamina -= hit.Stamina * Consts.GUARD_STAMINA_TAKEN;
                if (Stamina <= 0f)
                {
                    Stamina = 0f;
                    Start(ActionId.GuardBreak);
                    return HitResult.GuardBroken;
                }
                return HitResult.Blocked;
            }
            if (GuardUp() && inArc)
            {
                Stamina -= hit.Stamina * Consts.GUARD_STAMINA_TAKEN;
                if (Stamina <= 0f)
                {
                    Stamina = 0f;
                    Start(ActionId.GuardBreak);
                    return HitResult.GuardBroken;
                }
                Start(ActionId.GuardHit);
                _guardCounter = Consts.GUARD_COUNTER_WINDOW;
                return HitResult.Blocked;
            }

            if (!NoFlinch)
            {
                SwapState = null;
            }
            Hp -= hit.Damage;
            if (Hp <= 0f)
            {
                Die();
                return HitResult.Killed;
            }
            if (!airborne && !NoFlinch)
            {
                Start(ActionId.Hurt(hit.Level, SideOf(toward)));
            }
            return HitResult.Hit;
        }

        /// <summary>Which side a hit coming along <paramref name="toward"/> (from the body to the attacker) lands on.</summary>
        private Dir SideOf(Vec3 toward)
        {
            float off = SimMath.AngleDiff(Yaw, SimMath.YawOf(toward));
            if (toward.LengthSquared() < 1e-6f || F.Abs(off) <= F.FRAC_PI_4)
            {
                return Dir.Front;
            }
            if (F.Abs(off) >= F.PI - F.FRAC_PI_4)
            {
                return Dir.Back;
            }
            return off > 0f ? Dir.Left : Dir.Right;
        }

        /// <summary>
        /// A reaction the host decides on, after its own damage has landed
        /// (Eldenheim: Valheim's block meter, a heavy blow's knockdown). Not in
        /// the air or dead.
        /// </summary>
        public void React(ActionId id)
        {
            if (!IsDead && !Airborne() && State != StateKind.Air)
            {
                Start(id);
            }
        }

        /// <summary>Knocked down by a blow from <paramref name="from"/>.</summary>
        public void Knockdown(Vec3 from) =>
            React(ActionId.Hurt(HurtLevel.Knockdown, SideOf(new Vec3(from.X - Pos.X, 0f, from.Z - Pos.Z))));

        private void Die()
        {
            Hp = 0f;
            Sprinting = false;
            Guarding = false;
            Crouching = false;
            Buffer = null;
            AirAttackState = null;
            SwapState = null;
            State = StateKind.Dead;
            DeadT = 0f;
        }

        public static int NextLeftHand(int left)
        {
            var order = new System.Collections.Generic.List<int> { ErData.SHIELD, ErData.FIST, ErData.TORCH };
            for (int i = 0; i < ErData.SHIELD; i++)
            {
                if (i != ErData.FIST && i != ErData.TORCH)
                {
                    order.Add(i);
                }
            }
            int at = Math.Max(order.IndexOf(left), 0);
            return order[(at + 1) % order.Count];
        }
    }
}
