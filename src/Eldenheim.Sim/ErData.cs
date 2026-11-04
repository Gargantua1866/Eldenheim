using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Eldenheim.Sim
{
    /// <summary>
    /// The sandbox's extracted.rs, read from eldenheim_data.bin (written by
    /// tools/simtrace from the sandbox's own tables). Load it once before
    /// stepping anything.
    /// </summary>
    public static class ErData
    {
        public static float WALK_SPEED, RUN_SPEED, RUN_BACK_SPEED, RUN_SIDE_SPEED, SPRINT_SPEED, CROUCH_WALK_SPEED, CROUCH_RUN_SPEED;
        public static float MAX_HP, MAX_STAMINA;
        public static int DEFAULT_WEAPON, SHIELD, FIST, TORCH;
        public static WeaponInfo[] Weapons;
        public static SwapDef[] Swaps;
        public static bool Loaded;

        private static readonly Dictionary<int, ActionDef> Actions = new Dictionary<int, ActionDef>();
        private static readonly Dictionary<int, AirAttackDef> Airs = new Dictionary<int, AirAttackDef>();

        public static IEnumerable<KeyValuePair<int, ActionDef>> AllActions => Actions;

        public static ActionDef Action(ActionId id) => Actions.TryGetValue(id.Key, out ActionDef d) ? d : null;

        public static ActionDef Attack(int weapon, bool twoHand, AttackKind kind) =>
            Action(new ActionId(ActionTag.Attack, (byte)weapon, (byte)(twoHand ? 1 : 0), (byte)kind));

        /// <summary>kind: 0 light, 1 heavy, 2 paired (with twoHand false).</summary>
        public static AirAttackDef Air(int weapon, bool twoHand, int kind) =>
            Airs.TryGetValue(weapon << 16 | (twoHand ? 1 : 0) << 8 | kind, out AirAttackDef d) ? d : null;

        public static SwapDef Swap(SwapKind kind) => Swaps[(int)kind];

        public static void Load(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "EHDT" || r.ReadUInt32() != 2)
                {
                    throw new IOException("not an eldenheim_data.bin (version 2)");
                }
                WALK_SPEED = r.ReadSingle();
                RUN_SPEED = r.ReadSingle();
                RUN_BACK_SPEED = r.ReadSingle();
                RUN_SIDE_SPEED = r.ReadSingle();
                SPRINT_SPEED = r.ReadSingle();
                CROUCH_WALK_SPEED = r.ReadSingle();
                CROUCH_RUN_SPEED = r.ReadSingle();
                MAX_HP = r.ReadSingle();
                MAX_STAMINA = r.ReadSingle();
                DEFAULT_WEAPON = (int)r.ReadUInt32();
                SHIELD = (int)r.ReadUInt32();
                FIST = (int)r.ReadUInt32();
                TORCH = (int)r.ReadUInt32();

                Weapons = new WeaponInfo[r.ReadUInt32()];
                for (int i = 0; i < Weapons.Length; i++)
                {
                    Weapons[i] = new WeaponInfo
                    {
                        Name = Str(r),
                        Category = r.ReadByte(),
                        Attack = r.ReadSingle(),
                        Weight = r.ReadSingle(),
                        Stance = new[] { r.ReadByte(), r.ReadByte() },
                    };
                }

                Actions.Clear();
                uint actions = r.ReadUInt32();
                for (uint i = 0; i < actions; i++)
                {
                    var id = new ActionId((ActionTag)r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                    Actions[id.Key] = ReadAction(r);
                }

                Airs.Clear();
                uint airs = r.ReadUInt32();
                for (uint i = 0; i < airs; i++)
                {
                    int weapon = r.ReadByte(), two = r.ReadByte(), kind = r.ReadByte();
                    Airs[weapon << 16 | two << 8 | kind] = new AirAttackDef
                    {
                        From = r.ReadSingle(),
                        To = r.ReadSingle(),
                        Stamina = r.ReadSingle(),
                        Source = Str(r),
                        Radius = r.ReadSingle(),
                        Blade = Rows(r, 6),
                        Speed = Rows(r, 4),
                    };
                }

                Swaps = new SwapDef[6];
                for (int i = 0; i < 6; i++)
                {
                    Swaps[i] = new SwapDef
                    {
                        Start = Str(r),
                        End = Str(r),
                        StartLen = r.ReadSingle(),
                        EndLen = r.ReadSingle(),
                        Apply = r.ReadSingle(),
                        FreeFrom = r.ReadSingle(),
                    };
                }
            }
            Loaded = true;
        }

        private static ActionDef ReadAction(BinaryReader r)
        {
            var d = new ActionDef
            {
                Name = Str(r),
                Source = Str(r),
                Total = r.ReadSingle(),
                InputFrom = r.ReadSingle(),
                InputDodgeFrom = r.ReadSingle(),
                CancelLight = r.ReadSingle(),
                CancelHeavy = r.ReadSingle(),
                CancelDodge = r.ReadSingle(),
                CancelJump = r.ReadSingle(),
                CancelGuard = r.ReadSingle(),
                CancelMove = r.ReadSingle(),
                CancelLeft = r.ReadSingle(),
                IframesFrom = r.ReadSingle(),
                IframesTo = r.ReadSingle(),
                JumpFrames = r.ReadByte() != 0,
                Stamina = r.ReadSingle(),
            };
            d.Hits = new Hit[r.ReadUInt32()];
            for (int i = 0; i < d.Hits.Length; i++)
            {
                d.Hits[i] = new Hit
                {
                    From = r.ReadSingle(),
                    To = r.ReadSingle(),
                    Mv = r.ReadSingle(),
                    GuardDamage = r.ReadSingle(),
                    Stamina = r.ReadSingle(),
                    Stop = r.ReadSingle(),
                    Radius = r.ReadSingle(),
                    Blade = Rows(r, 6),
                };
            }
            d.HasCharge = r.ReadByte() != 0;
            if (d.HasCharge)
            {
                d.ChargeFrom = r.ReadSingle();
                d.ChargeTo = r.ReadSingle();
            }
            d.NoTurn = Rows(r, 2);
            d.Turn = Rows(r, 3);
            d.Motion = Rows(r, 3);
            d.Speed = Rows(r, 4);
            return d;
        }

        private static float[][] Rows(BinaryReader r, int width)
        {
            var rows = new float[r.ReadUInt32()][];
            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = new float[width];
                for (int j = 0; j < width; j++)
                {
                    rows[i][j] = r.ReadSingle();
                }
            }
            return rows;
        }

        private static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes((int)r.ReadUInt32()));
    }
}
