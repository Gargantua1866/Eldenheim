using System.Collections.Generic;
using UnityEngine;

namespace Eldenheim
{
    /// <summary>
    /// The HitboxDebug view: each capsule the swing sweeps for hits, drawn as a
    /// thick round-capped line (its width the capsule's diameter).
    /// </summary>
    internal static class Hitboxes
    {
        public static readonly Color Idle = new Color(0.2f, 1f, 0.3f, 0.35f);
        public static readonly Color Live = new Color(1f, 0.15f, 0.1f, 0.55f);
        public static readonly Color ErBlade = new Color(1f, 0.85f, 0.1f, 0.55f);
        public static readonly Color Iframes = new Color(0.3f, 0.8f, 1f, 0.3f);

        private static readonly List<LineRenderer> Lines = new List<LineRenderer>();
        private static Material _material;
        private static int _used;

        public static void Begin() => _used = 0;

        public static void Capsule(Vector3 a, Vector3 b, float radius, Color color)
        {
            if (_used == Lines.Count)
            {
                var go = new GameObject("eldenheim-hitbox");
                Object.DontDestroyOnLoad(go);
                LineRenderer made = go.AddComponent<LineRenderer>();
                made.material = _material ?? (_material = new Material(Shader.Find("Sprites/Default")));
                made.positionCount = 2;
                made.numCapVertices = 6;
                made.useWorldSpace = true;
                made.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                made.receiveShadows = false;
                Lines.Add(made);
            }
            LineRenderer line = Lines[_used++];
            line.enabled = true;
            line.SetPosition(0, a);
            line.SetPosition(1, b);
            line.startWidth = line.endWidth = radius * 2f;
            line.startColor = line.endColor = color;
        }

        /// <summary>Hides the capsules not drawn since Begin.</summary>
        public static void End()
        {
            for (int i = _used; i < Lines.Count; i++)
            {
                if (Lines[i] != null)
                {
                    Lines[i].enabled = false;
                }
            }
        }
    }
}
