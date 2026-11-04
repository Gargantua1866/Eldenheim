using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Eldenheim.Anim
{
    /// <summary>
    /// Writes the whole pose onto the player's skeleton after Valheim's
    /// animator has run (LateUpdate), blended over its pose by Weight, which
    /// is how hand-offs fade. The driven bones take the set rotations; the
    /// rest of the body's own skeleton is held at its bind pose.
    ///
    /// It used to be an IAnimationJob in a second PlayableGraph. Binding that
    /// job's stream handles fails outright when the character loads wearing
    /// the Ashlands chest ("could not resolve ... because the avatar is
    /// invalid"), and it leaves the animator unable to write any bone for the
    /// rest of the session, Valheim's own animation included (a frozen body
    /// once Eldenheim lets go); neither destroying the graph nor
    /// Animator.Rebind() recovers it. Writing the bones directly never touches
    /// the animator.
    /// </summary>
    public class PoseOutput
    {
        public readonly List<Transform> Bones;
        public readonly Transform Hips;
        private readonly Quaternion[] _rotations;
        private readonly Vector3[] _bonePositions;
        private readonly List<Transform> _others;
        private readonly Quaternion[] _otherRotations;
        private readonly Vector3[] _otherPositions;
        private Vector3 _hips;
        /// <summary>
        /// Bones from this index on (the hands' attach points) aren't animated by
        /// Valheim: blended from their rest, not from what they last held, and put
        /// back at rest when the pose fades out (else the items stay turned as ER held them).
        /// </summary>
        private readonly int _unanimated;
        private readonly Quaternion[] _rest;
        private bool _atRest = true;

        public PoseOutput(List<Transform> bones, Transform hips, SkinnedMeshRenderer body, int unanimated)
        {
            Bones = bones;
            Hips = hips;
            _unanimated = unanimated;
            _rotations = bones.Select(b => b.localRotation).ToArray();
            _rest = (Quaternion[])_rotations.Clone();
            _bonePositions = bones.Select(b => b.localPosition).ToArray();
            Transform armature = hips.parent;
            var driven = new HashSet<Transform>(bones);
            // Only the body's own skeleton (its bones and their parents) is held
            // at bind pose: the rest under the Armature (held and sheathed items,
            // cloth colliders) is left alone.
            var skeleton = new HashSet<Transform>();
            foreach (Transform bone in body.bones)
            {
                for (Transform t = bone; t != null && t != armature && t.IsChildOf(armature) && skeleton.Add(t); t = t.parent)
                {
                }
            }
            _others = skeleton.Where(t => !driven.Contains(t) && !UnderAttach(t, armature)).ToList();
            Dictionary<Transform, Quaternion> bindLocal = BindLocalRotations(body);
            _otherRotations = _others.Select(t => bindLocal.TryGetValue(t, out Quaternion q) ? q : t.localRotation).ToArray();
            _otherPositions = _others.Select(t => t.localPosition).ToArray();
            _hips = hips.localPosition;
        }

        public float Weight { get; set; }

        public void Set(int bone, Quaternion localRotation) => _rotations[bone] = localRotation;

        public void SetHipsLocal(Vector3 local) => _hips = local;

        /// <summary>The driven bone furthest from what was last set (a check that the writes land).</summary>
        public string WorstBone()
        {
            float worst = 0f;
            string name = "-";
            for (int i = 0; i < Bones.Count; i++)
            {
                float a = Quaternion.Angle(Bones[i].localRotation, _rotations[i]);
                if (a > worst)
                {
                    worst = a;
                    name = Bones[i].name;
                }
            }
            return $"{name} {worst:0}deg";
        }

        /// <summary>After the animator (LateUpdate): the pose onto the bones, over Valheim's by Weight.</summary>
        public void Write()
        {
            float w = Weight;
            if (w <= 0f)
            {
                if (!_atRest)
                {
                    for (int i = _unanimated; i < Bones.Count; i++)
                    {
                        if (Bones[i] != null)
                        {
                            Bones[i].localRotation = _rest[i];
                        }
                    }
                    _atRest = true;
                }
                return;
            }
            _atRest = false;
            for (int i = 0; i < Bones.Count; i++)
            {
                if (Bones[i] != null)
                {
                    Bones[i].localRotation = Quaternion.Slerp(i >= _unanimated ? _rest[i] : Bones[i].localRotation, _rotations[i], w);
                    Bones[i].localPosition = Vector3.Lerp(Bones[i].localPosition, _bonePositions[i], w);
                }
            }
            for (int i = 0; i < _others.Count; i++)
            {
                if (_others[i] != null)
                {
                    _others[i].localRotation = Quaternion.Slerp(_others[i].localRotation, _otherRotations[i], w);
                    _others[i].localPosition = Vector3.Lerp(_others[i].localPosition, _otherPositions[i], w);
                }
            }
            Hips.localPosition = Vector3.Lerp(Hips.localPosition, _hips, w);
        }

        private static bool UnderAttach(Transform t, Transform armature)
        {
            for (Transform p = t.parent; p != null && p != armature; p = p.parent)
            {
                if (p.name.IndexOf("attach", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static Dictionary<Transform, Quaternion> BindLocalRotations(SkinnedMeshRenderer body)
        {
            Matrix4x4[] bind = body.sharedMesh.bindposes;
            var world = new Dictionary<Transform, Quaternion>();
            for (int i = 0; i < body.bones.Length && i < bind.Length; i++)
            {
                if (body.bones[i] != null)
                {
                    world[body.bones[i]] = (body.transform.localToWorldMatrix * bind[i].inverse).rotation;
                }
            }
            var local = new Dictionary<Transform, Quaternion>();
            foreach (KeyValuePair<Transform, Quaternion> kv in world)
            {
                Transform parent = kv.Key.parent;
                Quaternion parentWorld = world.TryGetValue(parent, out Quaternion p) ? p : parent.rotation;
                local[kv.Key] = Quaternion.Inverse(parentWorld) * kv.Value;
            }
            return local;
        }
    }
}
