using Eldenheim.Sim;
using UnityEngine;

namespace Eldenheim
{
    /// <summary>
    /// Sim space ↔ Unity. The sandbox is right-handed with yaw 0 facing +Z
    /// and +X on the character's left; Unity is left-handed with +X on the
    /// right. So X is mirrored and yaw changes sign.
    /// </summary>
    public static class Space
    {
        public static Vector3 ToUnity(Vec3 v) => new Vector3(-v.X, v.Y, v.Z);

        public static Vec3 ToSim(Vector3 u) => new Vec3(-u.x, u.y, u.z);

        public static Vector3 DirToUnity(Vec3 v) => new Vector3(-v.X, v.Y, v.Z);

        public static float YawToUnityDeg(float simYaw) => -simYaw * Mathf.Rad2Deg;

        public static float YawToSim(float unityDeg) => -unityDeg * Mathf.Deg2Rad;
    }

    /// <summary>
    /// The level.rs interface over Valheim's colliders. Height is a sphere
    /// cast down from just above the character, the width of its capsule so a
    /// step is seen as soon as the capsule's edge reaches it. Creatures count
    /// as ground (the player stands on a lox's back), the player's own body not.
    /// </summary>
    public class PhysicsLevel : ILevel
    {
        public static readonly int GroundMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
        private static readonly int CastMask = GroundMask | LayerMask.GetMask("character", "character_noenv");

        /// <summary>Unity height the casts start from: the character's, plus headroom.</summary>
        public float CastFrom;
        public float Radius = 0.3f;
        /// <summary>The player's own body, never ground.</summary>
        public Rigidbody Self;
        /// <summary>Sprinting: as Valheim's running (its wall run), no slope slides the player.</summary>
        public bool NoSlide;
        /// <summary>The moving body under the feet after the last move on foot (a creature, a ship), or null.</summary>
        public Rigidbody GroundBody;
        /// <summary>The ground under the feet after the last move on foot (Valheim's ground contact: footsteps, landings).</summary>
        public Collider GroundCollider;
        public Vector3 GroundNormal = Vector3.up, GroundPoint;
        /// <summary>Valheim's slippage (sliding past 0.5) and whether the ground is past the slide angle (a sprint up it is a wall run).</summary>
        public float Slippage => _slippage;
        public bool OnSteep;

        /// <summary>Valheim's slide angle for players (38°): on steeper ground the player slides down more and more.</summary>
        private static readonly float SlideY = Mathf.Cos(38f * Mathf.Deg2Rad);
        /// <summary>The player's CapsuleCollider radius: its normals are what the capsule meets.</summary>
        private const float BodyRadius = 0.49f;
        /// <summary>
        /// Contacts steeper than this stop the capsule instead of lifting it:
        /// small rounded rocks lift it (~65°), logs, sharp rocks and cliffs don't.
        /// </summary>
        private static readonly float ClimbY = Mathf.Cos(70f * Mathf.Deg2Rad);
        /// <summary>A ladder's rungs (the wood ladder's 0.66 m steps) are walked up whole; the drawn body eases over them.</summary>
        private const float LadderStep = 0.7f;
        private float _slippage;
        /// <summary>How fast the slide fades off steep ground (share per second): about a quarter of a second.</summary>
        private const float SlideFade = 4f;
        private readonly RaycastHit[] _hits = new RaycastHit[16];
        private static readonly System.Collections.Generic.Dictionary<Collider, bool> LadderOf = new System.Collections.Generic.Dictionary<Collider, bool>();

        public float Height(float x, float z) => Ground(x, z, out _, out _);

        /// <summary>
        /// Height(), and the collider and normal there. The capsule is wider
        /// than the cast and meets a ladder's next rung first: a rung it
        /// touches counts as the ground at once, so it is stepped onto.
        /// </summary>
        private float Ground(float x, float z, out Vector3 normal, out Collider collider)
        {
            float y = Cast(x, z, Radius, out normal, out collider);
            float wide = Cast(x, z, BodyRadius, out Vector3 wideNormal, out Collider rung);
            if (wide > y && Ladder(rung))
            {
                normal = wideNormal;
                collider = rung;
                return wide;
            }
            return y;
        }

        /// <summary>
        /// The ground's height under (x, z), its normal as the capsule meets it
        /// (a sweep's normal points from the contact to the sphere's centre, so
        /// an edge, a log or a rock, reads as steep as it feels) and its collider.
        /// </summary>
        private float Cast(float x, float z, float radius, out Vector3 normal, out Collider collider)
        {
            Vector3 at = Space.ToUnity(new Vec3(x, 0f, z));
            at.y = CastFrom;
            int n = Physics.SphereCastNonAlloc(at, radius, Vector3.down, _hits, 80f, CastMask, QueryTriggerInteraction.Ignore);
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                // Distance 0: the sphere started inside it (the player's own capsule, a creature it overlaps).
                if (_hits[i].distance > 0f && (Self == null || _hits[i].rigidbody != Self) && (best < 0 || _hits[i].distance < _hits[best].distance))
                {
                    best = i;
                }
            }
            if (best >= 0)
            {
                normal = _hits[best].normal;
                collider = _hits[best].collider;
                return _hits[best].point.y;
            }
            normal = Vector3.up;
            collider = null;
            return -1000f;
        }

        /// <summary>A ladder or steep stair piece (Valheim hops the player up their rungs with AutoJumpLedge triggers).</summary>
        private static bool Ladder(Collider c)
        {
            if (c == null)
            {
                return false;
            }
            if (!LadderOf.TryGetValue(c, out bool ladder))
            {
                Piece piece = c.GetComponentInParent<Piece>();
                ladder = piece != null && (piece.name.IndexOf("ladder", System.StringComparison.OrdinalIgnoreCase) >= 0
                                           || piece.GetComponentInChildren<AutoJumpLedge>(true) != null);
                LadderOf[c] = ladder;
            }
            return ladder;
        }

        /// <summary>
        /// A steep ramp: the capsule-wide cast sees it rise too far ahead, but
        /// right under the feet it climbs no more than a step per move. Walls
        /// still stop: under the feet they rise at once.
        /// </summary>
        private bool Steep(Vec3 pos, float x, float z)
        {
            float here = Cast(pos.X, pos.Z, 0.05f, out _, out _), there = Cast(x, z, 0.05f, out _, out _);
            return there - here <= Consts.STEP_HEIGHT && there <= pos.Y + Consts.STEP_HEIGHT;
        }

        /// <summary>
        /// On foot, moves are slowed up a slope and sped down it, slide down
        /// ground steeper than 38° as Character.ApplySlide (fully after a
        /// second there; never while sprinting), and climb only what lifts the
        /// capsule: logs, sharp rocks and cliffs stop a walk or a roll; ladders
        /// are walked up rung by rung. In the air the sandbox's step reach and
        /// steep ramps still apply.
        /// </summary>
        public void Slide(ref Vec3 pos, Vec3 delta)
        {
            float underY = Ground(pos.X, pos.Z, out Vector3 underNormal, out Collider under);
            bool onFoot = pos.Y - underY <= 0.05f;
            Cast(pos.X, pos.Z, BodyRadius, out Vector3 ground, out _);
            GroundBody = null;
            if (onFoot)
            {
                Rigidbody body = under != null ? under.attachedRigidbody : null;
                GroundBody = body != null && !body.isKinematic ? body : null;
                GroundCollider = under;
                GroundNormal = underNormal;
                GroundPoint = Space.ToUnity(new Vec3(pos.X, underY, pos.Z));
                OnSteep = ground.y < SlideY;
                delta *= SlopeFactor(ground, delta);
                bool moving = delta.X * delta.X + delta.Z * delta.Z > 1e-8f;
                // Off steep ground the slide fades out (Valheim drops it at once, but its
                // body's momentum carries on): on bumpy ground either side of 38° the speed
                // down a slope jumped between the slide's and the walk's.
                _slippage = NoSlide && moving ? 0f
                    : ground.y < SlideY ? Mathf.MoveTowards(_slippage, 1f, Consts.DT) : Mathf.MoveTowards(_slippage, 0f, SlideFade * Consts.DT);
                if (_slippage > 0f)
                {
                    // Valheim's slide: 5 m/s along Cross(n, Cross(n, up)), the way down the slope.
                    Vec3 down = Space.ToSim(Vector3.Cross(ground, Vector3.Cross(ground, Vector3.up)) * (5f * Consts.DT));
                    delta.X += (down.X - delta.X) * _slippage;
                    delta.Z += (down.Z - delta.Z) * _slippage;
                }
            }
            float x = pos.X + delta.X, z = pos.Z + delta.Z;
            if (CanEnter(pos, x, z, onFoot))
            {
                pos.X = x;
                pos.Z = z;
            }
            else if (CanEnter(pos, x, pos.Z, onFoot))
            {
                pos.X = x;
            }
            else if (CanEnter(pos, pos.X, z, onFoot))
            {
                pos.Z = z;
            }
        }

        private bool CanEnter(Vec3 pos, float x, float z, bool onFoot)
        {
            float rise = Ground(x, z, out _, out Collider hit) - pos.Y;
            if (!onFoot)
            {
                return rise <= Consts.STEP_HEIGHT || Steep(pos, x, z);
            }
            if (rise <= 0.02f)
            {
                return true;
            }
            if (Ladder(hit))
            {
                return rise <= LadderStep;
            }
            Cast(x, z, BodyRadius, out Vector3 normal, out _);
            return rise <= Consts.STEP_HEIGHT && normal.y >= ClimbY;
        }

        /// <summary>
        /// Speed along ground of normal n: half the slope's sine slower uphill
        /// (0.69 at 38°), a quarter of it faster downhill.
        /// </summary>
        internal static float SlopeFactor(Vector3 n, Vec3 delta)
        {
            Vector3 dir = Space.DirToUnity(delta);
            dir.y = 0f;
            float len = dir.magnitude;
            if (len < 1e-5f)
            {
                return 1f;
            }
            float grade = -(n.x * dir.x + n.z * dir.z) / (len * Mathf.Max(n.y, 0.1f));
            float sin = grade / Mathf.Sqrt(1f + grade * grade);
            return sin > 0f ? 1f - 0.5f * sin : 1f - 0.25f * sin;
        }
    }
}
