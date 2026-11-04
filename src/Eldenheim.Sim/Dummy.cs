namespace Eldenheim.Sim
{
    public enum DState : byte { Idle, Windup, Strike, Recover, Stagger, Dead }

    /// <summary>dummy.rs: the sparring partner.</summary>
    public class Dummy
    {
        public const float RADIUS = 0.5f;
        public const float HEIGHT = 2.3f;
        public const float HEAD_HEIGHT = 2.45f;
        public const float HEAD_RADIUS = 0.3f;
        public const float MAX_HP = 1200f;
        private const float MAX_POISE = 60f;
        private const float AGGRO_RANGE = 4.2f;
        private const float COOLDOWN = 1.6f;
        public const float WINDUP = 0.9f;
        private const float TRACK_UNTIL = 0.6f;
        public const float STRIKE = 0.1f;
        private const float RECOVER = 1.2f;
        private const float STAGGER = 1.6f;
        private const float RESPAWN = 4f;

        private static readonly (bool low, HurtLevel level)[] Swings =
        {
            (false, HurtLevel.Middle),
            (true, HurtLevel.Small),
            (false, HurtLevel.Large),
            (false, HurtLevel.Knockdown),
        };

        public Vec3 Pos;
        public float Yaw;
        public float Hp;
        public float Poise;
        public DState State;
        /// <summary>For Windup and Strike: a low sweep.</summary>
        public bool Low;
        public float T;
        public bool Aggressive;
        public bool Connected;
        public bool EvadeLogged;
        public HurtLevel Level;
        public float HitStop;
        private byte _next;
        private float _sinceHit;

        public Dummy(Vec3 pos)
        {
            Pos = pos;
            Yaw = F.PI;
            Hp = MAX_HP;
            Poise = MAX_POISE;
            State = DState.Idle;
            Level = HurtLevel.Middle;
        }

        public bool Alive => State != DState.Dead;
        public bool Hostile => Aggressive && Alive;

        private void Enter(DState state, bool low = false)
        {
            State = state;
            Low = low;
            T = 0f;
        }

        public void Calm()
        {
            if (State == DState.Windup || State == DState.Strike || State == DState.Recover)
            {
                Enter(DState.Idle);
            }
        }

        public string TakeHit(float damage, float poise)
        {
            Hp -= damage;
            _sinceHit = 0f;
            if (Hp <= 0f)
            {
                Hp = 0f;
                Enter(DState.Dead);
                return " - defeated";
            }
            Poise -= poise;
            if (Poise <= 0f)
            {
                Poise = MAX_POISE;
                Enter(DState.Stagger);
                return " - poise broken";
            }
            return "";
        }

        public Incoming Step(Vec3 player, bool playerDead)
        {
            if (HitStop > 0f)
            {
                HitStop -= Consts.DT;
                return null;
            }
            T += Consts.DT;
            _sinceHit += Consts.DT;
            if (_sinceHit > 5f)
            {
                Poise = MAX_POISE;
            }
            var to = new Vec3(player.X - Pos.X, 0f, player.Z - Pos.Z);
            float distance = to.Length();
            void Track(float rate)
            {
                if (distance > 0.01f)
                {
                    Yaw = SimMath.TurnToward(Yaw, SimMath.YawOf(to), F.ToRadians(rate) * Consts.DT);
                }
            }
            switch (State)
            {
                case DState.Idle:
                {
                    Track(180f);
                    bool level = F.Abs(player.Y - Pos.Y) < 1.5f;
                    if (Aggressive && !playerDead && level && T >= COOLDOWN && distance <= AGGRO_RANGE)
                    {
                        Connected = false;
                        EvadeLogged = false;
                        var swing = Swings[_next % Swings.Length];
                        _next = unchecked((byte)(_next + 1));
                        Level = swing.level;
                        Enter(DState.Windup, swing.low);
                    }
                    break;
                }
                case DState.Windup:
                    if (T < TRACK_UNTIL)
                    {
                        Track(240f);
                    }
                    if (T >= WINDUP)
                    {
                        Enter(DState.Strike, Low);
                    }
                    break;
                case DState.Strike:
                    if (T >= STRIKE)
                    {
                        Enter(DState.Recover);
                    }
                    else if (!Connected)
                    {
                        float range = Low ? 3.6f : 3.4f, halfArc = Low ? 180f : 40f;
                        float damage, stamina;
                        switch (Level)
                        {
                            case HurtLevel.Small: damage = 120f; stamina = 30f; break;
                            case HurtLevel.Middle: damage = 160f; stamina = 40f; break;
                            case HurtLevel.Large: damage = 200f; stamina = 50f; break;
                            default: damage = 240f; stamina = 60f; break;
                        }
                        bool inArc = to.NormalizeOrZero().Dot(SimMath.DirOf(Yaw)) >= F.Cos(F.ToRadians(halfArc));
                        bool inHeight = F.Abs(player.Y - Pos.Y) < 2.5f;
                        if (distance <= range && inArc && inHeight)
                        {
                            return new Incoming { Damage = damage, Stamina = stamina, From = Pos, Low = Low, Level = Level };
                        }
                    }
                    break;
                case DState.Recover:
                    if (T >= RECOVER)
                    {
                        Enter(DState.Idle);
                    }
                    break;
                case DState.Stagger:
                    if (T >= STAGGER)
                    {
                        Enter(DState.Idle);
                    }
                    break;
                case DState.Dead:
                    if (T >= RESPAWN)
                    {
                        bool aggressive = Aggressive;
                        var fresh = new Dummy(Pos) { Aggressive = aggressive };
                        CopyFrom(fresh);
                    }
                    break;
            }
            return null;
        }

        private void CopyFrom(Dummy d)
        {
            Pos = d.Pos;
            Yaw = d.Yaw;
            Hp = d.Hp;
            Poise = d.Poise;
            State = d.State;
            Low = d.Low;
            T = d.T;
            Aggressive = d.Aggressive;
            Connected = d.Connected;
            EvadeLogged = d.EvadeLogged;
            Level = d.Level;
            HitStop = d.HitStop;
            _next = d._next;
            _sinceHit = d._sinceHit;
        }
    }
}
