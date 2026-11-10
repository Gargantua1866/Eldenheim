using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Eldenheim.Anim
{
    /// <summary>
    /// An ER pose onto Valheim's player rig (spike 0b, measured at 0.3–3°).
    /// Per mapped bone, in character space, with rest = each rig's bind pose
    /// and A turning the ER bone's rest direction onto Valheim's:
    ///
    ///     target = er(t) · er_rest⁻¹ · A⁻¹ · valheim_rest
    ///
    /// Only rotations are written (bone lengths stay Valheim's, so nothing
    /// stretches), plus the hips' height.
    /// </summary>
    public class Retarget
    {
        private static readonly List<string[]> Map = BuildMap();

        private static List<string[]> BuildMap()
        {
            var map = new List<string[]>
            {
                new[] { "Pelvis", "Hips", "Spine", "Spine" },
                new[] { "Spine", "Spine", "Spine1", "Spine1" },
                new[] { "Spine1", "Spine1", "Spine2", "Spine2" },
                new[] { "Spine2", "Spine2", "Neck", "Neck" },
                new[] { "Neck", "Neck", "Head", "Head" },
                new[] { "Head", "Head", null, null },
            };
            foreach (var (er, vh) in new[] { ("L_", "Left"), ("R_", "Right") })
            {
                map.Add(new[] { er + "Clavicle", vh + "Shoulder", er + "UpperArm", vh + "Arm" });
                map.Add(new[] { er + "UpperArm", vh + "Arm", er + "Forearm", vh + "ForeArm" });
                map.Add(new[] { er + "Forearm", vh + "ForeArm", er + "Hand", vh + "Hand" });
                map.Add(new[] { er + "Hand", vh + "Hand", er + "Finger2", vh + "HandMiddle1" });
                foreach (var (f, name) in new[] { ("0", "Thumb"), ("1", "Index"), ("2", "Middle"), ("3", "Ring"), ("4", "Pinky") })
                {
                    // ER names a finger's three bones FingerN, FingerN1, FingerN2.
                    string a = er + "Finger" + f, b = a + "1", c = a + "2";
                    map.Add(new[] { a, vh + "Hand" + name + "1", b, vh + "Hand" + name + "2" });
                    map.Add(new[] { b, vh + "Hand" + name + "2", c, vh + "Hand" + name + "3" });
                    map.Add(new[] { c, vh + "Hand" + name + "3", null, null });
                }
            }
            foreach (var (er, vh) in new[] { ("L_", "Left"), ("R_", "Right") })
            {
                map.Add(new[] { er + "Thigh", vh + "UpLeg", er + "Calf", vh + "Leg" });
                map.Add(new[] { er + "Calf", vh + "Leg", er + "Foot", vh + "Foot" });
                map.Add(new[] { er + "Foot", vh + "Foot", er + "Toe0", vh + "ToeBase" });
                map.Add(new[] { er + "Toe0", vh + "ToeBase", null, null });
            }
            return map;
        }

        public readonly List<Transform> Bones = new List<Transform>();
        public Transform Hips;
        public Transform RightAttach, LeftAttach;

        private readonly List<int> _er = new List<int>();
        private readonly List<Quaternion> _align = new List<Quaternion>();
        private readonly Dictionary<Transform, Quaternion> _restModel = new Dictionary<Transform, Quaternion>();
        private readonly Dictionary<Transform, Vector3> _restPos = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<Transform, Quaternion> _model = new Dictionary<Transform, Quaternion>();
        private readonly Clips _clips;
        private readonly Transform _root;
        private readonly int _erPelvis, _erRoot, _erRWeapon, _erLWeapon, _erLHand;
        private readonly float _scale = 1f;
        private Quaternion _rightAttachLocal = Quaternion.identity, _leftAttachLocal = Quaternion.identity;
        private Vector3 _rightAttachOffset, _leftAttachOffset;

        public string Report { get; }

        public Retarget(Clips clips, Transform playerRoot, SkinnedMeshRenderer body)
        {
            _clips = clips;
            _root = playerRoot;
            _erPelvis = clips.Bone("Pelvis");
            _erRoot = clips.Bone("RootPos");
            _erRWeapon = clips.Bone("R_Weapon");

            Matrix4x4[] bind = body.sharedMesh.bindposes;
            Matrix4x4 toRoot = playerRoot.worldToLocalMatrix * body.transform.localToWorldMatrix;
            var byName = new Dictionary<string, Transform>();
            for (int i = 0; i < body.bones.Length && i < bind.Length; i++)
            {
                Transform t = body.bones[i];
                if (t == null)
                {
                    continue;
                }
                Matrix4x4 m = toRoot * bind[i].inverse;
                _restModel[t] = m.rotation;
                _restPos[t] = m.GetColumn(3);
                byName[t.name] = t;
            }

            var report = new StringBuilder();
            Quaternion lastAlign = Quaternion.identity;
            foreach (string[] row in Map)
            {
                int er = clips.Bone(row[0]);
                if (er < 0 || !byName.TryGetValue(row[1], out Transform vh))
                {
                    report.Append($" skip {row[0]}->{row[1]};");
                    continue;
                }
                int erChild = row[2] == null ? -1 : clips.Bone(row[2]);
                Transform vhChild = row[3] != null && byName.TryGetValue(row[3], out Transform c) ? c : null;
                Quaternion align = lastAlign;
                if (erChild >= 0 && vhChild != null)
                {
                    align = Quaternion.FromToRotation(clips.Rest.Pos[erChild] - clips.Rest.Pos[er], _restPos[vhChild] - _restPos[vh]);
                }
                else if (row[0] == "Head")
                {
                    // Both rest heads face forward. The neck's correction (25°,
                    // the rigs' necks lean differently) would tip the face up.
                    align = Quaternion.identity;
                }
                lastAlign = align;
                Bones.Add(vh);
                _er.Add(er);
                _align.Add(align);
                if (row[1] == "Hips")
                {
                    Hips = vh;
                }
            }
            if (Hips != null && _erPelvis >= 0)
            {
                _scale = _restPos[Hips].y / clips.Rest.Pos[_erPelvis].y;
            }
            Transform rightHand = byName.TryGetValue("RightHand", out Transform rh) ? rh : null;
            Transform leftHand = byName.TryGetValue("LeftHand", out Transform lh) ? lh : null;
            RightAttach = rightHand != null ? rightHand.Find("RightHand_Attach") : null;
            LeftAttach = leftHand != null ? leftHand.Find("LeftHand_Attach") : null;
            if (RightAttach != null)
            {
                _rightAttachLocal = RightAttach.localRotation;
                // In the hand's own frame, in metres (what _model turns into character space).
                _rightAttachOffset = Quaternion.Inverse(rightHand.rotation) * (RightAttach.position - rightHand.position);
            }
            if (LeftAttach != null)
            {
                _leftAttachLocal = LeftAttach.localRotation;
                _leftAttachOffset = Quaternion.Inverse(leftHand.rotation) * (LeftAttach.position - leftHand.position);
            }
            _erLWeapon = clips.Bone("L_Weapon");
            _erLHand = clips.Bone("L_Hand");
            Report = $"mapped {Bones.Count}/{Map.Count} bones, hips scale {_scale:0.000}, attach R={RightAttach != null} L={LeftAttach != null}{report}";
        }

        /// <summary>Local rotations for <see cref="Bones"/>, and the hips' character-space position.</summary>
        public void Solve(ErPose er, Quaternion[] local, out Vector3 hips)
        {
            _model.Clear();
            for (int i = 0; i < Bones.Count; i++)
            {
                int e = _er[i];
                Quaternion target = er.Rot[e] * Quaternion.Inverse(_clips.Rest.Rot[e]) * Quaternion.Inverse(_align[i]) * _restModel[Bones[i]];
                _model[Bones[i]] = target;
                local[i] = Quaternion.Inverse(ParentModel(Bones[i])) * target;
            }
            Vector3 pelvis = er.Pos[_erPelvis];
            Vector3 root = _erRoot >= 0 ? er.Pos[_erRoot] : Vector3.zero;
            hips = new Vector3(pelvis.x - root.x, pelvis.y, pelvis.z - root.z) * _scale;
        }

        /// <summary>
        /// The right-hand attach point's local rotation that lays the held
        /// item's blade (bladeLocal, in the attach's space) along the ER weapon
        /// bone's Y axis, keeping the roll the hand gives it. Call after Solve.
        /// </summary>
        public Quaternion RightAttachRotation(ErPose er, Vector3 bladeLocal) => AttachRotation(er, RightAttach, _rightAttachLocal, _erRWeapon, bladeLocal, Vector3.up);

        /// <summary>
        /// The same for the left hand: the item's axis (bladeLocal, in the
        /// attach's space) laid along erAxis of ER's L_Weapon. The left
        /// weapon bone is the right one mirrored, so a blade runs along its
        /// -Y (rig.rs turns left-hand models half a turn about X); a shield's
        /// face is along its Z.
        /// </summary>
        public Quaternion LeftAttachRotation(ErPose er, Vector3 bladeLocal, Vector3 erAxis) => AttachRotation(er, LeftAttach, _leftAttachLocal, _erLWeapon, bladeLocal, erAxis);

        /// <summary>Where ER's left weapon bone points erAxis, in character space.</summary>
        public Vector3 LeftWeaponAxis(ErPose er, Vector3 erAxis) => _erLWeapon >= 0 ? er.Rot[_erLWeapon] * erAxis : Vector3.zero;

        private Quaternion AttachRotation(ErPose er, Transform attachPoint, Quaternion rest, int erWeapon, Vector3 bladeLocal, Vector3 erAxis)
        {
            if (attachPoint == null || erWeapon < 0 || bladeLocal == Vector3.zero)
            {
                return rest;
            }
            Quaternion hand = _model.TryGetValue(attachPoint.parent, out Quaternion h) ? h : Quaternion.identity;
            Quaternion attach = hand * rest;
            Vector3 have = attach * bladeLocal;
            Vector3 want = er.Rot[erWeapon] * erAxis;
            Quaternion fixedAttach = Quaternion.FromToRotation(have, want) * attach;
            return Quaternion.Inverse(hand) * fixedAttach;
        }

        public Quaternion RightAttachRest => _rightAttachLocal;
        public Quaternion LeftAttachRest => _leftAttachLocal;

        // --- The free hand on a two-handed grip ------------------------------------

        private const float GRIP_NEAR = 0.12f, GRIP_FAR = 0.3f;
        private int[] _leftArm;
        /// <summary>Where the last grip put the left wrist (character space), for checks.</summary>
        public Vector3 GripTarget, GripAttach;
        public float GripErOff;

        /// <summary>
        /// Two-handed, ER puts the left hand on the weapon. Rotations alone put
        /// it wherever Valheim's arm lengths take it, off the handle. This
        /// keeps the left hand where ER has it relative to the right weapon
        /// bone (bending the elbow as the animation did), fading out as ER's
        /// own hand leaves the handle. After Solve and PlaceFeet.
        /// </summary>
        /// <summary>Where along the handle (ER metres from the right hand) the left hand last held it.</summary>
        private float _heldAlong = -0.25f;

        /// <summary>
        /// hold (0..1): keep the left hand on the handle even where ER's animation
        /// lets go (the jumps are shared by all weapons, one hand free), where it
        /// last held it; between 0 and 1, part of the way there.
        /// </summary>
        public float GripLeftHand(ErPose er, Quaternion[] local, Vector3 hips, float hold = 0f, float guardAlong = 0f, float guard = 0f)
        {
            if (_leftArm == null)
            {
                _leftArm = new[] { "LeftArm", "LeftForeArm", "LeftHand" }.Select(n => Bones.FindIndex(b => b.name == n)).ToArray();
            }
            if (RightAttach == null || _erLHand < 0 || _erRWeapon < 0 || _leftArm.Any(i => i < 0))
            {
                return 0f;
            }
            // How far ER's left hand is from the weapon's handle line.
            Vector3 grip = er.Pos[_erRWeapon], axis = er.Rot[_erRWeapon] * Vector3.up;
            Vector3 toHand = er.Pos[_erLHand] - grip;
            float off = (toHand - axis * Vector3.Dot(toHand, axis)).magnitude;
            GripErOff = off;
            float weight = 1f - Mathf.Clamp01((off - GRIP_NEAR) / (GRIP_FAR - GRIP_NEAR));
            // Guarding (two-handed, or EmptyHandGuard's): the left hand goes where the
            // guard clip itself has it on the weapon line, as the guard layer comes in.
            if (guard > 0f)
            {
                toHand = Vector3.Lerp(toHand, axis * guardAlong, guard);
                weight = Mathf.Lerp(weight, 1f, guard);
            }
            else if (weight >= 1f)
            {
                _heldAlong = Vector3.Dot(toHand, axis);
            }
            else if (hold > 0f)
            {
                toHand = Vector3.Lerp(toHand, axis * _heldAlong, hold);
                weight = Mathf.Lerp(weight, 1f, hold);
            }
            if (weight <= 0f)
            {
                return 0f;
            }
            Transform rightHand = RightAttach.parent;
            Vector3 attach = CharPos(rightHand, hips) + _model[rightHand] * _rightAttachOffset;
            GripAttach = attach;
            Vector3 target = attach + toHand * _scale;
            Transform up = Bones[_leftArm[0]], mid = Bones[_leftArm[1]], end = Bones[_leftArm[2]];
            if (LeftAttach != null)
            {
                // ER's wrist-to-handle offset belongs to ER's hand: put the left
                // hand's own grip point (its attach point) on the handle line,
                // at ER's distance along it.
                Vector3 palm = target + _model[end] * _leftAttachOffset;
                Vector3 onLine = attach + axis * Vector3.Dot(palm - attach, axis);
                target += onLine - palm;
            }

            Vector3 s = CharPos(up, hips), e = s + _model[up] * Offset(up, mid), w = e + _model[mid] * Offset(mid, end);
            target = Vector3.Lerp(w, target, weight);
            GripTarget = target;
            float upper = (e - s).magnitude, lower = (w - e).magnitude;
            Vector3 toTarget = target - s;
            float reach = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upper - lower) + 1e-3f, upper + lower - 1e-3f);
            Vector3 along = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : Vector3.down;
            // Keep the elbow out the way the animation had it.
            Vector3 line = (w - s).sqrMagnitude > 1e-8f ? (w - s).normalized : Vector3.down;
            Vector3 hint = (e - s) - line * Vector3.Dot(e - s, line) + Vector3.down * 0.02f;
            Vector3 bend = hint - along * Vector3.Dot(hint, along);
            bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.down;
            float x = (upper * upper - lower * lower + reach * reach) / (2f * reach);
            Vector3 newElbow = s + along * x + bend * Mathf.Sqrt(Mathf.Max(upper * upper - x * x, 0f));
            Vector3 newWrist = s + along * reach;

            Quaternion upperTurn = Quaternion.FromToRotation(e - s, newElbow - s);
            Quaternion upperModel = upperTurn * _model[up];
            Quaternion lowerModel = Quaternion.FromToRotation(upperTurn * (w - e), newWrist - newElbow) * upperTurn * _model[mid];
            Quaternion handModel = _model[end];
            local[_leftArm[0]] = Quaternion.Inverse(ParentModel(up)) * upperModel;
            local[_leftArm[1]] = Quaternion.Inverse(upperModel) * lowerModel;
            local[_leftArm[2]] = Quaternion.Inverse(lowerModel) * handModel;
            _model[up] = upperModel;
            _model[mid] = lowerModel;
            return weight;
        }

        private int[][] _fingerPairs;

        /// <summary>
        /// Closes the left hand round the handle: ER leaves the off hand's
        /// fingers to a grip layer the sandbox doesn't have, so the right
        /// hand's grip (its fingers' local rotations; the rig's left and right
        /// bone frames are mirror images) is copied over, by weight.
        /// </summary>
        public void CloseLeftHand(Quaternion[] local, float weight)
        {
            if (_fingerPairs == null)
            {
                var pairs = new List<int[]>();
                for (int i = 0; i < Bones.Count; i++)
                {
                    string n = Bones[i].name;
                    if (n.StartsWith("LeftHand") && n != "LeftHand")
                    {
                        int r = Bones.FindIndex(b => b.name == "Right" + n.Substring(4));
                        if (r >= 0)
                        {
                            pairs.Add(new[] { i, r });
                        }
                    }
                }
                _fingerPairs = pairs.ToArray();
            }
            foreach (int[] p in _fingerPairs)
            {
                local[p[0]] = Quaternion.Slerp(local[p[0]], local[p[1]], weight);
            }
        }

        // --- Arms out of the torso -------------------------------------------------
        //
        // ER's arms are posed for ER's slimmer body: on Valheim's (and its
        // armour) some moves cross the forearms through the chest (two-handed
        // halberd heavies, where ER's left hand slides down the shaft to the
        // right hip). After the pose, the upper arm and forearm are kept outside
        // an oval round the spine (half-widths in the chest's frame), the arm
        // re-solved to reach the pushed wrist with its elbow pushed out too.
        // Points along each bone are tested, not just the joints: a forearm
        // across the belly has both ends outside.
        //
        // The spine runs near the back: Valheim's body mesh (bind pose, male and
        // female) reaches 0.17-0.24 m in front of the spine line, 0.14-0.20 m
        // behind and ~0.21 m to each side. The oval reaches 0.22 m forward (the
        // bare body plus chest armour) and 0.15 m back (it was 0.14 both ways,
        // so arms low across the belly, as pole movesets hold them, sank in);
        // its width (0.22 m) matches the body's: the probe command, which
        // measures arms against the drawn body and armour meshes, found
        // forearms crossing the ribs inside a narrower one, and arms hanging
        // at the sides (idle, run, sprint) still don't touch it. The hand is tested too: a hand on a shaft
        // across the belly reaches ~0.1 m past the wrist, its thumb
        // ~3.5 cm to the side.

        private const float TORSO_WIDE = 0.22f, TORSO_FRONT = 0.22f, TORSO_BACK = 0.15f;
        private const float HAND_REACH = 0.1f, HAND_PAD = 0.035f;
        /// <summary>Over this far above the neck the oval closes to nothing: there is no torso up there.</summary>
        private const float TORSO_CLOSE = 0.15f;
        private const float TORSO_DEEP = (TORSO_FRONT + TORSO_BACK) / 2f, TORSO_SHIFT = (TORSO_FRONT - TORSO_BACK) / 2f;
        private int[][] _arms;
        private Transform _spineLow, _neck, _chestBone;

        /// <summary>The deepest an elbow or wrist went into the torso on the last pass (m; a check).</summary>
        public float ArmDepth;

        /// <summary>Keeps one arm (0 right, 1 left) out of the torso. How far the wrist moved (m).</summary>
        public float KeepArmOut(int side, Quaternion[] local, Vector3 hips)
        {
            if (_arms == null)
            {
                _arms = new int[2][];
                string[][] names = { new[] { "RightArm", "RightForeArm", "RightHand" }, new[] { "LeftArm", "LeftForeArm", "LeftHand" } };
                for (int s = 0; s < 2; s++)
                {
                    _arms[s] = names[s].Select(n => Bones.FindIndex(b => b.name == n)).ToArray();
                }
                _spineLow = Bones.Find(b => b.name == "Spine");
                _neck = Bones.Find(b => b.name == "Neck");
                _chestBone = Bones.Find(b => b.name == "Spine2");
            }
            int[] arm = _arms[side];
            if (arm.Any(i => i < 0) || _spineLow == null || _neck == null || _chestBone == null)
            {
                return 0f;
            }
            Transform up = Bones[arm[0]], mid = Bones[arm[1]], end = Bones[arm[2]];
            Transform knuckle = end.Find(side == 0 ? "RightHandMiddle1" : "LeftHandMiddle1");
            Vector3 low = CharPos(_spineLow, hips), high = CharPos(_neck, hips);
            Quaternion chest = _model.TryGetValue(_chestBone, out Quaternion c) ? c : Quaternion.identity;
            // Each push re-bends the arm from where the last left it: from deep
            // inside one push rarely clears every point (the probe command found
            // hands 10 cm into the belly after a single pass).
            Vector3 start = Vector3.zero, moved = Vector3.zero;
            for (int pass = 0; pass < 4; pass++)
            {
                Vector3 s0 = CharPos(up, hips), e = s0 + _model[up] * Offset(up, mid), w = e + _model[mid] * Offset(mid, end);
                if (pass == 0)
                {
                    start = w;
                }
                // The deepest point of each bone: the upper arm's pushes the elbow
                // (scaled up, as it pivots on the shoulder), the forearm's moves
                // the forearm out whole.
                Vector3 pushE = Vector3.zero, pushW = Vector3.zero;
                float deepUpper = 0f, deepFore = 0f;
                for (int k = 1; k <= 4; k++)
                {
                    float t = k / 4f;
                    float d = OutOfTorso(Vector3.Lerp(s0, e, t), low, high, chest, 0f, out Vector3 surface);
                    if (d > deepUpper)
                    {
                        deepUpper = d;
                        pushE = (surface - Vector3.Lerp(s0, e, t)) / t;
                    }
                    d = OutOfTorso(Vector3.Lerp(e, w, t), low, high, chest, 0f, out surface);
                    if (d > deepFore)
                    {
                        deepFore = d;
                        pushW = surface - Vector3.Lerp(e, w, t);
                    }
                }
                // The hand, along its own bones to the knuckles, padded by its
                // half-thickness (the thumb sits beside that line): pushing it
                // out moves the wrist.
                Vector3 toKnuckle = knuckle != null ? _model[end] * Offset(end, knuckle) : (w - e).normalized * HAND_REACH;
                for (int k = 1; k <= 2; k++)
                {
                    Vector3 h = w + toKnuckle * k / 2f;
                    float d = OutOfTorso(h, low, high, chest, HAND_PAD, out Vector3 surface);
                    if (d > deepFore)
                    {
                        deepFore = d;
                        pushW = surface - h;
                    }
                }
                float deepest = Mathf.Max(deepUpper, deepFore);
                if (pass == 0)
                {
                    ArmDepth = Mathf.Max(ArmDepth, deepest);
                }
                if (deepest <= 0.002f)
                {
                    break;
                }
                Vector3 eOut = e + (pushE.sqrMagnitude > pushW.sqrMagnitude ? pushE : pushW);
                Vector3 wOut = w + pushW + (deepFore > 0f ? Vector3.zero : pushE);
                TwoBone(arm, s0, e, w, wOut, eOut - s0, local);
                moved = CharPos(up, hips) + _model[up] * Offset(up, mid) + _model[mid] * Offset(mid, end) - start;
            }
            return moved.magnitude;
        }

        /// <summary>How far p is inside the torso oval (0: outside), and where it would sit on its surface.</summary>
        private static float OutOfTorso(Vector3 p, Vector3 low, Vector3 high, Quaternion chest, float pad, out Vector3 surface)
        {
            Vector3 axis = high - low;
            float t = axis.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - low, axis) / axis.sqrMagnitude) : 0f;
            Vector3 centre = low + axis * t;
            // Hands raised overhead, even both on one handle above the head, are not in the torso.
            float over = axis.sqrMagnitude > 1e-8f ? Vector3.Dot(p - high, axis.normalized) : 0f;
            float close = Mathf.Clamp01(1f - over / TORSO_CLOSE);
            if (close <= 0f)
            {
                surface = p;
                return 0f;
            }
            // The oval's middle sits in front of the spine (chest +z is forward).
            float shift = TORSO_SHIFT * close;
            Vector3 v = Quaternion.Inverse(chest) * (p - centre) - new Vector3(0f, 0f, shift);
            float along = v.y;
            v.y = 0f;
            // Straight through the middle: out the front.
            Vector3 dir = v.sqrMagnitude > 1e-8f ? v : Vector3.forward * 1e-3f;
            float wide = (TORSO_WIDE + pad) * close, deep = (TORSO_DEEP + pad) * close;
            float q = Mathf.Sqrt(dir.x * dir.x / (wide * wide) + dir.z * dir.z / (deep * deep));
            Vector3 onSurface = dir / q;
            surface = centre + chest * new Vector3(onSurface.x, along, onSurface.z + shift);
            return q < 1f ? (onSurface - v).magnitude : 0f;
        }

        /// <summary>
        /// The arm (shoulder s, elbow e, wrist w) re-bent to put the wrist on
        /// target, its elbow towards bendHint; the hand keeps its rotation.
        /// </summary>
        private void TwoBone(int[] arm, Vector3 s, Vector3 e, Vector3 w, Vector3 target, Vector3 bendHint, Quaternion[] local)
        {
            Transform up = Bones[arm[0]], mid = Bones[arm[1]], end = Bones[arm[2]];
            float upper = (e - s).magnitude, lower = (w - e).magnitude;
            Vector3 toTarget = target - s;
            float reach = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(upper - lower) + 1e-3f, upper + lower - 1e-3f);
            Vector3 along = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : Vector3.down;
            Vector3 bend = bendHint - along * Vector3.Dot(bendHint, along);
            bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.down;
            float x = (upper * upper - lower * lower + reach * reach) / (2f * reach);
            Vector3 newElbow = s + along * x + bend * Mathf.Sqrt(Mathf.Max(upper * upper - x * x, 0f));
            Vector3 newWrist = s + along * reach;
            Quaternion upperTurn = Quaternion.FromToRotation(e - s, newElbow - s);
            Quaternion upperModel = upperTurn * _model[up];
            Quaternion lowerModel = Quaternion.FromToRotation(upperTurn * (w - e), newWrist - newElbow) * upperTurn * _model[mid];
            Quaternion handModel = _model[end];
            local[arm[0]] = Quaternion.Inverse(ParentModel(up)) * upperModel;
            local[arm[1]] = Quaternion.Inverse(upperModel) * lowerModel;
            local[arm[2]] = Quaternion.Inverse(lowerModel) * handModel;
            _model[up] = upperModel;
            _model[mid] = lowerModel;
        }

        /// <summary>A mapped bone's character-space position, from the solved rotations and the rest bone lengths.</summary>
        private Vector3 CharPos(Transform bone, Vector3 hips)
        {
            if (bone == Hips || bone.parent == null || bone.parent == _root)
            {
                return hips;
            }
            Transform p = bone.parent;
            return CharPos(p, hips) + ParentModel(bone) * Offset(p, bone);
        }

        // --- Feet on the ground (rig.rs's foot placement) -------------------------

        private const float FOOT_REACH = 0.5f;
        private const float FOOT_SETTLE = 24f;
        /// <summary>How fast the hips' drop follows the lower foot down and back up (per second).</summary>
        private const float SINK_SETTLE = 8f, SINK_RISE = 1.5f;
        /// <summary>
        /// How far a foot is lifted onto ground above it (m): the whole reach standing, less on the
        /// move (Controller), so running down a slope the trailing foot keeps its stride and slides
        /// a little, as Valheim's do, instead of being hauled up onto the slope behind.
        /// </summary>
        public float LiftReach = FOOT_REACH;
        private float _sink;
        private readonly float[] _footLift = new float[2];
        /// <summary>Test harness: the last hips drop and each foot's lift (m).</summary>
        public float LastSink;
        public float[] FootLift => _footLift;
        private int[][] _legs;

        /// <summary>
        /// Puts each foot on the ground under it, after Solve: sinks the body as
        /// far as the lower foot needs, then lifts the other foot and re-bends
        /// its knee in the plane the animation had it. ground(x, z) gives the
        /// ground's height (character space) under a character-space point, or
        /// null. Only rotations change, so the legs keep their length.
        /// </summary>
        public void PlaceFeet(Quaternion[] local, ref Vector3 hips, System.Func<Vector3, float?> ground, bool planted, float dt)
        {
            if (_legs == null)
            {
                _legs = new int[2][];
                string[][] names = { new[] { "LeftUpLeg", "LeftLeg", "LeftFoot" }, new[] { "RightUpLeg", "RightLeg", "RightFoot" } };
                for (int s = 0; s < 2; s++)
                {
                    _legs[s] = new int[3];
                    for (int j = 0; j < 3; j++)
                    {
                        _legs[s][j] = Bones.FindIndex(b => b.name == names[s][j]);
                    }
                }
            }
            if (_legs[0][0] < 0 || _legs[1][0] < 0 || Hips == null)
            {
                return;
            }
            Quaternion hipsModel = _model[Hips];
            var hip = new Vector3[2];
            var knee = new Vector3[2];
            var foot = new Vector3[2];
            for (int s = 0; s < 2; s++)
            {
                Transform up = Bones[_legs[s][0]], mid = Bones[_legs[s][1]], end = Bones[_legs[s][2]];
                hip[s] = hips + hipsModel * Offset(Hips, up);
                knee[s] = hip[s] + _model[up] * Offset(up, mid);
                foot[s] = knee[s] + _model[mid] * Offset(mid, end);
                // No time passed (or an extra physics step's pose): the feet stay as they are, without the raycast.
                if (dt > 0f)
                {
                    float? g = ground(foot[s]);
                    // Ground beyond reach: as far as the foot goes, not back to the clip's (a snap
                    // at the limit, every stride on steep slopes).
                    float wanted = planted && g.HasValue ? Mathf.Clamp(g.Value, -FOOT_REACH, LiftReach) : 0f;
                    _footLift[s] += (wanted - _footLift[s]) * (1f - Mathf.Exp(-FOOT_SETTLE * dt));
                }
            }
            // The hips drop to the lower foot's ground, smoothed over about a step: on a
            // slope the lower foot swaps every step (behind, then ahead) and the whole body
            // bobbed with it, up to 0.3 m sprinting up a 56° slope.
            float lowest = Mathf.Min(Mathf.Min(_footLift[0], _footLift[1]), 0f);
            if (dt > 0f)
            {
                // Down quickly (no foot left short of the ground), back up slowly: the hips ride
                // the strides' low points instead of rising and falling with each one.
                _sink += (lowest - _sink) * (1f - Mathf.Exp(-(lowest < _sink ? SINK_SETTLE : SINK_RISE) * dt));
            }
            float sink = planted ? Mathf.Min(_sink, 0f) : (_sink = 0f);
            LastSink = sink;
            hips.y += sink;
            for (int s = 0; s < 2; s++)
            {
                float lift = _footLift[s] - sink;
                if (Mathf.Abs(lift) < 1e-4f)
                {
                    continue;
                }
                Transform up = Bones[_legs[s][0]], mid = Bones[_legs[s][1]], end = Bones[_legs[s][2]];
                Vector3 h = hip[s] + Vector3.up * sink, k = knee[s] + Vector3.up * sink, f = foot[s] + Vector3.up * sink;
                float thigh = (k - h).magnitude, shin = (f - k).magnitude;
                Vector3 target = f + Vector3.up * lift;
                Vector3 toTarget = target - h;
                float reach = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(thigh - shin) + 1e-3f, thigh + shin - 1e-3f);
                Vector3 along = toTarget.sqrMagnitude > 1e-8f ? toTarget.normalized : Vector3.down;
                // Keep the knee bending the way the animation had it, biased forward.
                Vector3 axis = (f - h).sqrMagnitude > 1e-8f ? (f - h).normalized : Vector3.down;
                Vector3 animated = (k - h) - axis * Vector3.Dot(k - h, axis);
                Vector3 hint = animated + Vector3.forward * 0.08f;
                Vector3 bend = hint - along * Vector3.Dot(hint, along);
                bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.forward;
                float x = (thigh * thigh - shin * shin + reach * reach) / (2f * reach);
                Vector3 newKnee = h + along * x + bend * Mathf.Sqrt(Mathf.Max(thigh * thigh - x * x, 0f));
                Vector3 newFoot = h + along * reach;

                Quaternion thighTurn = Quaternion.FromToRotation(k - h, newKnee - h);
                Quaternion thighModel = thighTurn * _model[up];
                Quaternion calfModel = Quaternion.FromToRotation(thighTurn * (f - k), newFoot - newKnee) * thighTurn * _model[mid];
                Quaternion footModel = _model[end];
                local[_legs[s][0]] = Quaternion.Inverse(hipsModel) * thighModel;
                local[_legs[s][1]] = Quaternion.Inverse(thighModel) * calfModel;
                local[_legs[s][2]] = Quaternion.Inverse(calfModel) * footModel;
            }
        }

        /// <summary>A child's offset from its parent, in the parent's own (rest) frame, in metres.</summary>
        private Vector3 Offset(Transform parent, Transform child) =>
            Quaternion.Inverse(_restModel[parent]) * (_restPos[child] - _restPos[parent]);

        private Quaternion ParentModel(Transform bone)
        {
            Transform p = bone.parent;
            if (p == null || p == _root)
            {
                return Quaternion.identity;
            }
            if (_model.TryGetValue(p, out Quaternion solved))
            {
                return solved;
            }
            if (_restModel.TryGetValue(p, out Quaternion rest) && p.parent != null && _restModel.TryGetValue(p.parent, out Quaternion parentRest))
            {
                Quaternion model = ParentModel(p) * (Quaternion.Inverse(parentRest) * rest);
                _model[p] = model;
                return model;
            }
            return Quaternion.Inverse(_root.rotation) * p.rotation;
        }
    }
}
