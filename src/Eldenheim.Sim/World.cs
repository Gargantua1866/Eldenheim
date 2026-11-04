using System.Collections.Generic;

namespace Eldenheim.Sim
{
    /// <summary>sim/mod.rs's World: the player, the sparring dummy and the level.</summary>
    public class World
    {
        public ILevel Level;
        public Player Player;
        public Dummy Dummy;
        public bool Locked;
        public bool RecenterCamera;
        public readonly List<string> Log = new List<string>();

        public World(ILevel level)
        {
            Level = level;
            Player = new Player(Vec3.Zero);
            Dummy = new Dummy(new Vec3(0f, 0f, 8f));
        }

        public Vec3? Target() => Locked ? Dummy.Pos : (Vec3?)null;

        public void Step(Input inp)
        {
            float distance = Player.Pos.Distance(Dummy.Pos);

            RecenterCamera = false;
            if (inp.Lock)
            {
                if (Locked)
                {
                    Locked = false;
                }
                else if (Dummy.Alive && distance <= Consts.LOCK_ON_RANGE)
                {
                    Locked = true;
                }
                else
                {
                    RecenterCamera = true;
                }
            }
            if (Locked && (!Dummy.Alive || distance > Consts.LOCK_BREAK_RANGE || Player.IsDead))
            {
                Locked = false;
            }

            bool inCombat = Dummy.Hostile && distance < 25f;
            Player.Step(inp, Level, Target(), inCombat);
            SeparateBodies();
            PlayerAttacks();

            Incoming hit = Dummy.Step(Player.Pos, Player.IsDead);
            if (hit != null)
            {
                HitResult result = Player.ReceiveHit(hit, Level);
                string message;
                switch (result)
                {
                    case HitResult.Dodged: message = "Dodged (i-frames)"; break;
                    case HitResult.Jumped: message = "Jumped over the sweep"; break;
                    case HitResult.Blocked: message = "Blocked - guard counter ready"; break;
                    case HitResult.GuardBroken: message = "Guard broken"; break;
                    case HitResult.Hit: message = $"Took {hit.Damage:0} damage"; break;
                    case HitResult.Killed: message = "YOU DIED"; break;
                    default: message = null; break;
                }
                bool evaded = result == HitResult.Dodged || result == HitResult.Jumped;
                if (!evaded)
                {
                    Dummy.Connected = true;
                }
                if (message != null && !(evaded && Dummy.EvadeLogged))
                {
                    Log.Add(message);
                }
                Dummy.EvadeLogged |= evaded;
            }
            if (Player.IsDead)
            {
                Dummy.Calm();
            }
        }

        private void SeparateBodies()
        {
            if (!Dummy.Alive)
            {
                return;
            }
            Player p = Player;
            if (F.Abs(p.Pos.Y - Dummy.Pos.Y) > 1.8f)
            {
                return;
            }
            var away = new Vec3(p.Pos.X - Dummy.Pos.X, 0f, p.Pos.Z - Dummy.Pos.Z);
            float min = Dummy.RADIUS + 0.35f;
            float dist = away.Length();
            if (dist >= min)
            {
                return;
            }
            if (dist < 1e-3f)
            {
                away = -p.Facing();
            }
            Vec3 push = away.Normalize() * (min - dist);
            Level.Slide(ref p.Pos, push);
        }

        private void PlayerAttacks()
        {
            ActiveHit hit = Player.ActiveHitNow();
            if (hit == null || !Dummy.Alive)
            {
                return;
            }
            Vec3 at = Dummy.Pos;
            Vec3 feet = at + Vec3.UnitY * Dummy.RADIUS, shoulders = at + Vec3.UnitY * (Dummy.HEIGHT - Dummy.RADIUS);
            Vec3 head = at + Vec3.UnitY * Dummy.HEAD_HEIGHT;
            bool touches = false;
            for (int i = 0; i < Player.SWEEP_STEPS && !touches; i++)
            {
                Vec3 a = hit.SweepA[i], b = hit.SweepB[i];
                touches = SimMath.SegmentDistance(a, b, feet, shoulders) <= hit.Radius + Dummy.RADIUS
                          || SimMath.SegmentDistance(a, b, head, head) <= hit.Radius + Dummy.HEAD_RADIUS;
            }
            if (!touches)
            {
                return;
            }
            float damage = hit.Attack * hit.Mv;
            string outcome = Dummy.TakeHit(damage, hit.GuardDamage * 5f);
            Player.MarkHit();
            Player.HitStop = hit.Stop * Consts.HIT_STOP_SCALE;
            Dummy.HitStop = hit.Stop * Consts.HIT_STOP_SCALE;
            Log.Add($"Hit for {damage:0}{outcome}");
        }
    }
}
