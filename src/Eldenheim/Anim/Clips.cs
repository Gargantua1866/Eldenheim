using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Eldenheim.Anim
{
    /// <summary>
    /// A pose of the lean ER skeleton in character space (Unity axes): every
    /// bone's rotation, and positions for the few bones the bake stores them for.
    /// </summary>
    public class ErPose
    {
        public readonly Quaternion[] Rot;
        public readonly Vector3[] Pos;

        public ErPose(int bones)
        {
            Rot = new Quaternion[bones];
            Pos = new Vector3[bones];
        }

        public void CopyFrom(ErPose o)
        {
            System.Array.Copy(o.Rot, Rot, Rot.Length);
            System.Array.Copy(o.Pos, Pos, Pos.Length);
        }
    }

    public class Clip
    {
        public string Name;
        public float Blend;
        public int Frames;
        internal short[] Rot;
        internal float[] Pos;
    }

    /// <summary>tools/bake_unity.py --all (version 2): every clip the sandbox plays.</summary>
    public class Clips
    {
        public string[] Bones;
        public int[] Positioned;
        public ErPose Rest;
        private readonly Dictionary<string, Clip> _clips = new Dictionary<string, Clip>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>();

        public int Count => _clips.Count;
        public Clip Get(string name) => name != null && _clips.TryGetValue(name, out Clip c) && c.Frames > 0 ? c : null;
        public int Bone(string name) => _index.TryGetValue(name, out int i) ? i : -1;

        public static Clips Load(string path)
        {
            using (var r = new BinaryReader(File.OpenRead(path)))
            {
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "EHAN" || r.ReadUInt32() != 2)
                {
                    throw new IOException("not an eldenheim_anims_all.bin (version 2)");
                }
                int bones = (int)r.ReadUInt32(), clips = (int)r.ReadUInt32();
                var c = new Clips { Bones = new string[bones] };
                for (int i = 0; i < bones; i++)
                {
                    c.Bones[i] = Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte()));
                    c._index[c.Bones[i]] = i;
                }
                c.Rest = new ErPose(bones);
                for (int i = 0; i < bones; i++)
                {
                    c.Rest.Pos[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    c.Rest.Rot[i] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                }
                c.Positioned = new int[r.ReadUInt32()];
                for (int i = 0; i < c.Positioned.Length; i++)
                {
                    c.Positioned[i] = r.ReadUInt16();
                }
                for (int n = 0; n < clips; n++)
                {
                    var clip = new Clip { Name = Encoding.ASCII.GetString(r.ReadBytes(r.ReadByte())), Blend = r.ReadSingle(), Frames = (int)r.ReadUInt32() };
                    clip.Rot = new short[clip.Frames * bones * 4];
                    clip.Pos = new float[clip.Frames * c.Positioned.Length * 3];
                    for (int f = 0; f < clip.Frames; f++)
                    {
                        byte[] raw = r.ReadBytes(bones * 8);
                        System.Buffer.BlockCopy(raw, 0, clip.Rot, f * bones * 8, raw.Length);
                        for (int p = 0; p < c.Positioned.Length * 3; p++)
                        {
                            clip.Pos[f * c.Positioned.Length * 3 + p] = r.ReadSingle();
                        }
                    }
                    c._clips[clip.Name] = clip;
                }
                return c;
            }
        }

        /// <summary>As the sandbox's anim.rs: loops wrap on the last frame, one-shots hold it.</summary>
        public void Sample(Clip clip, float frame, bool looped, ErPose into)
        {
            float last = clip.Frames - 1;
            frame = looped && last > 0f ? Mod(frame, last) : Mathf.Clamp(frame, 0f, last);
            int i = Mathf.Min((int)Mathf.Floor(frame), clip.Frames - 1);
            int j = Mathf.Min(i + 1, clip.Frames - 1);
            float t = frame - i;
            int nb = Bones.Length;
            for (int b = 0; b < nb; b++)
            {
                into.Rot[b] = Quaternion.Slerp(Q(clip.Rot, (i * nb + b) * 4), Q(clip.Rot, (j * nb + b) * 4), t);
            }
            int np = Positioned.Length;
            for (int p = 0; p < np; p++)
            {
                int a = (i * np + p) * 3, z = (j * np + p) * 3;
                into.Pos[Positioned[p]] = Vector3.Lerp(new Vector3(clip.Pos[a], clip.Pos[a + 1], clip.Pos[a + 2]), new Vector3(clip.Pos[z], clip.Pos[z + 1], clip.Pos[z + 2]), t);
            }
        }

        private static Quaternion Q(short[] s, int at)
        {
            const float k = 1f / 32767f;
            var q = new Quaternion(s[at] * k, s[at + 1] * k, s[at + 2] * k, s[at + 3] * k);
            float n = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return n > 1e-6f ? new Quaternion(q.x / n, q.y / n, q.z / n, q.w / n) : Quaternion.identity;
        }

        public static float Mod(float a, float b)
        {
            float r = a % b;
            return r < 0f ? r + b : r;
        }
    }
}
