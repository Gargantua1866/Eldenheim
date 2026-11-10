using System.IO;

namespace Eldenheim.Sim
{
    /// <summary>
    /// What the pose chooser reads from a player, packed for the network
    /// (PLAN §3.7): the owner sends it, other players fill a stand-in Player
    /// from it and pose that. Weapon and action numbers index the data build,
    /// so both ends must have the same one (Plugin.DataHash).
    /// </summary>
    public static class PoseState
    {
        private const byte VERSION = 2;

        public static byte[] Write(Player p)
        {
            var m = new MemoryStream(64);
            using (var w = new BinaryWriter(m))
            {
                w.Write(VERSION);
                w.Write((byte)p.State);
                w.Write((byte)p.Act.Id.Tag);
                w.Write(p.Act.Id.A);
                w.Write(p.Act.Id.B);
                w.Write(p.Act.Id.C);
                w.Write(p.Act.F);
                w.Write(p.Air.Jumped);
                w.Write(p.Air.F);
                w.Write(p.DeadT);
                w.Write(p.Speed);
                w.Write(p.MoveDir.X);
                w.Write(p.MoveDir.Z);
                // The owner's guard options decide which hand guards (GuardsWithRight).
                w.Write((byte)((p.Sprinting ? 1 : 0) | (p.Crouching ? 2 : 0) | (p.Guarding ? 4 : 0)
                               | (p.EmptyHandGuards ? 8 : 0) | (p.PairedOnLight ? 16 : 0) | (p.Air.Thrown ? 32 : 0)));
                w.Write((byte)p.Weapon);
                w.Write((byte)p.Left);
                w.Write((byte)p.Grip);
                w.Write(p.HitStop);
                w.Write(p.AirAttackState.HasValue);
                if (p.AirAttackState is AirAttack a)
                {
                    w.Write(a.Heavy);
                    w.Write(a.Paired);
                    w.Write(a.Moveset.Weapon);
                    w.Write(a.Moveset.TwoHand);
                    w.Write(a.F);
                }
                w.Write(p.SwapState.HasValue);
                if (p.SwapState is Swap s)
                {
                    w.Write((byte)s.Kind);
                    w.Write(s.F);
                    w.Write((byte)s.Grip);
                    w.Write((byte)s.Weapon);
                    w.Write((byte)s.Left);
                    w.Write(s.Applied);
                }
                // Last, so a build without it still reads the rest.
                w.Write(p.AimPitch);
            }
            return m.ToArray();
        }

        /// <summary>Fills p from Write's bytes; false (p untouched) if they aren't this version's.</summary>
        public static bool Read(byte[] data, Player p)
        {
            if (data == null || data.Length < 2 || data[0] != VERSION)
            {
                return false;
            }
            try
            {
                using (var r = new BinaryReader(new MemoryStream(data)))
                {
                    r.ReadByte();
                    var state = (StateKind)r.ReadByte();
                    var id = new ActionId((ActionTag)r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                    float actF = r.ReadSingle();
                    bool jumped = r.ReadBoolean();
                    float airF = r.ReadSingle();
                    float deadT = r.ReadSingle(), speed = r.ReadSingle();
                    var move = new Vec3(r.ReadSingle(), 0f, r.ReadSingle());
                    byte flags = r.ReadByte();
                    int weapon = r.ReadByte(), left = r.ReadByte();
                    var grip = (Grip)r.ReadByte();
                    float hitStop = r.ReadSingle();
                    AirAttack? air = null;
                    if (r.ReadBoolean())
                    {
                        bool heavy = r.ReadBoolean(), paired = r.ReadBoolean();
                        var moveset = new Moveset(r.ReadByte(), r.ReadBoolean());
                        AirAttackDef def = paired ? moveset.AirPaired() : moveset.Air(heavy);
                        air = new AirAttack { Heavy = heavy, Paired = paired, Moveset = moveset, Def = def, F = r.ReadSingle() };
                        if (def == null)
                        {
                            return false;
                        }
                    }
                    Swap? swap = null;
                    if (r.ReadBoolean())
                    {
                        swap = new Swap
                        {
                            Kind = (SwapKind)r.ReadByte(), F = r.ReadSingle(), Grip = (Grip)r.ReadByte(),
                            Weapon = r.ReadByte(), Left = r.ReadByte(), Applied = r.ReadBoolean(),
                        };
                    }
                    float aim = r.BaseStream.Position + 4 <= r.BaseStream.Length ? r.ReadSingle() : 0f;
                    if (weapon >= ErData.Weapons.Length || left >= ErData.Weapons.Length)
                    {
                        return false;
                    }
                    p.State = state;
                    p.Act.Id = id;
                    p.Act.F = actF;
                    p.Air.Jumped = jumped;
                    p.Air.F = airF;
                    p.Air.Thrown = (flags & 32) != 0;
                    p.DeadT = deadT;
                    p.Speed = speed;
                    p.MoveDir = move;
                    p.Sprinting = (flags & 1) != 0;
                    p.Crouching = (flags & 2) != 0;
                    p.Guarding = (flags & 4) != 0;
                    p.EmptyHandGuards = (flags & 8) != 0;
                    p.PairedOnLight = (flags & 16) != 0;
                    p.Weapon = weapon;
                    p.Left = left;
                    p.Grip = grip;
                    p.HitStop = hitStop;
                    p.AirAttackState = air;
                    p.SwapState = swap;
                    p.AimPitch = aim;
                    return true;
                }
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        /// <summary>
        /// Moves the frame counters on by dt seconds, as the owner's sim does
        /// between updates (nothing moves in hit-stop).
        /// </summary>
        public static void Advance(Player p, float dt)
        {
            if (p.HitStop > 0f)
            {
                p.HitStop -= dt;
                return;
            }
            float frames = dt * Consts.ANIM_FPS;
            ActionDef act = p.State == StateKind.Act ? ErData.Action(p.Act.Id) : null;
            p.Act.F += frames * (act != null ? act.SpeedAt(p.Act.F) : 1f);
            p.Air.F += frames;
            p.DeadT += frames;
            if (p.AirAttackState is AirAttack a)
            {
                a.F += frames * a.Def.SpeedAt(a.F);
                p.AirAttackState = a;
            }
            if (p.SwapState is Swap s)
            {
                s.F += frames;
                p.SwapState = s;
            }
        }
    }
}
