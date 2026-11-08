using System;
using System.Collections.Generic;

namespace Eldenheim.Sim
{
    // Port of src/sim/data.rs. Enum orders match the Rust declarations: the
    // exported data and traces identify them by ordinal.

    public enum Load : byte { Light, Medium, Heavy }

    public enum Dir : byte { Front, Back, Left, Right }

    public enum HurtLevel : byte { Small, Middle, Large, Knockdown }

    public enum JumpKind : byte { Stand, Walk, WalkBack, WalkLeft, WalkRight, Run, RunBack, RunLeft, RunRight, Sprint }

    public enum Grip : byte { OneHand, TwoHandRight, TwoHandLeft }

    public enum SwapKind : byte { ToTwoHandRight, ToTwoHandLeft, ToOneHandFromRight, ToOneHandFromLeft, NextWeapon, NextLeft }

    public enum AttackKind : byte
    {
        Light1, Light2, Light3, Light4, Light5, Light6, RunLight, RunHeavy, RollAttack, CrouchAttack, BackstepAttack,
        Heavy1Charge, Heavy1, Heavy2Charge, Heavy2, GuardCounter, JumpLightLand, JumpLightLandShort, JumpHeavyLand,
        JumpHeavyLandShort, LeftLight1, LeftLight2, LeftLight3, LeftLight4, LeftLight5, LeftLight6, PairedLight1,
        PairedLight2, PairedLight3, PairedLight4, PairedLight5, PairedLight6, PairedRun, PairedRoll, PairedBackstep,
        PairedJumpLand, PairedJumpLandShort, Feint1, Feint2,
    }

    public static class Consts
    {
        public const float ANIM_FPS = 30f;
        public const float DT = 1f / 60f;
        public const float DF = ANIM_FPS * DT;

        public const float STAMINA_REGEN = 45f;
        public const float GUARD_REGEN_MULT = 0.5f;
        public const float SPRINT_DRAIN = 11f;
        public const float GUARD_STAMINA_TAKEN = 0.55f;

        public const float ROLL_COST = 12f;
        public const float BACKSTEP_COST = 8f;
        public const float JUMP_COST = 10f;

        public const float ACCEL = 26f;
        public const float DECEL = 32f;
        public const float WALK_TILT = 0.55f;
        public const float STICK_DEADZONE = 0.1f;
        public const float TURN_RUN = 1080f;
        public const float TURN_SPRINT = 480f;
        public const float TURN_LOCKED = 720f;
        public const float TURN_ACTION_DEFAULT = 360f;
        public const float SPRINT_HOLD_FRAMES = 10f;
        public const float STEP_HEIGHT = 0.35f;

        public const float GRAVITY = 18f;
        public const float TERMINAL_VELOCITY = 40f;
        public const float JUMP_CLEARANCE = 0.35f;
        public const float JUMP_BECOMES_FALL = 3f;
        public const float FALL_HEAVY_LANDING = 8f;
        public const float FALL_DAMAGE_START = 16f;
        public const float FALL_DEATH = 20f;

        public const float HIT_STOP_SCALE = 0.5f;
        public const float GUARD_COUNTER_WINDOW = 20f;
        public const float GUARD_RAISE_FRAMES = 0f;
        public const float GUARD_ARC_DEG = 80f;

        public const float LOCK_ON_RANGE = 15f;
        public const float LOCK_BREAK_RANGE = 22f;
        public const float NEVER = 9999f;
    }

    public class WeaponInfo
    {
        public string Name;
        public byte Category;
        public float Attack;
        public float Weight;
        public byte[] Stance;
    }

    public class SwapDef
    {
        public string Start, End;
        public float StartLen, EndLen, Apply, FreeFrom;
        public float Total => StartLen + EndLen;
    }

    public class Hit
    {
        public float From, To, Mv, GuardDamage, Stamina, Stop, Radius;
        public float[][] Blade;
        /// <summary>The weapon is thrown here (the Smithscript weapons): 1 from the right hand, 2 the left, 0 no throw.</summary>
        public byte Throw;
    }

    public class AirAttackDef
    {
        public float From, To, Stamina;
        public string Source;
        public float Radius;
        public float[][] Blade;
        public float[][] Speed;
        /// <summary>As <see cref="Hit.Throw"/>.</summary>
        public byte Throw;

        public float SpeedAt(float frame) => ActionDef.SpeedAt(Speed, frame);
    }

    public class ActionDef
    {
        public string Name, Source;
        public float Total, InputFrom, InputDodgeFrom;
        public float CancelLight, CancelHeavy, CancelDodge, CancelJump, CancelGuard, CancelMove, CancelLeft;
        public float IframesFrom, IframesTo;
        public bool JumpFrames;
        public float Stamina;
        public Hit[] Hits;
        public bool HasCharge;
        public float ChargeFrom, ChargeTo;
        public float[][] NoTurn;
        public float[][] Turn;
        public float[][] Motion;
        /// <summary>
        /// Windows the game plays faster or slower (TAE 608): from, to, speed
        /// at from, speed at to. Frames are the animation's own, so in them
        /// the action's frame simply advances faster.
        /// </summary>
        public float[][] Speed;
        /// <summary>Frames the character guards through the attack (the Carian Knight's Sword's charging heavies).</summary>
        public float GuardFrom, GuardTo;

        public float SpeedAt(float frame) => SpeedAt(Speed, frame);

        /// <summary>sim/data.rs speed_at(): the speed at a frame; 1 outside every window.</summary>
        public static float SpeedAt(float[][] windows, float frame)
        {
            foreach (float[] w in windows)
            {
                if (frame >= w[0] && frame < w[1])
                {
                    return w[2] + (w[3] - w[2]) * (frame - w[0]) / (w[1] - w[0]);
                }
            }
            return 1f;
        }

        /// <summary>Cumulative [left, up, forward] root motion at a frame.</summary>
        public void MotionAt(float frame, out float left, out float up, out float forward)
        {
            if (Motion.Length == 0)
            {
                left = up = forward = 0f;
                return;
            }
            int last = Motion.Length - 1;
            frame = F.Clamp(frame, 0f, last);
            int i = Math.Min((int)F.Floor(frame), last);
            int j = Math.Min(i + 1, last);
            float t = frame - i;
            float[] a = Motion[i], b = Motion[j];
            left = a[0] + (b[0] - a[0]) * t;
            up = a[1] + (b[1] - a[1]) * t;
            forward = a[2] + (b[2] - a[2]) * t;
        }

        public float UpAt(float frame)
        {
            MotionAt(frame, out _, out float up, out _);
            return up;
        }

        public Hit FirstHit => Hits.Length > 0 ? Hits[0] : null;

        public bool CanTurn(float frame)
        {
            foreach (float[] w in NoTurn)
            {
                if (frame >= w[0] && frame < w[1])
                {
                    return false;
                }
            }
            return true;
        }

        public float TurnRate(float frame)
        {
            foreach (float[] w in Turn)
            {
                if (frame >= w[0] && frame < w[1])
                {
                    return w[2];
                }
            }
            return Consts.TURN_ACTION_DEFAULT;
        }

        /// <summary>The ends of a blade at a frame, in the character's own space.</summary>
        public static bool BladeAt(float[][] blade, float from, float frame, out Vec3 a, out Vec3 b)
        {
            a = b = Vec3.Zero;
            if (blade.Length == 0)
            {
                return false;
            }
            int last = blade.Length - 1;
            float at = F.Clamp(frame - F.Floor(from), 0f, last);
            float[] p = blade[(int)at], q = blade[Math.Min((int)at + 1, last)];
            float t = F.Fract(at);
            a = new Vec3(p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t, p[2] + (q[2] - p[2]) * t);
            b = new Vec3(p[3] + (q[3] - p[3]) * t, p[4] + (q[4] - p[4]) * t, p[5] + (q[5] - p[5]) * t);
            return true;
        }
    }

    public struct Moveset : IEquatable<Moveset>
    {
        public byte Weapon;
        public bool TwoHand;

        public Moveset(int weapon, bool twoHand)
        {
            Weapon = (byte)weapon;
            TwoHand = twoHand;
        }

        public ActionDef Attack(AttackKind kind) => ErData.Attack(Weapon, TwoHand, kind);
        public bool Has(AttackKind kind) => Attack(kind) != null;
        public AirAttackDef Air(bool heavy) => ErData.Air(Weapon, TwoHand, heavy ? 1 : 0);
        public AirAttackDef AirPaired() => ErData.Air(Weapon, false, 2);
        public WeaponInfo Info => ErData.Weapons[Weapon];

        public bool Equals(Moveset o) => Weapon == o.Weapon && TwoHand == o.TwoHand;
        public override bool Equals(object obj) => obj is Moveset m && Equals(m);
        public override int GetHashCode() => Weapon * 2 + (TwoHand ? 1 : 0);
        public static bool operator ==(Moveset a, Moveset b) => a.Equals(b);
        public static bool operator !=(Moveset a, Moveset b) => !a.Equals(b);
    }

    public enum ActionTag : byte
    {
        Roll, CrouchRoll, Backstep, SprintStop, Jump, LandLight, LandRun, LandSprint, LandStrafeWalk, LandStrafe,
        LandHeavy, LandFall, Attack, GuardHit, GuardBreak, Hurt, GuardBlow,
    }

    /// <summary>
    /// The Rust ActionId enum: a tag and up to three ordinals, exactly as
    /// simtrace keys them.
    /// </summary>
    public struct ActionId : IEquatable<ActionId>
    {
        public ActionTag Tag;
        public byte A, B, C;

        public ActionId(ActionTag tag, byte a = 0, byte b = 0, byte c = 0)
        {
            Tag = tag;
            A = a;
            B = b;
            C = c;
        }

        public static ActionId Roll(Load l, Dir d) => new ActionId(ActionTag.Roll, (byte)l, (byte)d);
        public static ActionId CrouchRoll(Load l, Dir d) => new ActionId(ActionTag.CrouchRoll, (byte)l, (byte)d);
        public static readonly ActionId Backstep = new ActionId(ActionTag.Backstep);
        public static readonly ActionId SprintStop = new ActionId(ActionTag.SprintStop);
        public static ActionId Jump(JumpKind k) => new ActionId(ActionTag.Jump, (byte)k);
        public static readonly ActionId LandLight = new ActionId(ActionTag.LandLight);
        public static readonly ActionId LandRun = new ActionId(ActionTag.LandRun);
        public static readonly ActionId LandSprint = new ActionId(ActionTag.LandSprint);
        public static ActionId LandStrafeWalk(Dir d) => new ActionId(ActionTag.LandStrafeWalk, (byte)d);
        public static ActionId LandStrafe(Dir d) => new ActionId(ActionTag.LandStrafe, (byte)d);
        public static readonly ActionId LandHeavy = new ActionId(ActionTag.LandHeavy);
        public static readonly ActionId LandFall = new ActionId(ActionTag.LandFall);
        public static ActionId Attack(Moveset m, AttackKind k) => new ActionId(ActionTag.Attack, m.Weapon, (byte)(m.TwoHand ? 1 : 0), (byte)k);
        public static readonly ActionId GuardHit = new ActionId(ActionTag.GuardHit);
        public static readonly ActionId GuardBreak = new ActionId(ActionTag.GuardBreak);
        public static readonly ActionId GuardBlow = new ActionId(ActionTag.GuardBlow);
        public static ActionId Hurt(HurtLevel l, Dir d) => new ActionId(ActionTag.Hurt, (byte)l, (byte)d);

        public bool IsAttack => Tag == ActionTag.Attack;
        public Moveset Moveset => new Moveset(A, B != 0);
        public AttackKind Kind => (AttackKind)C;
        public bool IsRollLike => Tag == ActionTag.Roll || Tag == ActionTag.CrouchRoll;

        public ActionDef Def()
        {
            ActionDef d = ErData.Action(this);
            if (d == null)
            {
                throw new InvalidOperationException("no extracted data for " + this);
            }
            return d;
        }

        public float StartCost()
        {
            switch (Tag)
            {
                case ActionTag.Roll:
                case ActionTag.CrouchRoll:
                    return Consts.ROLL_COST;
                case ActionTag.Backstep:
                    return Consts.BACKSTEP_COST;
                case ActionTag.Jump:
                    return Consts.JUMP_COST;
                default:
                    return 0f;
            }
        }

        public float ExitSpeed()
        {
            switch (Tag)
            {
                case ActionTag.Roll when (Load)A == Load.Heavy:
                    return ErData.RUN_SPEED * 0.4f;
                case ActionTag.Roll:
                case ActionTag.LandRun:
                    return ErData.RUN_SPEED;
                case ActionTag.CrouchRoll:
                    return ErData.CROUCH_RUN_SPEED;
                case ActionTag.LandSprint:
                    return ErData.SPRINT_SPEED;
                case ActionTag.LandStrafeWalk:
                    return ErData.WALK_SPEED;
                case ActionTag.LandStrafe when (Dir)A == Dir.Front:
                    return ErData.RUN_SPEED;
                case ActionTag.LandStrafe when (Dir)A == Dir.Back:
                    return ErData.RUN_BACK_SPEED;
                case ActionTag.LandStrafe:
                    return ErData.RUN_SIDE_SPEED;
                default:
                    return 0f;
            }
        }

        public int Key => (int)Tag << 24 | A << 16 | B << 8 | C;

        public bool Equals(ActionId o) => Tag == o.Tag && A == o.A && B == o.B && C == o.C;
        public override bool Equals(object obj) => obj is ActionId a && Equals(a);
        public override int GetHashCode() => Key;
        public static bool operator ==(ActionId a, ActionId b) => a.Equals(b);
        public static bool operator !=(ActionId a, ActionId b) => !a.Equals(b);

        public override string ToString()
        {
            switch (Tag)
            {
                case ActionTag.Roll:
                case ActionTag.CrouchRoll:
                    return $"{Tag}({(Load)A}, {(Dir)B})";
                case ActionTag.Jump:
                    return $"Jump({(JumpKind)A})";
                case ActionTag.LandStrafeWalk:
                case ActionTag.LandStrafe:
                    return $"{Tag}({(Dir)A})";
                case ActionTag.Attack:
                    return $"Attack({(A < ErData.Weapons.Length ? ErData.Weapons[A].Name : A.ToString())}{(B != 0 ? " 2H" : "")}, {(AttackKind)C})";
                case ActionTag.Hurt:
                    return $"Hurt({(HurtLevel)A}, {(Dir)B})";
                default:
                    return Tag.ToString();
            }
        }
    }
}
