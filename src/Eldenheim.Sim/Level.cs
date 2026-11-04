using System.Collections.Generic;

namespace Eldenheim.Sim
{
    /// <summary>
    /// What the sim asks of the world (level.rs's two methods). The arena
    /// implements it for tests; Valheim implements it with physics queries.
    /// </summary>
    public interface ILevel
    {
        /// <summary>Height of the ground under (x, z).</summary>
        float Height(float x, float z);

        /// <summary>Moves pos across by delta, sliding along anything too tall to step onto.</summary>
        void Slide(ref Vec3 pos, Vec3 delta);
    }

    /// <summary>level.rs: flat ground at y = 0 plus solid columns.</summary>
    public class BlockLevel : ILevel
    {
        public struct Block
        {
            public float MinX, MinZ, MaxX, MaxZ, Top;
        }

        public readonly List<Block> Blocks = new List<Block>();

        public float Height(float x, float z)
        {
            float h = 0f;
            foreach (Block b in Blocks)
            {
                if (x >= b.MinX && x <= b.MaxX && z >= b.MinZ && z <= b.MaxZ)
                {
                    h = F.Max(h, b.Top);
                }
            }
            return h;
        }

        public void Slide(ref Vec3 pos, Vec3 delta)
        {
            float reach = pos.Y + Consts.STEP_HEIGHT;
            float x = pos.X + delta.X, z = pos.Z + delta.Z;
            if (Height(x, z) <= reach)
            {
                pos.X = x;
                pos.Z = z;
            }
            else if (Height(x, pos.Z) <= reach)
            {
                pos.X = x;
            }
            else if (Height(pos.X, z) <= reach)
            {
                pos.Z = z;
            }
        }

        private void Add(float x0, float x1, float z0, float z1, float top) =>
            Blocks.Add(new Block { MinX = x0, MaxX = x1, MinZ = z0, MaxZ = z1, Top = top });

        public static BlockLevel Flat() => new BlockLevel();

        public static BlockLevel Arena()
        {
            var l = new BlockLevel();
            l.Add(-8f, -6f, 4f, 6f, 1f);
            l.Add(-11f, -8f, 4f, 6f, 1.75f);
            l.Add(-13f, -11f, 4f, 6f, 2.5f);
            l.Add(-14f, -13f, -4f, 8f, 2.5f);
            l.Add(-15f, -12f, 9.5f, 13.5f, 2.5f);
            l.Add(9f, 10.5f, 0f, 1.5f, 1f);
            l.Add(10.5f, 12f, 0f, 1.5f, 2f);
            l.Add(-4f, -1f, -13.5f, -10.5f, 0.3f);
            l.Add(-3.5f, -1.5f, -13f, -11f, 0.6f);
            l.Add(12.5f, 16f, -7f, -4f, 1.5f);
            foreach (float x in new[] { -6f, -2f, 2f, 6f })
            {
                l.Add(x - 0.5f, x + 0.5f, -9f, -8f, 3.5f);
            }
            for (int i = 0; i < 15; i++)
            {
                float x = 8f + i * 0.3f;
                l.Add(x, x + 0.3f, -7f, -4f, (i + 1) * 0.1f);
            }
            const float rise = 0.25f, run = 0.5f;
            const int steps = 84;
            for (int i = 0; i < steps; i++)
            {
                float x = 8f + i * run;
                l.Add(x, x + run, 10f, 13f, (i + 1) * rise);
            }
            float end = 8f + steps * run;
            l.Add(end, end + 5f, 8f, 15f, steps * rise);
            return l;
        }
    }
}
