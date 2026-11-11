using Eldenheim.Sim;
using UnityEngine;
using SimPlayer = Eldenheim.Sim.Player;
using VPlayer = global::Player;

namespace Eldenheim.Anim
{
    /// <summary>
    /// The sandbox's rig.rs, minus the drawing: which clip the sim's state
    /// shows, cross-faded over the clip's authored blend, with the arm layers
    /// (weapon carry, guard, grip change) and the hands closed on the weapon.
    /// The sandbox works in joint positions; this works in each bone's
    /// character-space rotation, which is what the retarget takes.
    /// </summary>
    public class Chooser
    {
        /// <summary>The one-handed stance of spears and halberds.</summary>
        private const int POLE_STANCE = 3;
        private const uint IDLE = 0, GUARD = 100, WALK = 20000, RUN = 20100, SPRINT = 20200;
        private const uint CROUCH_IDLE = 300000, CROUCH_WALK = 320000, CROUCH_RUN = 320100;
        private const uint RUN_STOP = 22100, CROUCH_RUN_STOP = 322100, CROUCH_ENTER = 390000, CROUCH_EXIT = 390001;
        private const float STOP_WITHIN = 0.3f;
        private const string AIR_LOOP = "a000_202040";
        private const string FALL_START = "a000_004000";
        private const float FALL_START_FRAMES = 24f;
        // Knocked off a ledge by a hit: thrown onto the back, then tumbling in
        // that pose (the heavy landing's sprawl starts from it).
        private const string THROWN_START = "a000_004060", THROWN_LOOP = "a000_004010";
        private const float THROWN_START_FRAMES = 55f;
        private const string DEATH = "a000_017002";
        private const float LAYER_BLEND = 0.12f;

        private static readonly string[] Fingers =
        {
            "Finger0", "Finger01", "Finger02", "Finger1", "Finger11", "Finger12", "Finger2", "Finger21", "Finger22",
            "Finger3", "Finger31", "Finger32", "Finger4", "Finger41", "Finger42",
        };

        private readonly Clips _clips;
        private readonly int[] _leftArm, _rightArm, _leftFingers, _rightFingers;
        private readonly int _chest, _lWeapon, _rWeapon, _lHand;

        public string Clip { get; private set; }
        public float Frame { get; private set; }
        public bool Looped { get; private set; }
        public readonly ErPose Shown;

        private readonly ErPose _from, _target, _layer, _base, _blended;
        private bool _hasShown;
        private float _fade, _fadeLen;
        private float _phase, _guard, _carry, _swap;
        private string _rest;
        private float _restFrame;
        private float _ran;
        private uint _ranDir;
        private bool _still = true, _crouched;

        public Chooser(Clips clips)
        {
            _clips = clips;
            int n = clips.Bones.Length;
            Shown = new ErPose(n);
            _from = new ErPose(n);
            _target = new ErPose(n);
            _layer = new ErPose(n);
            _base = new ErPose(n);
            _blended = new ErPose(n);
            _chest = clips.Bone("Spine2");
            _lWeapon = clips.Bone("L_Weapon");
            _rWeapon = clips.Bone("R_Weapon");
            _lHand = clips.Bone("L_Hand");
            _leftFingers = Indices(clips, "L_", Fingers);
            _rightFingers = Indices(clips, "R_", Fingers);
            _leftArm = Concat(Indices(clips, "", new[] { "L_Clavicle", "L_UpperArm", "L_Forearm", "L_Hand", "L_Weapon" }), _leftFingers);
            _rightArm = Concat(Indices(clips, "", new[] { "R_Clavicle", "R_UpperArm", "R_Forearm", "R_Hand", "R_Weapon" }), _rightFingers);
        }

        private static int[] Indices(Clips c, string prefix, string[] names)
        {
            var list = new System.Collections.Generic.List<int>();
            foreach (string name in names)
            {
                int i = c.Bone(prefix + name);
                if (i >= 0)
                {
                    list.Add(i);
                }
            }
            return list.ToArray();
        }

        private static int[] Concat(int[] a, int[] b)
        {
            var r = new int[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);
            return r;
        }

        public static string ClipName(int category, uint id) => $"a{category:000}_{id:000000}";

        /// <summary>The shield's guard style (EquipParamWeapon byte 0xE8): 0 medium, 1 greatshield, 2 small.</summary>
        public int ShieldStyle;

        /// <summary>The guard layer's clip on the last update (a check).</summary>
        public string GuardClip;

        /// <summary>
        /// A two-handed guard (or EmptyHandGuard's): how far up the weapon line (ER
        /// metres from the right weapon bone) its clip has the left wrist, and the
        /// guard layer's weight. The layer lays only rotations over the body, so
        /// Shown's hand positions are still the body clip's, not the guard's.
        /// </summary>
        public float GuardAlong, GuardWeight;

        /// <summary>
        /// A shield guard clip in the held shield's style. The sim times every
        /// style by the medium shield's clips (the same lengths); each style has
        /// its own hold (000100/110/120), hits (19200/19300/19400 +0/10/20) and
        /// break (19500/19510/19520).
        /// </summary>
        public static string ShieldClip(string name, int style)
        {
            if (style == 0 || !name.StartsWith("a000_") || !uint.TryParse(name.Substring(5), out uint id))
            {
                return name;
            }
            if (id >= 19200 && id < 19230)
            {
                return ClipName(0, id + 100u * (uint)style);
            }
            return id == 19500 || id == GUARD ? ClipName(0, id + 10u * (uint)style) : name;
        }

        private bool ShieldGuards(SimPlayer p) => p.Grip == Grip.OneHand && p.Left == ErData.SHIELD && !p.GuardsWithRight();

        /// <summary>
        /// Guarding with the right weapon and nothing in the left hand
        /// (EmptyHandGuard) looks as guarding two-handed does: its whole stance,
        /// not only its arms over the one-handed body.
        /// </summary>
        internal static bool GuardsTwoHanded(SimPlayer p) =>
            p.GuardsWithRight() && p.Left != p.Weapon && (p.Guarding || p.State == StateKind.Act && p.Act.Id == ActionId.GuardHit);

        public static int Stance(SimPlayer p)
        {
            switch (p.Grip)
            {
                case Grip.OneHand: return ErData.Weapons[p.Weapon].Stance[GuardsTwoHanded(p) ? 1 : 0];
                case Grip.TwoHandRight: return ErData.Weapons[p.Weapon].Stance[1];
                default: return ErData.Weapons[p.Left].Stance[1];
            }
        }

        /// <summary>rig.rs playing(): the clip, frame and looping the state shows.</summary>
        private void Playing(SimPlayer p, float time, float dt, float ahead, out string name, out float frame, out bool looped)
        {
            looped = false;
            if (p.AirAttackState is AirAttack attack)
            {
                name = attack.Def.Source;
                frame = attack.F + ahead * attack.Def.SpeedAt(attack.F);
                return;
            }
            int stance = Stance(p);
            bool grounded = p.State == StateKind.Ground;
            bool still = grounded && p.Speed < 0.05f;
            float runningFrom = p.Crouching ? (ErData.CROUCH_WALK_SPEED + ErData.CROUCH_RUN_SPEED) / 2f : (ErData.WALK_SPEED + ErData.RUN_SPEED) / 2f;
            _ran = grounded && p.Speed > runningFrom ? 0f : _ran + dt;
            if (!still)
            {
                _rest = null;
            }
            else if (p.Crouching != _crouched)
            {
                _rest = ClipName(0, p.Crouching ? CROUCH_ENTER : CROUCH_EXIT);
                _restFrame = 0f;
            }
            else if (!_still && _ran < STOP_WITHIN)
            {
                string[] options;
                if (p.Crouching)
                {
                    options = new[] { ClipName(0, CROUCH_RUN_STOP) };
                }
                else
                {
                    int own = p.Grip == Grip.OneHand ? 0 : stance;
                    options = new[] { ClipName(own, RUN_STOP + _ranDir), ClipName(own, RUN_STOP) };
                }
                _rest = null;
                foreach (string o in options)
                {
                    if (_clips.Get(o) != null)
                    {
                        _rest = o;
                        _restFrame = 0f;
                        break;
                    }
                }
            }
            _still = still;
            _crouched = p.Crouching;
            if (_rest != null)
            {
                _restFrame += Consts.ANIM_FPS * dt;
                Clip c = _clips.Get(_rest);
                if (c != null && _restFrame < c.Frames - 1)
                {
                    name = _rest;
                    frame = _restFrame;
                    return;
                }
                _rest = null;
            }
            switch (p.State)
            {
                case StateKind.Dead:
                    name = DEATH;
                    frame = p.DeadT + ahead;
                    return;
                case StateKind.Air when p.Air.Thrown && p.Air.F < THROWN_START_FRAMES:
                    name = THROWN_START;
                    frame = p.Air.F + ahead;
                    return;
                case StateKind.Air when p.Air.Thrown:
                    name = THROWN_LOOP;
                    frame = time * Consts.ANIM_FPS;
                    looped = true;
                    return;
                case StateKind.Air when !p.Air.Jumped && p.Air.F < FALL_START_FRAMES:
                    name = FALL_START;
                    frame = p.Air.F + ahead;
                    return;
                case StateKind.Air:
                    name = AIR_LOOP;
                    frame = time * Consts.ANIM_FPS;
                    looped = true;
                    return;
                case StateKind.Act:
                    name = p.Act.Id.Def().Source;
                    if (ShieldGuards(p))
                    {
                        name = ShieldClip(name, ShieldStyle);
                    }
                    // Inside the game's speed windows the frame runs ahead faster too.
                    frame = p.Act.F + ahead * p.Act.Id.Def().SpeedAt(p.Act.F);
                    return;
            }
            looped = true;
            if (p.Speed < 0.05f)
            {
                name = p.Crouching ? ClipName(0, CROUCH_IDLE) : ClipName(stance, IDLE);
                frame = time * Consts.ANIM_FPS;
                return;
            }
            Vec3 facing = p.Facing();
            float along = p.MoveDir.Dot(facing);
            float across = p.MoveDir.Dot(new Vec3(facing.Z, 0f, -facing.X));
            uint dir = Mathf.Abs(along) >= Mathf.Abs(across) ? (along < 0f ? 1u : 0u) : (across < 0f ? 3u : 2u);
            _ranDir = dir;
            uint id;
            float native;
            if (p.Crouching && p.Speed > (ErData.CROUCH_WALK_SPEED + ErData.CROUCH_RUN_SPEED) / 2f)
            {
                id = CROUCH_RUN + dir;
                native = ErData.CROUCH_RUN_SPEED;
            }
            else if (p.Crouching)
            {
                id = CROUCH_WALK + dir;
                native = ErData.CROUCH_WALK_SPEED;
            }
            else if (p.Sprinting && p.Speed > ErData.RUN_SPEED)
            {
                id = SPRINT;
                native = ErData.SPRINT_SPEED;
            }
            else if (p.Speed > (ErData.WALK_SPEED + ErData.RUN_SPEED) / 2f)
            {
                id = RUN + dir;
                native = new[] { ErData.RUN_SPEED, ErData.RUN_BACK_SPEED, ErData.RUN_SIDE_SPEED, ErData.RUN_SIDE_SPEED }[dir];
            }
            else
            {
                id = WALK + dir;
                native = ErData.WALK_SPEED;
            }
            name = ClipName(stance, id);
            if (p.Grip == Grip.OneHand || p.Crouching || _clips.Get(name) == null)
            {
                name = ClipName(0, id);
            }
            Clip clip = _clips.Get(name);
            float last = clip != null ? clip.Frames - 1 : 1f;
            _phase = Frac(_phase + p.Speed / native * Consts.ANIM_FPS * dt / last);
            frame = _phase * last;
        }

        private static float Frac(float x) => x - Mathf.Floor(x);

        /// <summary>
        /// rig.rs animate(), up to the drawing: fills <see cref="Shown"/>.
        /// alpha is how far the clock is past the last sim tick (0..1).
        /// </summary>
        public bool Update(SimPlayer p, float time, float dt, float alpha)
        {
            float ahead = p.HitStop > 0f ? 0f : alpha * Consts.DF;
            Playing(p, time, dt, ahead, out string name, out float frame, out bool looped);
            Clip clip = _clips.Get(name);
            if (clip == null)
            {
                return _hasShown;
            }
            _clips.Sample(clip, frame, looped, _target);
            Frame = looped ? Clips.Mod(frame, Mathf.Max(clip.Frames - 1, 1)) : frame;
            Looped = looped;
            if (name != Clip)
            {
                Clip = name;
                // From the clips' own blend, not Shown: the layers, the sprint's unlean and
                // the free aim's tilt are put on it again every frame, so fading from Shown
                // counted them twice for the fade's first frames (the aim: a 10° jerk back to idle).
                _from.CopyFrom(_blended);
                _fade = 0f;
                _fadeLen = _hasShown ? clip.Blend / Consts.ANIM_FPS : 0f;
            }
            _fade += dt;
            float t = _fadeLen > 0f ? Mathf.Clamp01(_fade / _fadeLen) : 1f;
            t = t * t * (3f - 2f * t);
            if (t >= 1f)
            {
                Shown.CopyFrom(_target);
            }
            else
            {
                for (int i = 0; i < Shown.Rot.Length; i++)
                {
                    Shown.Rot[i] = Quaternion.Slerp(_from.Rot[i], _target.Rot[i], t);
                    Shown.Pos[i] = Vector3.Lerp(_from.Pos[i], _target.Pos[i], t);
                }
            }
            _blended.CopyFrom(Shown);
            _hasShown = true;

            bool twoHanded = p.Grip != Grip.OneHand || GuardsTwoHanded(p);
            int stance = Stance(p);
            bool grounded = p.State == StateKind.Ground;
            bool raising = p.Guarding || p.State == StateKind.Act && p.Act.Id == ActionId.GuardHit;
            float step = dt / LAYER_BLEND;
            _guard = Mathf.Clamp01(_guard + (raising ? step : -step));
            bool stopping = _rest != null && !p.Crouching && !Clip.EndsWith(CROUCH_EXIT.ToString("000000"));
            bool carrying = !twoHanded && stance != 0 && grounded && (p.Speed >= 0.05f || stopping);
            _carry = Mathf.Clamp01(_carry + (carrying ? step : -step));

            Carry(ClipName(stance, IDLE), time, _carry, dt, onChest: stance == POLE_STANCE);
            // Two-handed, a jump (take-off to landing; not jump attacks) keeps the
            // weapon held as at idle: ER's jumps are shared, the weapon hanging at the side.
            bool jumping = twoHanded && !p.AirAttackState.HasValue && (p.State == StateKind.Air || p.State == StateKind.Act && Controller.IsJumpAct(p.Act.Id.Tag));
            _jumpHold = Mathf.Clamp01(_jumpHold + (jumping ? step : -step));
            Overlay(ClipName(stance, IDLE), null, time, _leftArm, _rightArm, _jumpHold);
            string guardClip;
            // Bare fists, two-handed too, have no handle: they guard as the empty left hand does.
            bool fists = Controller.Handless(p.Weapon);
            if (twoHanded && !fists)
            {
                guardClip = ClipName(stance, GUARD);
                Overlay(guardClip, null, time, _leftArm, _rightArm, _guard);
            }
            else if (p.GuardsWithRight())
            {
                // No shield and nothing in the left hand (Eldenheim's option):
                // the weapon's two-handed guard, the free hand on the handle.
                // Paired weapons (a weapon in each hand) guard one-handed.
                guardClip = ClipName(ErData.Weapons[p.Weapon].Stance[p.Left == p.Weapon ? 0 : 1], GUARD);
                Overlay(guardClip, null, time, _leftArm, _rightArm, _guard);
            }
            else
            {
                guardClip = ShieldClip(ClipName(ErData.Weapons[ErData.SHIELD].Stance[0], GUARD), ShieldStyle);
                Overlay(guardClip, null, time, _leftArm, null, _guard);
            }
            GuardClip = guardClip;
            GuardWeight = (twoHanded && !fists) || p.GuardsWithRight() ? _guard : 0f;
            if (GuardWeight > 0f && _lHand >= 0 && _rWeapon >= 0)
            {
                GuardAlong = Vector3.Dot(_layer.Pos[_lHand] - _layer.Pos[_rWeapon], _layer.Rot[_rWeapon] * Vector3.up);
            }
            // Standing still, the guard clip's own legs too: ER braces (feet
            // 0.8 m apart, hips 10 cm lower). The sandbox layers arms only.
            bool bracing = p.Guarding && grounded && p.Speed < 0.05f && !p.Crouching && _rest == null;
            _brace = Mathf.Clamp01(_brace + (bracing ? step : -step) * 0.6f);
            LegsOverlay(guardClip, time, _brace);
            if (p.SwapState is Swap swap)
            {
                SwapDef def = ErData.Swap(swap.Kind);
                float at = swap.F + alpha * Consts.DF;
                _swap = Mathf.Min(_swap + step, 1f);
                if (at < def.StartLen)
                {
                    Overlay(def.Start, at, time, _leftArm, _rightArm, _swap);
                }
                else
                {
                    float u = Mathf.Clamp01((at - def.StartLen) / def.EndLen);
                    Overlay(def.End, at - def.StartLen, time, _leftArm, _rightArm, _swap * (1f - u * u * (3f - 2f * u)));
                }
            }
            else
            {
                _swap = 0f;
            }

            _sprintW = Mathf.Clamp01(_sprintW + (p.Sprinting && grounded ? step : -step) * 0.5f);
            Unlean(1f - Settings.SprintLean.Value / 100f, _sprintW);
            // The tilt eases in and out (the sim's steps at once: hits aim where the camera looks).
            if (dt > 0f)
            {
                _aim = Mathf.SmoothDamp(_aim, p.AimPitch, ref _aimRate, AIM_EASE, Mathf.Infinity, dt);
            }
            Aim(_aim);
            HeadLookAt(dt);

            // The body's animations leave the fingers open: the grip is the
            // one in the stance's idle, carried on each hand's weapon bone.
            Clip idle = _clips.Get(ClipName(stance, IDLE));
            if (idle != null)
            {
                _clips.Sample(idle, 0f, false, _layer);
                CloseHand(_lWeapon, _leftFingers);
                CloseHand(_rWeapon, _rightFingers);
            }
            return true;
        }

        private float _jumpHold;
        private const float CARRY_SWING = 0.6f, CARRY_SWING_MAX = 20f;
        // A pole swings a quarter as much, at most 6° (~20 cm at the tip of a 2 m pole):
        // the full swing wobbled it, none at all looked stiff.
        private const float POLE_SWING = 0.15f, POLE_SWING_MAX = 6f;
        private int _rUpper = -2, _rClavicle;
        private float _swingW;
        private readonly System.Collections.Generic.Dictionary<string, Quaternion> _upperMean = new System.Collections.Generic.Dictionary<string, Quaternion>();

        /// <summary>
        /// The one-handed carry of a big weapon (greatswords, greataxes, spears,
        /// halberds) while moving: the right arm takes the stance idle's hold,
        /// and the whole arm, kept as held, turns at the shoulder by part of the
        /// walk or run's own upper-arm swing (about that clip's average, at most
        /// 20°), so it isn't stiff. No swing in the stops, which don't loop.
        /// (The sandbox lays the idle's arm over as it is.) onChest: the hold
        /// leans and turns with the chest instead of keeping the idle's upright
        /// arm (a pole carried at the hip; the arm hung straight down while the
        /// body leant into the run).
        /// </summary>
        private void Carry(string name, float time, float weight, float dt, bool onChest)
        {
            _swingW = Mathf.MoveTowards(_swingW, Looped ? 1f : 0f, dt / 0.2f);
            if (weight <= 0f)
            {
                return;
            }
            Clip hold = _clips.Get(name);
            if (hold == null)
            {
                return;
            }
            if (_rUpper == -2)
            {
                _rUpper = _clips.Bone("R_UpperArm");
                _rClavicle = _clips.Bone("R_Clavicle");
            }
            Quaternion swing = Quaternion.identity;
            if (_rUpper >= 0 && _swingW > 0f && UpperMean(Clip) is Quaternion mean)
            {
                (Quaternion.Inverse(mean) * Shown.Rot[_rUpper]).ToAngleAxis(out float angle, out Vector3 axis);
                float most = onChest ? POLE_SWING_MAX : CARRY_SWING_MAX;
                angle = Mathf.Clamp((angle > 180f ? angle - 360f : angle) * (onChest ? POLE_SWING : CARRY_SWING) * _swingW, -most, most);
                swing = Quaternion.AngleAxis(angle, axis);
            }
            _clips.Sample(hold, time * Consts.ANIM_FPS, true, _layer);
            // The swing, from the upper arm's own frame into character space: the arm below turns with it.
            Quaternion turn = _rUpper >= 0 ? _layer.Rot[_rUpper] * swing * Quaternion.Inverse(_layer.Rot[_rUpper]) : Quaternion.identity;
            Quaternion seat = onChest && _chest >= 0 ? Shown.Rot[_chest] * Quaternion.Inverse(_layer.Rot[_chest]) : Quaternion.identity;
            // It leans with the chest, but not with each stride's full twist and bob: the chest smoothed over ~0.2 s.
            _seat = onChest ? Quaternion.Slerp(_seat, seat, 1f - Mathf.Exp(-dt / 0.2f)) : Quaternion.identity;
            seat = _seat;
            foreach (int b in _rightArm)
            {
                Quaternion carried = seat * (b == _rClavicle ? _layer.Rot[b] : turn * _layer.Rot[b]);
                Shown.Rot[b] = Quaternion.Slerp(Shown.Rot[b], carried, weight);
            }
        }

        /// <summary>The right upper arm's average rotation over a clip (null: no such clip).</summary>
        private Quaternion? UpperMean(string name)
        {
            if (name == null)
            {
                return null;
            }
            if (_upperMean.TryGetValue(name, out Quaternion known))
            {
                return known;
            }
            Clip clip = _clips.Get(name);
            if (clip == null)
            {
                return null;
            }
            Vector4 sum = Vector4.zero;
            Quaternion first = Quaternion.identity;
            for (int i = 0; i < 12; i++)
            {
                _clips.Sample(clip, (clip.Frames - 1) * i / 12f, false, _base);
                Quaternion q = _base.Rot[_rUpper];
                if (i == 0)
                {
                    first = q;
                }
                float sign = Quaternion.Dot(first, q) < 0f ? -1f : 1f;
                sum += new Vector4(q.x, q.y, q.z, q.w) * sign;
            }
            sum.Normalize();
            return _upperMean[name] = new Quaternion(sum.x, sum.y, sum.z, sum.w);
        }

        private float _brace;
        private int[] _legs;
        private Quaternion _seat = Quaternion.identity;

        /// <summary>The pelvis and legs from a clip (looped on the clock), rotations and positions.</summary>
        private void LegsOverlay(string name, float time, float weight)
        {
            if (weight <= 0f)
            {
                return;
            }
            Clip clip = _clips.Get(name);
            if (clip == null)
            {
                return;
            }
            if (_legs == null)
            {
                var list = new System.Collections.Generic.List<int>();
                for (int i = 0; i < _clips.Bones.Length; i++)
                {
                    string n = _clips.Bones[i];
                    if (n == "Pelvis" || n.Contains("Thigh") || n.Contains("Calf") || n.Contains("Foot") || n.Contains("Toe"))
                    {
                        list.Add(i);
                    }
                }
                _legs = list.ToArray();
            }
            _clips.Sample(clip, time * Consts.ANIM_FPS, true, _layer);
            foreach (int b in _legs)
            {
                Shown.Rot[b] = Quaternion.Slerp(Shown.Rot[b], _layer.Rot[b], weight);
                Shown.Pos[b] = Vector3.Lerp(Shown.Pos[b], _layer.Pos[b], weight);
            }
        }

        private float _sprintW;
        private bool[] _upper;
        private int _pelvis = -2, _head;

        /// <summary>
        /// Takes back part of the sprint's forward lean (Valheim's character
        /// already stoops at rest, so ER's full lean reads as a hunch): the
        /// upper body turns about the pelvis towards upright by `amount` of the
        /// way, at `weight`.
        /// </summary>
        private void Unlean(float amount, float weight)
        {
            if (weight <= 0f || amount <= 0f)
            {
                return;
            }
            UpperBones();
            if (_pelvis < 0 || _head < 0)
            {
                return;
            }
            Vector3 pivot = Shown.Pos[_pelvis];
            Vector3 spine = Shown.Pos[_head] - pivot;
            if (spine.sqrMagnitude < 1e-6f)
            {
                return;
            }
            Vector3 v = spine.normalized;
            Quaternion fix = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(v, Vector3.Slerp(v, Vector3.up, amount)), weight);
            for (int i = 0; i < _upper.Length; i++)
            {
                if (_upper[i])
                {
                    Shown.Rot[i] = fix * Shown.Rot[i];
                    Shown.Pos[i] = pivot + fix * (Shown.Pos[i] - pivot);
                }
            }
        }

        /// <summary>The pelvis, the head, and which bones are the upper body (all but the legs and root).</summary>
        private void UpperBones()
        {
            if (_pelvis != -2)
            {
                return;
            }
            _pelvis = _clips.Bone("Pelvis");
            _head = _clips.Bone("Head");
            _upper = new bool[_clips.Bones.Length];
            for (int i = 0; i < _upper.Length; i++)
            {
                string n = _clips.Bones[i];
                _upper[i] = n != "Pelvis" && n != "RootPos" && n != "Master" && n != "RootRotY" && n != "RootRotXZ"
                            && !n.Contains("Thigh") && !n.Contains("Calf") && !n.Contains("Foot") && !n.Contains("Toe");
            }
        }

        /// <summary>Where the head looks (character space, Valheim's CharacterAnimEvent look); zero: ahead.</summary>
        public Vector3 HeadLook;
        /// <summary>No head look (attacks), as Valheim's while InAttack.</summary>
        public bool HeadLookOff;
        private float _headW;
        /// <summary>Valheim's look-at weight (CharacterAnimEvent.m_lookWeight), clamp (m_lookClamp 0.5: 90°) and fade (1 a second).</summary>
        private const float HEAD_WEIGHT = 0.5f, HEAD_CLAMP = 90f;

        /// <summary>Valheim's head look (CharacterAnimEvent.UpdateLookat, animator IK): the head turned part way to the look.</summary>
        private void HeadLookAt(float dt)
        {
            _headW = Mathf.MoveTowards(_headW, HeadLookOff ? 0f : HEAD_WEIGHT, dt);
            UpperBones();
            if (_headW <= 0f || _head < 0 || HeadLook.sqrMagnitude < 1e-4f)
            {
                return;
            }
            Vector3 dir = Vector3.RotateTowards(Vector3.forward, HeadLook.normalized, HEAD_CLAMP * Mathf.Deg2Rad, 0f);
            Shown.Rot[_head] = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.forward, dir), _headW) * Shown.Rot[_head];
        }

        private float _aim, _aimRate;
        /// <summary>Test harness (ytrace): the drawn tilt (rad).</summary>
        public float AimShown => _aim;
        /// <summary>How long the drawn tilt takes to follow the sim's (s, SmoothDamp's time).</summary>
        private const float AIM_EASE = 0.15f;

        /// <summary>The sim's AimPitch (free aim): the upper body tilted up or down about the hips, as its blade path is.</summary>
        private void Aim(float pitch)
        {
            if (Mathf.Abs(pitch) < 1e-4f)
            {
                return;
            }
            UpperBones();
            if (_pelvis < 0)
            {
                return;
            }
            Vector3 pivot = Shown.Pos[_pelvis];
            // Character space: +z forward, +y up.
            Quaternion tilt = Quaternion.FromToRotation(Vector3.forward, new Vector3(0f, Mathf.Sin(pitch), Mathf.Cos(pitch)));
            for (int i = 0; i < _upper.Length; i++)
            {
                if (_upper[i])
                {
                    Shown.Rot[i] = tilt * Shown.Rot[i];
                    Shown.Pos[i] = pivot + tilt * (Shown.Pos[i] - pivot);
                }
            }
        }

        private void CloseHand(int weapon, int[] fingers)
        {
            if (weapon < 0)
            {
                return;
            }
            Quaternion carry = Shown.Rot[weapon] * Quaternion.Inverse(_layer.Rot[weapon]);
            foreach (int f in fingers)
            {
                Shown.Rot[f] = carry * _layer.Rot[f];
            }
        }

        /// <summary>
        /// An arm layer: those bones taken from another clip, re-seated on the
        /// chest of whatever the body is doing.
        /// </summary>
        private void Overlay(string name, float? at, float time, int[] arms, int[] arms2, float weight)
        {
            if (weight <= 0f)
            {
                return;
            }
            Clip clip = _clips.Get(name);
            if (clip == null)
            {
                return;
            }
            if (at.HasValue)
            {
                _clips.Sample(clip, at.Value, false, _layer);
            }
            else
            {
                _clips.Sample(clip, time * Consts.ANIM_FPS, true, _layer);
            }
            // rig.rs moves the layer's joints by the chest's offset only, so
            // each arm bone keeps the layer's own character-space direction.
            Apply(arms, Quaternion.identity, weight);
            if (arms2 != null)
            {
                Apply(arms2, Quaternion.identity, weight);
            }
        }

        private void Apply(int[] bones, Quaternion seat, float weight)
        {
            foreach (int b in bones)
            {
                Shown.Rot[b] = Quaternion.Slerp(Shown.Rot[b], seat * _layer.Rot[b], weight);
            }
        }
    }
}
