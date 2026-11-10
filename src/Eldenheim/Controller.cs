using System.Collections.Generic;
using System.Linq;
using Eldenheim.Anim;
using Eldenheim.Sim;
using HarmonyLib;
using UnityEngine;
using Input = Eldenheim.Sim.Input;
using SimPlayer = Eldenheim.Sim.Player;
using VPlayer = global::Player;

namespace Eldenheim
{
    /// <summary>
    /// Owns the local player while an Elden Ring-mapped weapon (or bare fists)
    /// is out: the sandbox's sim, ticked at 60 Hz inside Valheim's motion
    /// step, moves the body through PhysX and chooses the pose. Valheim keeps
    /// everything else (inventory, building, swimming, riding...).
    /// </summary>
    public class Controller : MonoBehaviour
    {
        public static Controller Local;
        public static Clips Clips;

        public bool Active { get; private set; }
        public SimPlayer Sim { get; private set; }
        public bool BlockingNow;
        public Character LockTarget;
        public readonly List<string> Log = new List<string>();
        public string Why = "";

        private VPlayer _vp;
        private Rigidbody _body;
        private Animator _animator;
        private VisEquipment _vis;
        private readonly Bindings _bindings = new Bindings();
        private readonly PhysicsLevel _level = new PhysicsLevel();
        private readonly Combat _combat = new Combat();
        private readonly Combat.Blade _rightBlade = new Combat.Blade(), _leftBlade = new Combat.Blade();
        private Steps _steps;
        private Chooser _chooser;
        private Retarget _retarget;
        private PoseOutput _pose;
        private Quaternion[] _local;
        private float _acc;
        private float _weight;
        private ItemDrop.ItemData _lastRight, _lastLeft;
        private Grip _lastGrip;
        private GameObject _lastRightInstance, _lastLeftInstance;
        private Vector3 _bladeRight, _bladeLeft;
        private Vector2 _rangeRight, _rangeLeft;
        // A paired weapon's blade lines run beside the attach points (zero for held items).
        private Vector3 _offsetRight, _offsetLeft;
        private float _radiusRight, _radiusLeft;
        private float _clock;
        private bool _recenter;
        private float _bodyY;
        private float _grip;
        private bool _counterSounded;
        // The body follows the sim a tick behind, interpolated, so it moves
        // evenly although 60 Hz ticks fall unevenly into 50 Hz physics steps.
        private Vector3 _tickFrom, _tickTo, _expected;
        private float _yawFrom, _yawTo;

        private static readonly AccessTools.FieldRef<Character, Vector3> RootMotion = AccessTools.FieldRefAccess<Character, Vector3>("m_rootMotion");
        private static readonly AccessTools.FieldRef<Character, Vector3> CurrentVel = AccessTools.FieldRefAccess<Character, Vector3>("m_currentVel");
        private static readonly AccessTools.FieldRef<Character, float> MaxAirAltitude = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");
        private static readonly AccessTools.FieldRef<Character, bool> Running = AccessTools.FieldRefAccess<Character, bool>("m_running");
        private static readonly AccessTools.FieldRef<Character, float> LastGroundTouch = AccessTools.FieldRefAccess<Character, float>("m_lastGroundTouch");
        private static readonly AccessTools.FieldRef<Character, float> JumpTimer = AccessTools.FieldRefAccess<Character, float>("m_jumpTimer");
        private static readonly AccessTools.FieldRef<Character, Vector3> PushForce = AccessTools.FieldRefAccess<Character, Vector3>("m_pushForce");
        private static readonly AccessTools.FieldRef<Character, bool> HasGroundContact = AccessTools.FieldRefAccess<Character, bool>("m_groundContact");
        private static readonly AccessTools.FieldRef<Character, Vector3> ContactNormal = AccessTools.FieldRefAccess<Character, Vector3>("m_groundContactNormal");
        private static readonly AccessTools.FieldRef<Character, Vector3> ContactPoint = AccessTools.FieldRefAccess<Character, Vector3>("m_groundContactPoint");
        private static readonly AccessTools.FieldRef<Character, Collider> LowestContact = AccessTools.FieldRefAccess<Character, Collider>("m_lowestContactCollider");
        private static readonly AccessTools.FieldRef<Character, bool> Sliding = AccessTools.FieldRefAccess<Character, bool>("m_sliding");
        private static readonly AccessTools.FieldRef<Character, bool> WallRunning = AccessTools.FieldRefAccess<Character, bool>("m_wallRunning");
        private Vector3 _carry;
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LeftItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_leftItem");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> RightInstance = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_rightItemInstance");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> LeftInstance = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_leftItemInstance");
        private static readonly System.Reflection.MethodInfo TakeInput = AccessTools.Method(typeof(PlayerController), "TakeInput");

        public static Controller For(VPlayer vp)
        {
            Controller c = vp.GetComponent<Controller>();
            if (c == null)
            {
                c = vp.gameObject.AddComponent<Controller>();
            }
            return c;
        }

        private void Awake()
        {
            _vp = GetComponent<VPlayer>();
            _body = GetComponent<Rigidbody>();
            _vis = GetComponent<VisEquipment>();
            _nview = GetComponent<ZNetView>();
            _animator = Traverse.Create(_vp).Field("m_animator").GetValue<Animator>();
            All.Add(this);
        }

        private void OnDestroy()
        {
            All.Remove(this);
            if (Local == this)
            {
                Local = null;
            }
        }

        /// <summary>Another player's character: posed from what its owner sends (PLAN §3.7), never simulated here.</summary>
        private bool Remote => _nview != null && _nview.IsValid() && !_nview.IsOwner();

        /// <summary>Whether Eldenheim poses this character now (the local player or another player).</summary>
        public static bool Posing(Character c) => c is VPlayer && c.TryGetComponent(out Controller k) && k.Active;

        // A remote player's Humanoid holds no items: these stand in, from VisEquipment's synced hashes.
        public ItemDrop.ItemData Right => Remote ? _remoteRight : RightItem(_vp);

        /// <summary>In an attack (grounded or in the air): no equipping, sheathing or drawing.</summary>
        public bool Attacking => Active && Sim != null && (Sim.State == StateKind.Act && Sim.Act.Id.IsAttack || Sim.AirAttackState.HasValue);

        public ItemDrop.ItemData Left => Remote ? _remoteLeft : LeftItem(_vp);

        // --- Other players ------------------------------------------------------

        private static readonly List<Controller> All = new List<Controller>();
        private static readonly int StateKey = "eh_pose".GetStableHashCode(), DataKey = "eh_data".GetStableHashCode();
        private static readonly AccessTools.FieldRef<VisEquipment, int> RightHash = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentRightItemHash");
        private static readonly AccessTools.FieldRef<VisEquipment, int> LeftHash = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLeftItemHash");
        private static readonly AccessTools.FieldRef<VisEquipment, int> RightQuality = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentRightItemQuality");
        private static readonly AccessTools.FieldRef<VisEquipment, int> LeftQuality = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLeftItemQuality");
        private static readonly AccessTools.FieldRef<VisEquipment, int> LeftVariant = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLeftItemVariant");
        private ZNetView _nview;
        private byte[] _sent, _got;
        private uint _revision;
        private bool _warned;
        private ItemDrop.ItemData _remoteRight, _remoteLeft;
        private bool _sentInvincible;

        /// <summary>The owner: the sim's pose state into the ZDO (empty while Eldenheim is off), only when it changed.</summary>
        private void Publish(bool on)
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner())
            {
                return;
            }
            ZDO zdo = _nview.GetZDO();
            // Valheim's own flag for other clients' IsDodgeInvincible: an attack they
            // judge (their creatures, their hits) then meets the roll's i-frames and
            // sends the perfect dodge (HitWhileDodging), whose effects are networked.
            bool invincible = on && Sim != null && Sim.Invincible();
            if (invincible != _sentInvincible)
            {
                _sentInvincible = invincible;
                zdo.Set(ZDOVars.s_dodgeinv, invincible);
            }
            byte[] now = on && Sim != null ? PoseState.Write(Sim) : System.Array.Empty<byte>();
            if (_sent != null && now.SequenceEqual(_sent))
            {
                return;
            }
            _sent = now;
            zdo.Set(StateKey, now);
            zdo.Set(DataKey, Plugin.DataHash);
        }

        /// <summary>
        /// Another player with Eldenheim on: a stand-in sim filled from its ZDO
        /// (frames moved on between updates) drives the same chooser and
        /// retarget as the local player's. Footsteps and effects arrive
        /// through Valheim's own networking.
        /// </summary>
        private void FixedUpdate()
        {
            if (!Remote)
            {
                return;
            }
            float dt = Time.fixedDeltaTime;
            ZDO zdo = _nview.GetZDO();
            bool fresh = false;
            if (zdo.DataRevision != _revision)
            {
                _revision = zdo.DataRevision;
                byte[] got = zdo.GetByteArray(StateKey);
                if (got != null && (_got == null || !got.SequenceEqual(_got)))
                {
                    _got = got;
                    fresh = true;
                }
            }
            RemoteParries(zdo);
            int theirs = zdo.GetInt(DataKey);
            if (theirs != 0 && theirs != Plugin.DataHash && !_warned)
            {
                _warned = true;
                Plugin.Report($"{_vp.GetPlayerName()} has a different Eldenheim data build: shown with Valheim's animation");
            }
            bool on = Clips != null && ErData.Loaded && Plugin.DataHash != 0 && theirs == Plugin.DataHash
                      && _got != null && _got.Length > 1 && !_vp.IsDead()
                      && _animator != null && _animator.isInitialized && _animator.avatar != null && _animator.avatar.isValid;
            if (on)
            {
                if (Sim == null)
                {
                    Sim = new SimPlayer(new Vec3(0f, 0f, 0f));
                }
                if (fresh || !Active)
                {
                    StateKind wasState = Sim.State;
                    ActionId wasAct = Sim.Act.Id;
                    float wasF = Sim.Act.F, wasAirF = Sim.AirAttackState?.F ?? -1f;
                    on = PoseState.Read(_got, Sim);
                    // A late update would step the frame back (moved on here meanwhile), which reads as a new swing: hold it.
                    if (on && Sim.State == wasState && Sim.Act.Id == wasAct && Sim.Act.F < wasF && wasF - Sim.Act.F < 3f)
                    {
                        Sim.Act.F = wasF;
                    }
                    if (on && Sim.AirAttackState is AirAttack air && air.F < wasAirF && wasAirF - air.F < 3f)
                    {
                        air.F = wasAirF;
                        Sim.AirAttackState = air;
                    }
                }
                else
                {
                    PoseState.Advance(Sim, dt);
                }
            }
            if (on && !Active)
            {
                Active = true;
                BuildPose();
                ErSounds.Preload(_vp);
            }
            else if (!on && Active)
            {
                Active = false;
                _combat.StopTrails();
                RestoreHandVisuals();
                RestorePoles();
            }
            if (!Active)
            {
                FadeOutPose(dt);
                return;
            }
            _clock += dt;
            // Where the synced transform is: a two-handed window's trail goes to the hand nearer its sweep.
            Sim.Pos = Space.ToSim(transform.position);
            Sim.Yaw = Space.YawToSim(transform.eulerAngles.y);
            _remoteRight = RemoteItem(_remoteRight, RightHash(_vis), RightQuality(_vis), 0);
            _remoteLeft = RemoteItem(_remoteLeft, LeftHash(_vis), LeftQuality(_vis), LeftVariant(_vis));
            HandVisuals(Sim);
            TrackBlades();
            _combat.RemoteTrails(_vp, Sim, _rightBlade, _leftBlade);
            RemoteSounds();
            // Nothing reads a remote's event log.
            Log.Clear();
        }

        private static ItemDrop.ItemData RemoteItem(ItemDrop.ItemData had, int hash, int quality, int variant)
        {
            if (hash == 0 || ObjectDB.instance == null)
            {
                return null;
            }
            if (had != null && had.m_dropPrefab != null && had.m_dropPrefab.name.GetStableHashCode() == hash && had.m_quality == quality && had.m_variant == variant)
            {
                return had;
            }
            GameObject prefab = ObjectDB.instance.GetItemPrefab(hash);
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop == null)
            {
                return null;
            }
            ItemDrop.ItemData item = drop.m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_quality = quality;
            item.m_variant = variant;
            return item;
        }

        // --- When Eldenheim owns the player -------------------------------------

        private static int Weapon(string name) => System.Array.FindIndex(ErData.Weapons, w => w.Name == name);

        /// <summary>The ER class whose moveset the item uses.</summary>
        internal static string MovesetClass(ItemDrop.ItemData item) => ClassOf(item, IsTwoHanded(item), Hands.IsPair(item));

        private static bool IsTwoHanded(ItemDrop.ItemData item) => item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon;

        /// <summary>Whether Eldenheim plays this Valheim weapon.</summary>
        public static bool Playable(ItemDrop.ItemData item) =>
            item != null && ClassOf(item, IsTwoHanded(item), Hands.IsPair(item)) is string name && (ErData.Weapons == null || Weapon(name) >= 0);

        /// <summary>
        /// The ER class a Valheim weapon plays as, or null: the F1 menu's
        /// choice for its type (Weapons). ER's greatswords share an animation
        /// category but differ by style (Claymore 125, Inseparable Sword 128);
        /// the sandbox's "Greatsword" is the colossal swords.
        /// </summary>
        private static string ClassOf(ItemDrop.ItemData item, bool twoHanded, bool dual)
        {
            // Valheim files torches under clubs; ER has its own torch moveset.
            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Torch)
            {
                return "Torch";
            }
            switch (item.m_shared.m_skillType)
            {
                case Skills.SkillType.Swords: return Settings.ClassFor(twoHanded ? "TwoHandedSwords" : "OneHandedSwords");
                case Skills.SkillType.Knives: return Settings.ClassFor("Knives");
                case Skills.SkillType.Clubs: return Settings.ClassFor(twoHanded ? "TwoHandedClubs" : "OneHandedClubs");
                // The dual axes are a power stance of one-handed axes.
                case Skills.SkillType.Axes: return Settings.ClassFor(twoHanded && !dual ? "TwoHandedAxes" : "OneHandedAxes");
                case Skills.SkillType.Polearms: return Settings.ClassFor("Polearms");
                case Skills.SkillType.Spears: return Settings.ClassFor("Spears");
                // Knuckles and claws (bare hands stay Fist).
                case Skills.SkillType.Unarmed: return Settings.ClassFor("Knuckles");
                default: return null;
            }
        }

        /// <summary>
        /// ER classes whose two hands don't share a handle: the fists (category
        /// 42), the hand-to-hand arts (55) and the throwing blades (53, a blade in each hand).
        /// </summary>
        public static bool Handless(int weapon) =>
            weapon == ErData.FIST || weapon >= 0 && weapon < ErData.Weapons.Length && ErData.Weapons[weapon].Category is byte c && (c == 42 || c == 55 || c == 53);

        /// <summary>The ER right weapon, left armament and grip for what Valheim has equipped, or false.</summary>
        public static bool Map(ItemDrop.ItemData right, ItemDrop.ItemData left, out int weapon, out int leftHand, out Grip grip)
        {
            weapon = ErData.FIST;
            leftHand = ErData.FIST;
            grip = Grip.OneHand;
            if (left != null)
            {
                switch (left.m_shared.m_itemType)
                {
                    case ItemDrop.ItemData.ItemType.Shield:
                        leftHand = ErData.SHIELD;
                        break;
                    case ItemDrop.ItemData.ItemType.Torch:
                        leftHand = ErData.TORCH;
                        break;
                    case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                    case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                        string leftClass = Hands.IsHandWeapon(left) ? ClassOf(left, IsTwoHanded(left), false) : null;
                        leftHand = leftClass != null ? Weapon(leftClass) : -1;
                        if (leftHand < 0)
                        {
                            return false;
                        }
                        break;
                    default:
                        return false;
                }
            }
            if (right == null)
            {
                // Both hands empty: the BareHands choice (Fist, or a hand-to-hand art, held in both hands as knuckles are).
                int bare = left == null ? Weapon(Settings.ClassFor("BareHands") ?? "Fist") : -1;
                if (bare >= 0 && bare != ErData.FIST)
                {
                    weapon = bare;
                    grip = Handless(bare) ? Grip.TwoHandRight : Grip.OneHand;
                }
                return true;
            }
            bool twoHanded = IsTwoHanded(right);
            bool pair = Hands.IsPair(right);
            string name = ClassOf(right, twoHanded, pair);
            weapon = name != null ? Weapon(name) : -1;
            if (weapon < 0)
            {
                return false;
            }
            if (pair && right.m_shared.m_skillType == Skills.SkillType.Unarmed)
            {
                // Knuckles and claws: one item on both hands, held two-handed.
                leftHand = ErData.FIST;
                grip = Grip.TwoHandRight;
            }
            else if (pair)
            {
                // Skoll and Hati, the dual axes: one item, a weapon in each hand,
                // so the power stance (ER's two-handed grip would bring both
                // hands onto one handle).
                leftHand = weapon;
            }
            else if (twoHanded && left == null)
            {
                // A Valheim two-hander starts in both hands; with an off-hand it is held in one.
                grip = Grip.TwoHandRight;
            }
            return true;
        }

        private bool Wanted(out string why)
        {
            why = "";
            if (!Settings.Enabled.Value || Clips == null || !ErData.Loaded)
            {
                why = "disabled or no data";
                return false;
            }
            if (_animator == null || !_animator.isInitialized || _animator.avatar == null || !_animator.avatar.isValid)
            {
                // The retarget reads the skeleton's bind pose: wait for it.
                why = "animator not ready";
                return false;
            }
            if (_vp.IsDead() || _vp.IsAttached() || _vp.InIntro() || _vp.IsDebugFlying() || _vp.IsTeleporting() || _vp.InPlaceMode())
            {
                why = "busy";
                return false;
            }
            if (Swimming())
            {
                why = "swimming";
                return false;
            }
            if (GrapplingPoint.m_localGrappler != null)
            {
                why = "grappling";
                return false;
            }
            if (ValheimMoves() is string valheim)
            {
                why = valheim;
                return false;
            }
            if (!Map(Right, Left, out _, out _, out _))
            {
                why = "not an Elden Ring weapon";
                return false;
            }
            return true;
        }

        /// <summary>
        /// A sim set up for Valheim: its fall damage and stamina rules, guard
        /// while sprinting, the power-stance crouch attack. Turning is the
        /// sandbox's (on a dime).
        /// </summary>
        private SimPlayer NewSim(Vec3 pos, float yaw) => new SimPlayer(pos)
        {
            Yaw = yaw,
            GuardWhileSprinting = true,
            ExternalFallDamage = true,
            PairedCrouchAttack = true,
            CanAfford = CanAfford,
            CanAffordAir = CanAffordAir,
            // Valheim's fall damage passes 50 (of its 100 cap) beyond 12 m.
            HeavyLandingFall = 12f,
            EarlyRollAttacks = true,
            QueueCrouch = true,
            NoAirJumpBuffer = true,
            JumpAttackChains = true,
            NoFlinch = true,
            MinLandingFall = 1.5f,
            // Below Valheim's own fall-damage height (Character.c_MinFallHeight), a drop doesn't stop a run.
            StopLandingFall = 4f,
            StepDown = 0.6f,
            LightLandings = true,
            JumpTurnFrames = 6f,
            NoAirAttackBuffer = true,
            LandFollowUps = true,
            LandInPlace = true,
            ReverseBackstepFrames = 4f,
            BackstepAttackTurn = 1800f,
        };

        private void Enter()
        {
            Active = true;
            Sim = NewSim(Space.ToSim(_body.position), Space.YawToSim(transform.eulerAngles.y));
            // On at the speed Valheim was moving (out of the water, say), not from a standstill.
            Vector3 moving = _body.linearVelocity;
            Sim.Speed = Mathf.Min(new Vector3(moving.x, 0f, moving.z).magnitude, ErData.SPRINT_SPEED);
            _level.Self = _body;
            _carry = Vector3.zero;
            // Wading out of deep water with the run key down sprints on (a held key sprints, it never dodges).
            if (_vp.InWater())
            {
                _bindings.IgnoreHeld("Dodge", "Sprint");
            }
            else
            {
                _bindings.IgnoreHeld();
            }
            _acc = 0f;
            _tickFrom = _tickTo = _expected = _body.position;
            _yawFrom = _yawTo = transform.eulerAngles.y;
            // The rig first: the blade's axis is measured on its attach point.
            BuildPose();
            SyncEquipment(force: true);
            _steps?.Silence();
            ErSounds.Preload(_vp);
        }

        private void Exit()
        {
            Active = false;
            _body.useGravity = true;
            // Valheim's own motion (swimming, say) carries on at the speed the body had, not its own stale one.
            Vector3 moving = _body.linearVelocity;
            CurrentVel(_vp) = new Vector3(moving.x, 0f, moving.z);
            _combat.StopTrails();
            Hitboxes.Begin();
            Hitboxes.End();
            _vis?.SetWeaponTrails(false);
            RestoreHandVisuals();
            RestorePoles();
            LockTarget = null;
            Publish(false);
        }

        private void BuildPose()
        {
            if (_pose != null || Clips == null)
            {
                return;
            }
            // The body itself: skinned gear (knuckles, armour) shares its bone list
            // but not its bind pose, so "most bones" can pick the wrong mesh.
            SkinnedMeshRenderer body = _vis != null ? _vis.m_bodyModel : null;
            if (body == null)
            {
                GameObject visual = Traverse.Create(_vp).Field("m_visual").GetValue<GameObject>();
                body = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true).OrderByDescending(s => s.bones.Length).First();
            }
            _retarget = new Retarget(Clips, transform, body);
            var driven = new List<Transform>(_retarget.Bones);
            if (_retarget.RightAttach != null)
            {
                driven.Add(_retarget.RightAttach);
            }
            if (_retarget.LeftAttach != null)
            {
                driven.Add(_retarget.LeftAttach);
            }
            _pose = new PoseOutput(driven, _retarget.Hips, body, _retarget.Bones.Count);
            _chooser = new Chooser(Clips);
            _steps = new Steps(_vp, Clips);
            _local = new Quaternion[_retarget.Bones.Count];
            Plugin.Trace("pose: " + _retarget.Report);
        }

        /// <summary>
        /// Valheim's equipment → the sim's hands. A change of what a hand holds
        /// (equipping, sheathing, drawing) plays ER's swap for that hand: the
        /// right hand's if the right item changed, else the left's. The new
        /// item shows from the swap's change-over frame.
        /// </summary>
        private void SyncEquipment(bool force)
        {
            ItemDrop.ItemData right = Right, left = Left;
            if (!force && right == _lastRight && left == _lastLeft && Settings.MovesetVersion == _lastMovesets)
            {
                return;
            }
            bool rightChanged = right != _lastRight, settingChanged = Settings.MovesetVersion != _lastMovesets;
            _lastMovesets = Settings.MovesetVersion;
            _lastRight = right;
            _lastLeft = left;
            _lastGrip = (Grip)255;
            if (!Map(right, left, out int weapon, out int leftHand, out Grip grip))
            {
                return;
            }
            bool animate = !force && !settingChanged && !_syncInstantly && Sim.State != StateKind.Dead
                           && (weapon != Sim.Weapon || leftHand != Sim.Left || grip != Sim.Grip);
            _syncInstantly = false;
            if (animate)
            {
                Sim.SwapState = new Swap
                {
                    Kind = rightChanged ? SwapKind.NextWeapon : SwapKind.NextLeft,
                    F = 0f, Grip = grip, Weapon = weapon, Left = leftHand, Applied = false,
                };
                _swapTo = new Vector3Int(weapon, leftHand, (int)grip);
                _swapping = true;
                Log.Add($"swap: {Sim.SwapState.Value.Kind} to {ErData.Weapons[weapon].Name}/{ErData.Weapons[leftHand].Name} {grip}");
                return;
            }
            _swapping = false;
            Sim.SwapState = null;
            Sim.Weapon = weapon;
            Sim.Left = leftHand;
            Sim.Grip = grip;
        }

        private bool _syncInstantly, _swapping;
        private Vector3Int _swapTo;

        /// <summary>The next equipment change applies at once, with no swap (a thrown spear leaving the hand).</summary>
        public void SyncInstantly() => _syncInstantly = true;

        /// <summary>A swap of ours that was cut short (a hit cancels swaps) still lands its new hands.</summary>
        private void FinishSwap()
        {
            if (!_swapping || Sim.SwapState.HasValue)
            {
                return;
            }
            _swapping = false;
            Sim.Weapon = _swapTo.x;
            Sim.Left = _swapTo.y;
            Sim.Grip = (Grip)_swapTo.z;
        }

        /// <summary>Which hand's new item is still waiting for the swap's change-over frame (null: none).</summary>
        private SwapKind? SwapHiding()
        {
            if (_swapping && Sim.SwapState is Swap swap && !swap.Applied)
            {
                return swap.Kind;
            }
            return null;
        }

        private void FlushSwingLog()
        {
            if (_combat.SwingLog != null)
            {
                Log.Add(_combat.SwingLog);
                _combat.SwingLog = null;
            }
        }

        /// <summary>
        /// The held item's blade direction in the attach point's space (its
        /// longest extent, away from the grip), and how far along it the
        /// item reaches (from, to), in metres.
        /// </summary>
        /// <summary>
        /// One hand's blade of a skinned paired weapon: the vertices of the
        /// posed mesh nearer this hand than the other, in this hand's attach
        /// space; the axis points to its far end (the head).
        /// </summary>
        private struct SkinnedPair
        {
            public Vector3 AxisRight, AxisLeft, OffsetRight, OffsetLeft;
            public Vector2 RangeRight, RangeLeft;
            public float RadiusRight, RadiusLeft;
        }

        private static readonly Dictionary<string, SkinnedPair> SkinnedCache = new Dictionary<string, SkinnedPair>();

        /// <summary>The skinned item's vertices as posed now, in world space.</summary>
        private static List<Vector3> BakeSkinned(GameObject instance)
        {
            var world = new List<Vector3>();
            foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var baked = new Mesh();
                smr.BakeMesh(baked, true);
                foreach (Vector3 v in baked.vertices)
                {
                    world.Add(smr.transform.TransformPoint(v));
                }
                Object.Destroy(baked);
            }
            return world;
        }

        /// <summary>
        /// A blade along points in an attach's space: the axis points to their
        /// far end (the head), on a line through their middle (offset from the
        /// attach point, square to the axis).
        /// </summary>
        private static Vector3 FitBlade(List<Vector3> points, out Vector3 offset, out Vector2 range, out float radius)
        {
            offset = Vector3.zero;
            range = Vector2.zero;
            radius = 0f;
            if (points.Count == 0)
            {
                return Vector3.zero;
            }
            float far = points.Max(v => v.magnitude);
            Vector3 sum = Vector3.zero;
            foreach (Vector3 v in points.Where(v => v.magnitude >= far * 0.6f))
            {
                sum += v;
            }
            Vector3 axis = sum.normalized;
            Vector3 middle = Vector3.zero;
            foreach (Vector3 v in points)
            {
                middle += v;
            }
            middle /= points.Count;
            offset = middle - axis * Vector3.Dot(middle, axis);
            float reach = points.Max(v => Vector3.Dot(v, axis));
            float widest = 0f;
            foreach (Vector3 v in points)
            {
                float along = Vector3.Dot(v, axis);
                if (along >= reach * 0.6f)
                {
                    widest = Mathf.Max(widest, (v - offset - axis * along).magnitude);
                }
            }
            range = new Vector2(reach * 0.3f, reach);
            radius = widest + 0.02f;
            return axis;
        }

        /// <summary>
        /// Just over the widest the item gets around its axis in its far 40%
        /// (the head or point): its vertices where the mesh is readable, else
        /// its box's corners.
        /// </summary>
        private static float HeadRadius(Transform attach, Vector3 axis, float far)
        {
            float widest = 0f;
            foreach (MeshFilter mf in attach.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }
                IEnumerable<Vector3> points = mesh.isReadable ? mesh.vertices : Corners(mesh.bounds);
                foreach (Vector3 v in points)
                {
                    Vector3 local = attach.InverseTransformPoint(mf.transform.TransformPoint(v));
                    float along = Vector3.Dot(local, axis);
                    if (along >= far * 0.6f)
                    {
                        widest = Mathf.Max(widest, (local - axis * along).magnitude);
                    }
                }
            }
            return widest + 0.02f;
        }

        private Vector3 BladeAxis(Transform attach, ItemDrop.ItemData item, out Vector2 range, out float radius)
        {
            range = Vector2.zero;
            radius = 0f;
            if (attach == null || attach.childCount == 0)
            {
                return Vector3.zero;
            }
            var bounds = new Bounds();
            bool any = false;
            foreach (MeshFilter mf in attach.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                foreach (Vector3 corner in Corners(mf.sharedMesh.bounds))
                {
                    Vector3 local = attach.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(local);
                    }
                }
            }
            if (!any)
            {
                return Vector3.zero;
            }
            Vector3 size = bounds.size, c = bounds.center;
            Vector3 axis = size.x >= size.y && size.x >= size.z ? Vector3.right * Mathf.Sign(c.x) :
                           size.y >= size.z ? Vector3.up * Mathf.Sign(c.y) : Vector3.forward * Mathf.Sign(c.z);
            // The longest part's own long axis: an item can sit at a slant in
            // its attach point (Valheim's atgeirs are ~25° off its Z), and the
            // attach-space box only finds the nearest attach axis.
            MeshFilter longest = attach.GetComponentsInChildren<MeshFilter>().Where(m => m.sharedMesh != null)
                .OrderByDescending(m => m.transform.TransformVector(m.sharedMesh.bounds.size).magnitude).FirstOrDefault();
            if (longest != null)
            {
                Bounds mb = longest.sharedMesh.bounds;
                Vector3 e = mb.size;
                Vector3 meshAxis = e.x >= e.y && e.x >= e.z ? Vector3.right : e.y >= e.z ? Vector3.up : Vector3.forward;
                Vector3 along = attach.InverseTransformDirection(longest.transform.TransformDirection(meshAxis)).normalized;
                if (Vector3.Dot(along, axis) < 0f)
                {
                    along = -along;
                }
                Plugin.Trace($"blade: {item?.m_shared.m_name} mesh axis {along:F3}, {Vector3.Angle(along, axis):0.0}° from the attach box axis");
                axis = along;
            }
            if (item != null && item.m_shared.m_skillType == Skills.SkillType.Spears && axis.z > 0f)
            {
                // Every Valheim spear has its head towards the attach point's -Z (measured
                // over all 14). Splitnir and the wooden spear sit centred on the hand, so
                // the longer half doesn't tell which end the head is.
                axis = -axis;
            }
            float far = 0f;
            foreach (MeshFilter mf in attach.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh != null)
                {
                    foreach (Vector3 corner in Corners(mf.sharedMesh.bounds))
                    {
                        far = Mathf.Max(far, Vector3.Dot(attach.InverseTransformPoint(mf.transform.TransformPoint(corner)), axis));
                    }
                }
            }
            range = new Vector2(far * 0.3f, far);
            radius = HeadRadius(attach, axis, far);
            Plugin.Trace($"blade: {item?.m_shared.m_name} on {attach.name} bounds {bounds.center:F2} size {bounds.size:F2} → axis {axis}, reach {far:0.00} m, radius {radius:0.000} m");
            return axis;
        }

        private static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
            {
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            }
        }

        // --- What each hand shows ---------------------------------------------------

        /// <summary>
        /// rig.rs's show(): two-handing the left armament brings it over to
        /// the right hand (and the right weapon is put away); two-handing
        /// either puts the left hand's shield or weapon away.
        /// </summary>
        private void HandVisuals(SimPlayer p)
        {
            if (_vis == null || _retarget == null)
            {
                return;
            }
            GameObject right = RightInstance(_vis), left = LeftInstance(_vis);
            bool twoLeft = p.Grip == Grip.TwoHandLeft;
            // A Smithscript weapon is gone from the hand that threw it, and reforms there a moment later.
            (bool thrownRight, bool thrownLeft) = Combat.ThrownHands(p);
            if (left != null && _retarget.LeftAttach != null && _retarget.RightAttach != null)
            {
                Transform want = twoLeft ? _retarget.RightAttach : _retarget.LeftAttach;
                if (left.transform.parent != want)
                {
                    left.transform.SetParent(want, false);
                }
                bool show = (p.Grip == Grip.OneHand || twoLeft) && SwapHiding() != SwapKind.NextLeft && !(twoLeft ? thrownRight : thrownLeft);
                Show(left, show);
            }
            if (right != null)
            {
                Show(right, !twoLeft && SwapHiding() != SwapKind.NextWeapon && !thrownRight);
            }
            SheatheIdleHand(p);
            bool twinChanged = TwinBlade(p, twoLeft ? left : right, thrownLeft);
            // Re-measure the blades whenever what sits on an attach point changed.
            bool slide = false;
            if (twinChanged || p.Grip != _lastGrip || right != _lastRightInstance || left != _lastLeftInstance)
            {
                slide = true;
                RestorePoles();
                _lastGrip = p.Grip;
                _lastRightInstance = right;
                _lastLeftInstance = left;
                ItemDrop.ItemData inRight = twoLeft ? Left : Right;
                _bladeRight = BladeAxis(_retarget.RightAttach, inRight, out _rangeRight, out _radiusRight);
                _leftShield = Left != null && Left.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield;
                if (_chooser != null)
                {
                    // An empty left hand guards as ER's fist does: its EquipParamWeapon row
                    // (21000000) has guard style 2, the small shield's forearm raised in front.
                    _chooser.ShieldStyle = _leftShield ? ShieldStyle(Left) : Left == null ? 2 : 0;
                }
                _leftShield &= _twin == null;
                _bladeLeft = _twin != null ? BladeAxis(_retarget.LeftAttach, null, out _rangeLeft, out _radiusLeft)
                    : twoLeft || Left == null ? Vector3.zero
                    : _leftShield ? ShieldFace(_retarget.LeftAttach) : BladeAxis(_retarget.LeftAttach, Left, out _rangeLeft, out _radiusLeft);
                // Paired weapons (Skoll and Hati, the dual axes) are one skinned mesh on the body, nothing on the attach points:
                // each hand's blade is its half of that mesh. Knuckles and claws keep the fists.
                _skinnedBlades = false;
                _pairSplit = null;
                _offsetRight = _offsetLeft = Vector3.zero;
                if (Hands.IsPair(Right) && Right.m_shared.m_skillType != Skills.SkillType.Unarmed && _bladeRight == Vector3.zero)
                {
                    _pairKey = Right.m_dropPrefab != null ? Right.m_dropPrefab.name : Right.m_shared.m_name;
                    _pairSplit = null;
                    if (SkinnedCache.TryGetValue(_pairKey, out SkinnedPair pair))
                    {
                        UsePair(pair);
                    }
                    else
                    {
                        // Fists until the hands have moved apart enough to tell the halves apart (PairSplit).
                        _pairSplit = new PairSplit { Instance = right };
                    }
                }
                _rightBlade.Clear();
                _leftBlade.Clear();
            }
            if (slide && !_skinnedBlades)
            {
                _rangeRight = SlidePole(twoLeft ? left : right, twoLeft ? Left : Right, _retarget.RightAttach, _bladeRight, _rangeRight);
                if (!_leftShield && _bladeLeft != Vector3.zero)
                {
                    _rangeLeft = SlidePole(left, Left, _retarget.LeftAttach, _bladeLeft, _rangeLeft);
                }
            }
            _rightBlade.Item = twoLeft ? Left : Right;
            _rightBlade.Instance = twoLeft ? left : right;
            // A torch strikes as itself (its fire, Clubs), not as a bare fist.
            bool leftStrikes = Hands.IsLeftWeapon(Left) || Left != null && Left.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Torch;
            _leftBlade.Item = leftStrikes ? Left : null;
            _leftBlade.Instance = leftStrikes && !twoLeft ? left : null;
        }

        private GameObject _twin, _twinOf;

        /// <summary>
        /// Two-handing the Smithscript Dagger throws from both hands: the left
        /// shows a copy of the knife two-handed (either hand's goes to the right
        /// attach point), whatever is equipped there. True if the copy came or went.
        /// </summary>
        private bool TwinBlade(SimPlayer p, GameObject knife, bool thrownLeft)
        {
            bool want = p.Grip != Grip.OneHand && knife != null && _retarget.LeftAttach != null
                        && !Hands.IsPair(Right) && ErData.Weapons[p.Moveset().Weapon].Category == 53;
            bool changed = false;
            if (_twin != null && (!want || _twinOf != knife))
            {
                DropTwin();
                changed = true;
            }
            if (want && _twin == null)
            {
                _twinOf = knife;
                _twin = Instantiate(knife, _retarget.LeftAttach, false);
                foreach (Collider c in _twin.GetComponentsInChildren<Collider>(true))
                {
                    Destroy(c);
                }
                foreach (MeleeWeaponTrail t in _twin.GetComponentsInChildren<MeleeWeaponTrail>(true))
                {
                    Destroy(t);
                }
                changed = true;
            }
            if (_twin != null)
            {
                bool show = !thrownLeft && SwapHiding() != (p.Grip == Grip.TwoHandLeft ? SwapKind.NextLeft : SwapKind.NextWeapon);
                foreach (Renderer r in _twin.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = show;
                }
            }
            return changed;
        }

        private void DropTwin()
        {
            if (_twin != null)
            {
                // Gone at once: the blade's axis is measured from what's left on the attach point.
                _twin.transform.SetParent(null, false);
                Destroy(_twin);
            }
            _twin = null;
            _twinOf = null;
        }

        private bool _leftShield, _skinnedBlades;
        private string _pairKey;
        private PairSplit _pairSplit;

        /// <summary>
        /// A paired weapon's one skinned mesh, baked now and then: each vertex
        /// belongs to the hand it keeps still against (its drift in that hand's
        /// attach space stays near zero while the hands move apart).
        /// </summary>
        private sealed class PairSplit
        {
            public GameObject Instance;
            public Vector3[] R0, L0;
            public float[] DriftR, DriftL;
            public float Next;
        }

        private void UsePair(SkinnedPair pair)
        {
            _bladeRight = pair.AxisRight;
            _offsetRight = pair.OffsetRight;
            _offsetLeft = pair.OffsetLeft;
            _rangeRight = pair.RangeRight;
            _radiusRight = pair.RadiusRight;
            _bladeLeft = pair.AxisLeft;
            _rangeLeft = pair.RangeLeft;
            _radiusLeft = pair.RadiusLeft;
            _skinnedBlades = _bladeRight != Vector3.zero && _bladeLeft != Vector3.zero;
            _rightBlade.Clear();
            _leftBlade.Clear();
        }

        private void SplitPair()
        {
            PairSplit s = _pairSplit;
            if (s == null || Time.time < s.Next)
            {
                return;
            }
            s.Next = Time.time + 0.1f;
            if (s.Instance == null)
            {
                _pairSplit = null;
                return;
            }
            Transform ra = _retarget.RightAttach, la = _retarget.LeftAttach;
            List<Vector3> world = BakeSkinned(s.Instance);
            if (s.R0 == null || s.R0.Length != world.Count)
            {
                s.R0 = world.Select(w => ra.InverseTransformPoint(w)).ToArray();
                s.L0 = world.Select(w => la.InverseTransformPoint(w)).ToArray();
                s.DriftR = new float[world.Count];
                s.DriftL = new float[world.Count];
                return;
            }
            int sure = 0;
            for (int i = 0; i < world.Count; i++)
            {
                s.DriftR[i] = Mathf.Max(s.DriftR[i], (ra.InverseTransformPoint(world[i]) - s.R0[i]).magnitude);
                s.DriftL[i] = Mathf.Max(s.DriftL[i], (la.InverseTransformPoint(world[i]) - s.L0[i]).magnitude);
                if (Mathf.Abs(s.DriftR[i] - s.DriftL[i]) > 0.05f)
                {
                    sure++;
                }
            }
            if (sure < world.Count * 0.9f)
            {
                return;
            }
            var right = new List<Vector3>();
            var left = new List<Vector3>();
            for (int i = 0; i < world.Count; i++)
            {
                // Vertices still in doubt (by the wrists, or not yet moved) are left out.
                if (s.DriftL[i] - s.DriftR[i] > 0.05f)
                {
                    right.Add(s.R0[i]);
                }
                else if (s.DriftR[i] - s.DriftL[i] > 0.05f)
                {
                    left.Add(s.L0[i]);
                }
            }
            var pair = new SkinnedPair();
            pair.AxisRight = FitBlade(right, out pair.OffsetRight, out pair.RangeRight, out pair.RadiusRight);
            pair.AxisLeft = FitBlade(left, out pair.OffsetLeft, out pair.RangeLeft, out pair.RadiusLeft);
            Plugin.Trace($"blade: {_pairKey} split by motion: right {right.Count} vertices, reach {pair.RangeRight.y:0.00} m, radius {pair.RadiusRight:0.000} m; " +
                          $"left {left.Count}, reach {pair.RangeLeft.y:0.00} m, radius {pair.RadiusLeft:0.000} m");
            SkinnedCache[_pairKey] = pair;
            _pairSplit = null;
            UsePair(pair);
        }
        private int _lastMovesets;
        private readonly HashSet<GameObject> _hidden = new HashSet<GameObject>();

        /// <summary>
        /// Shows or hides a held item by its renderers. (Deactivating it would
        /// lose its swing trail for good: MeleeWeaponTrail destroys its mesh
        /// when disabled and only builds one in Start.)
        /// </summary>
        private void Show(GameObject item, bool show)
        {
            if (item == null || show != _hidden.Contains(item))
            {
                return;
            }
            if (show)
            {
                _hidden.Remove(item);
            }
            else
            {
                _hidden.Add(item);
            }
            foreach (Renderer r in item.GetComponentsInChildren<Renderer>(true))
            {
                r.enabled = show;
            }
            // A torch's light shines from a hidden torch too: off with it, and
            // its LightLod (which turns lights on by distance) paused; back as they were.
            if (!show)
            {
                var off = new List<Behaviour>();
                foreach (Light l in item.GetComponentsInChildren<Light>(true))
                {
                    LightLod lod = l.GetComponent<LightLod>();
                    if (lod != null && lod.enabled)
                    {
                        lod.enabled = false;
                        off.Add(lod);
                    }
                    else if (l.enabled && lod == null)
                    {
                        off.Add(l);
                    }
                    l.enabled = false;
                }
                _hiddenLights[item] = off.ToArray();
            }
            else if (_hiddenLights.TryGetValue(item, out Behaviour[] lights))
            {
                _hiddenLights.Remove(item);
                foreach (Behaviour b in lights.Where(b => b != null))
                {
                    b.enabled = true;
                }
            }
        }

        private readonly Dictionary<GameObject, Behaviour[]> _hiddenLights = new Dictionary<GameObject, Behaviour[]>();

        private bool Shown(GameObject item) => item != null && item.activeInHierarchy && !_hidden.Contains(item);

        /// <summary>
        /// Grip to tip of ER's pole classes: a pole is held where its tip reaches as far.
        /// The halberds' are measured from the game's models (wp_a_1001, 1003, 0907: the
        /// box's far end from the model's origin, the grip); the rest follow tools/extract.py BLADES.
        /// </summary>
        private static readonly Dictionary<string, float> PoleTip = new Dictionary<string, float>
        {
            { "Halberd", 1.87f }, { "Banished Knight's Halberd", 1.90f }, { "Short Spear", 1.66f }, { "Smithscript Spear", 1.66f }, { "Great Spear", 2.26f },
            { "Guardian's Swordspear", 1.84f }, { "Cross-Naginata", 2.0f },
        };

        private readonly Dictionary<GameObject, Vector3> _poleRest = new Dictionary<GameObject, Vector3>();

        /// <summary>
        /// Valheim's poles are gripped elsewhere along the shaft than ER's (an
        /// atgeir reaches 2.24 m past the hand, ER's halberd 1.85; a spear 1.12,
        /// ER's 1.66): slide the item along its own axis until the tip reaches
        /// ER's, so it neither digs into the ground nor puts the hand at the tip.
        /// Returns the item's reach, moved by as much.
        /// </summary>
        private Vector2 SlidePole(GameObject instance, ItemDrop.ItemData item, Transform attach, Vector3 axis, Vector2 range)
        {
            if (instance == null || item == null || axis == Vector3.zero)
            {
                return range;
            }
            string cls = MovesetClass(item);
            if (cls == null || !PoleTip.TryGetValue(cls, out float tip))
            {
                return range;
            }
            float shift = tip - range.y;
            if (!_poleRest.ContainsKey(instance))
            {
                _poleRest[instance] = instance.transform.localPosition;
            }
            Vector3 along = instance.transform.parent.InverseTransformVector(attach.TransformVector(axis));
            instance.transform.localPosition = _poleRest[instance] + along.normalized * shift;
            Plugin.Trace($"pole: {item.m_shared.m_name} slid {shift:+0.00;-0.00} m (tip {range.y:0.00} → {tip:0.00})");
            return range + Vector2.one * shift;
        }

        private void RestorePoles()
        {
            foreach (var kv in _poleRest)
            {
                if (kv.Key != null)
                {
                    kv.Key.transform.localPosition = kv.Value;
                }
            }
            _poleRest.Clear();
        }

        /// <summary>
        /// A shield's outer face in its attach point's space: its thinnest
        /// extent, on the side the shield's bulk sits (the hand is behind it).
        /// </summary>
        private static Vector3 ShieldFace(Transform attach)
        {
            if (attach == null)
            {
                return Vector3.zero;
            }
            var bounds = new Bounds();
            bool any = false;
            foreach (MeshFilter mf in attach.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                foreach (Vector3 corner in Corners(mf.sharedMesh.bounds))
                {
                    Vector3 local = attach.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (!any)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        bounds.Encapsulate(local);
                    }
                }
            }
            if (!any)
            {
                return Vector3.zero;
            }
            Vector3 size = bounds.size, c = bounds.center;
            Vector3 face = size.x <= size.y && size.x <= size.z ? Vector3.right * Sign(c.x) :
                           size.y <= size.z ? Vector3.up * Sign(c.y) : Vector3.forward * Sign(c.z);
            Plugin.Trace($"shield: bounds {bounds.center:F2} size {bounds.size:F2} → face {face}");
            return face;
        }

        private static float Sign(float v) => v < 0f ? -1f : 1f;

        /// <summary>The ER left weapon bone's axis the left item lines up with (see Retarget.LeftAttachRotation).</summary>
        public static Vector3 LeftBladeAxis = Vector3.down, ShieldFaceAxis = Vector3.forward;

        private static readonly System.Reflection.MethodInfo AttachBackItem = AccessTools.Method(typeof(VisEquipment), "AttachBackItem");
        private static readonly System.Reflection.MethodInfo TriggerEquipEffect = AccessTools.Method(typeof(Humanoid), "TriggerEquipEffect");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> RightBack = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_rightBackItemInstance");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> LeftBack = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_leftBackItemInstance");
        private ItemDrop.ItemData _sheathed;
        private GameObject _sheathedBack;
        private bool _sheathMirrored;
        private Transform _sheathBone;
        private Matrix4x4 _sheathLocal;
        private Grip _sheathGrip;

        /// <summary>
        /// Two-handing one weapon puts the other hand's item (weapon or
        /// shield) on the back, where Valheim keeps it sheathed, with Valheim's
        /// equip sound; going back to one hand draws it again.
        /// </summary>
        private void SheatheIdleHand(SimPlayer p)
        {
            ItemDrop.ItemData idle = p.Grip == Grip.TwoHandRight ? Left : p.Grip == Grip.TwoHandLeft ? Right : null;
            if (Hands.IsPair(Right))
            {
                idle = null;
            }
            if (idle == _sheathed && (_sheathedBack != null || idle == null))
            {
                _sheathGrip = p.Grip;
                return;
            }
            bool gripChanged = _sheathGrip != p.Grip && (Right == _lastRight && Left == _lastLeft);
            if (_sheathedBack != null)
            {
                Destroy(_sheathedBack);
                _sheathedBack = null;
            }
            ItemDrop.ItemData sound = idle ?? _sheathed;
            _sheathed = idle;
            _sheathGrip = p.Grip;
            if (idle != null && idle.m_dropPrefab != null && AttachBackItem != null)
            {
                _sheathedBack = AttachBackItem.Invoke(_vis, new object[] { idle.m_dropPrefab.name.GetStableHashCode(), idle.m_variant, idle == Right, idle.m_quality }) as GameObject;
            }
            // Valheim hangs a one-handed weapon from either hand where the main
            // hand's goes: the off-hand one hangs mirrored, as in power stance.
            _sheathMirrored = false;
            if (_sheathedBack != null && idle == Left && Hands.IsHandWeapon(Left))
            {
                FaceOut(_sheathedBack.transform);
                _sheathMirrored = Mirrored(_sheathedBack.transform, out _sheathBone, out _sheathLocal);
            }
            // The owner's equip effect is networked: a remote copy would play it twice.
            if (gripChanged && sound != null && !Remote)
            {
                TriggerEquipEffect?.Invoke(_vp, new object[] { sound });
            }
        }

        /// <summary>
        /// Sheathed in power stance, both weapons hang on the same back point:
        /// the left one is mirrored across the spine so the two cross.
        /// </summary>
        private void CrossSheathed()
        {
            if (_sheathMirrored && _sheathedBack != null)
            {
                Hang(_sheathedBack.transform, _sheathBone, _sheathLocal);
            }
            GameObject right = _vis != null ? RightBack(_vis) : null, left = _vis != null ? LeftBack(_vis) : null;
            if (right == null || left == null || right.transform.parent != left.transform.parent)
            {
                _crossRight = null;
                return;
            }
            Transform r = right.transform;
            if (r != _crossRight || left.transform != _crossLeft)
            {
                if (r != _facedRight)
                {
                    // Once per item: FaceOut turns it half a turn, and Mirrored can fail a frame (it would turn back).
                    FaceOut(r);
                    _facedRight = r;
                }
                if (!Mirrored(r, out _crossBone, out _crossLocal))
                {
                    return;
                }
                _crossRight = r;
                _crossLeft = left.transform;
            }
            Hang(left.transform, _crossBone, _crossLocal);
        }

        private static void Hang(Transform item, Transform bone, Matrix4x4 local)
        {
            Matrix4x4 m = bone.localToWorldMatrix * local;
            item.SetPositionAndRotation(m.GetColumn(3), m.rotation);
        }

        /// <summary>
        /// Where <paramref name="from"/>'s mirror image across the spine hangs,
        /// relative to the skeleton bone it hangs from. Taken in the bind pose,
        /// once (the body's pose as it is sheathed must not count).
        /// </summary>
        private bool Mirrored(Transform from, out Transform bone, out Matrix4x4 local)
        {
            local = Matrix4x4.identity;
            bone = SkinnedAncestor(from, out Matrix4x4 bind);
            if (bone == null)
            {
                return false;
            }
            Matrix4x4 mirror = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            Matrix4x4 inBind = bind * bone.worldToLocalMatrix * from.localToWorldMatrix;
            local = bind.inverse * (mirror * inBind * mirror);
            return true;
        }

        /// <summary>
        /// Turns a hanging item half a turn about its shaft if its head's heavy
        /// side (an axe's blade) faces the body's middle, so it faces outward and
        /// its mirror image faces outward on the other side. The shaft is the
        /// longest mesh's own long axis through the grip (the item's origin):
        /// items can sit at a slant (atgeirs ~25°), so not the item's own axes.
        /// Items with no heavy side (swords) are left alone.
        /// </summary>
        private void FaceOut(Transform item)
        {
            MeshFilter longest = item.GetComponentsInChildren<MeshFilter>().Where(m => m.sharedMesh != null)
                .OrderByDescending(m => m.transform.TransformVector(m.sharedMesh.bounds.size).magnitude).FirstOrDefault();
            if (longest == null)
            {
                return;
            }
            Vector3 e = longest.sharedMesh.bounds.size;
            Vector3 meshAxis = e.x >= e.y && e.x >= e.z ? Vector3.right : e.y >= e.z ? Vector3.up : Vector3.forward;
            Vector3 shaft = item.InverseTransformDirection(longest.transform.TransformDirection(meshAxis)).normalized;
            Bounds b = default;
            bool any = false;
            foreach (MeshFilter mf in item.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                foreach (Vector3 corner in Corners(mf.sharedMesh.bounds))
                {
                    Vector3 at = item.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    if (any)
                    {
                        b.Encapsulate(at);
                    }
                    else
                    {
                        b = new Bounds(at, Vector3.zero);
                        any = true;
                    }
                }
            }
            Vector3 c = b.center, side = c - shaft * Vector3.Dot(c, shaft);
            if (item.TransformVector(side).magnitude < 0.03f)
            {
                return;
            }
            // In the character's space: +x is its right. Outward = the side the head hangs on.
            float headX = transform.InverseTransformPoint(item.TransformPoint(c)).x;
            float sideX = transform.InverseTransformDirection(item.TransformDirection(side)).x;
            if (headX * sideX < 0f)
            {
                item.localRotation *= Quaternion.AngleAxis(180f, shaft);
            }
        }

        /// <summary>The nearest skinned body bone above t, and its bind pose in the character's own space.</summary>
        private Transform SkinnedAncestor(Transform t, out Matrix4x4 bind)
        {
            bind = Matrix4x4.identity;
            SkinnedMeshRenderer body = _vis != null ? _vis.m_bodyModel : null;
            if (body == null)
            {
                return null;
            }
            for (Transform p = t.parent; p != null; p = p.parent)
            {
                int i = System.Array.IndexOf(body.bones, p);
                if (i >= 0 && i < body.sharedMesh.bindposes.Length)
                {
                    bind = transform.worldToLocalMatrix * body.transform.localToWorldMatrix * body.sharedMesh.bindposes[i].inverse;
                    return p;
                }
            }
            return null;
        }

        private Transform _crossRight, _crossLeft, _crossBone, _facedRight;
        private Matrix4x4 _crossLocal;

        private void RestoreHandVisuals()
        {
            if (_sheathedBack != null)
            {
                Destroy(_sheathedBack);
                _sheathedBack = null;
            }
            _sheathed = null;
            if (_vis == null || _retarget == null)
            {
                return;
            }
            DropTwin();
            GameObject right = RightInstance(_vis), left = LeftInstance(_vis);
            if (left != null && _retarget.LeftAttach != null)
            {
                left.transform.SetParent(_retarget.LeftAttach, false);
                left.SetActive(true);
            }
            right?.SetActive(true);
            foreach (GameObject item in _hidden.ToList())
            {
                Show(item, true);
            }
            _hidden.Clear();
            _lastGrip = (Grip)255;
        }

        /// <summary>Where each blade is in the world now (after the last pose), for the item sweep.</summary>
        private void TrackBlades()
        {
            if (_retarget == null)
            {
                return;
            }
            SplitPair();
            // Each hand on its own: a bare hand, or knuckles/claws (skinned onto
            // both hands), strikes with the hand itself; a held item with its blade.
            bool skinnedPair = Hands.IsPair(Right) && !_skinnedBlades;
            // What the right hand holds: two-handing the left armament brings it over.
            ItemDrop.ItemData inRight = Sim.Grip == Grip.TwoHandLeft ? Left : Right;
            if (inRight == null || skinnedPair)
            {
                TrackFist(_rightBlade, "RightHand", "RightHandMiddle1");
            }
            else
            {
                Track(_rightBlade, _retarget.RightAttach, _bladeRight, _rangeRight, _offsetRight);
                _rightBlade.Radius = _radiusRight;
            }
            // A bare left hand is a fist whatever the grip: bare fists held two-handed
            // still strike with both (fist two-handed Light2's second blow is the left).
            if (_skinnedBlades)
            {
                // A paired weapon's left half.
                _leftBlade.Item = Right;
                _leftBlade.Instance = _rightBlade.Instance;
                Track(_leftBlade, _retarget.LeftAttach, _bladeLeft, _rangeLeft, _offsetLeft);
                _leftBlade.Radius = _radiusLeft;
            }
            else if (skinnedPair || (Sim.Grip == Grip.OneHand ? Left == null : inRight == null))
            {
                // Two-handed bare fists put the other hand's item on the back: that hand strikes bare too.
                _leftBlade.Item = skinnedPair ? Right : null;
                if (!skinnedPair)
                {
                    _leftBlade.Instance = null;
                }
                TrackFist(_leftBlade, "LeftHand", "LeftHandMiddle1");
            }
            else if (Sim.Grip != Grip.OneHand)
            {
                // Both hands on one two-handed weapon: it strikes with the right hand's blade.
                _leftBlade.Clear();
            }
            else
            {
                Track(_leftBlade, _retarget.LeftAttach, _bladeLeft, _rangeLeft);
                _leftBlade.Radius = _radiusLeft;
            }
        }

        private const float FIST_REACH = 0.3f;

        /// <summary>
        /// A bare (or clawed) hand strikes along the whole forearm, elbow to
        /// past the knuckles: the fist moves include elbow strikes.
        /// </summary>
        private void TrackFist(Combat.Blade b, string wrist, string knuckle)
        {
            Transform w = _retarget.Bones.Find(t => t.name == wrist), k = _retarget.Bones.Find(t => t.name == knuckle);
            if (w == null || k == null)
            {
                b.Clear();
                return;
            }
            Vector3 dir = (k.position - w.position).normalized;
            Vector3 from = w.parent != null ? w.parent.position : w.position;
            b.Move(from, k.position + dir * FIST_REACH);
            b.Radius = 0f;
        }

        private void Track(Combat.Blade b, Transform attach, Vector3 axis, Vector2 range, Vector3 offset = default)
        {
            if (attach == null || axis == Vector3.zero || !Shown(b.Instance))
            {
                b.Clear();
                return;
            }
            b.Move(attach.TransformPoint(offset + axis * range.x), attach.TransformPoint(offset + axis * range.y));
        }

        // --- Every frame ---------------------------------------------------------

        private void Update()
        {
            // Less than deltaTime after an extra physics step's pose (PoseForStep), which already moved the pose's timers on.
            float poseDt = Time.time - _posedAt;
            _posedAt = Time.time;
            if (Remote)
            {
                if (Active && Sim != null)
                {
                    Pose(Sim, poseDt, (Time.time - Time.fixedTime) / Consts.DT, _clock + (Time.time - Time.fixedTime));
                }
                WritePose();
                return;
            }
            if (_vp != VPlayer.m_localPlayer)
            {
                return;
            }
            // Here, not in Awake: every player's character has a Controller, and the local player is known only after spawning.
            Local = this;
            HookBeforeCloth();
            if (!Settings.Enabled.Value)
            {
                Hands.Release(_vp);
            }
            else
            {
                Game.m_staminaRegenRate = Settings.StaminaRegenRate.Value / 100f;
            }
            bool wanted = Wanted(out Why);
            if (wanted && !Active)
            {
                Enter();
            }
            else if (!wanted && Active)
            {
                Exit();
            }
            if (!Active)
            {
                FadeOutPose(Time.deltaTime);
                WritePose();
                return;
            }
            SyncEquipment(force: false);
            bool input = TakeInput != null && (bool)TakeInput.Invoke(_vp.GetComponent<PlayerController>(), new object[] { false });
            // Valheim lets the player walk with the inventory or map open; only attacks are held back.
            bool menu = InventoryGui.IsVisible() || Minimap.IsOpen() || StoreGui.IsVisible() || Hud.IsPieceSelectionVisible();
            float camYaw = GameCamera.instance != null ? GameCamera.instance.transform.eulerAngles.y : transform.eulerAngles.y;
            // A gamepad gets no input at all with the inventory open (Valheim's TakeInput);
            // LeftJoystickInventory and DodgeInInventory let the stick walk and B dodge there.
            bool padInventory = !input && ZInput.IsGamepadActive() && InventoryGui.IsVisible() && !Menu.IsVisible()
                                && (Settings.LeftJoystickInventory.Value || Settings.DodgeInInventory.Value);
            _bindings.Gather(input || padInventory || Bindings.ForcedStick.HasValue || Bindings.Forced.Count > 0, menu, camYaw, padInventory);
            LockSwitching(input && !menu);
            // Every rendered frame, at this frame's point between sim ticks (the
            // physics step runs at 50 Hz: a pose made there steps visibly).
            Pose(Sim, poseDt, (_acc + Time.time - Time.fixedTime) / Consts.DT, _clock + (Time.time - Time.fixedTime));
            // Written here, before Unity updates particles (and cloth): a torch's
            // flame emitted from where Valheim's animator had left the hand
            // trailed fire between the two places.
            WritePose();
        }

        // MagicaCloth simulates capes just before the LateUpdate scripts
        // (MagicaManager.beforeLateUpdateDelegate): a pose written in LateUpdate
        // reaches the cloth a frame late, against a body it wasn't simulated
        // on, and capes thrash. The pose is written ahead of the cloth, first
        // in its list (re-hooked every frame: others may reorder it), and in
        // LateUpdate only on a frame the hook didn't run.
        private static readonly MagicaCloth2.MagicaManager.UpdateMethod BeforeCloth = () =>
        {
            foreach (Controller c in All)
            {
                c.WritePose();
            }
        };
        private static MagicaCloth2.MagicaManager.UpdateMethod _hooked;
        private int _posedFrame = -1;

        private void WritePose()
        {
            if (_posedFrame == Time.frameCount)
            {
                return;
            }
            _posedFrame = Time.frameCount;
            _pose?.Write();
        }

        private float _posedAt;
        private int _stepFrame = -1;

        /// <summary>Before each physics step (MonoUpdaters.FixedUpdate, whose last updater is the swing trails).</summary>
        internal static void BeforePhysicsStep()
        {
            foreach (Controller c in All)
            {
                try
                {
                    c.PoseForStep();
                }
                catch (System.Exception e)
                {
                    // Thrown from here it would stop every Valheim updater in this physics step.
                    Plugin.Report("pose for a physics step failed: " + e);
                }
            }
        }

        /// <summary>
        /// Valheim's animator runs after each physics step and overwrites the
        /// bones. A slow frame has several steps, and from the second on the
        /// swing trail and the blades would read Valheim's pose (the tip 2 m off
        /// the swing): those steps get the pose at their own time. Not outside
        /// attacks, where nothing reads the bones in the physics step.
        /// </summary>
        private void PoseForStep()
        {
            // The frame's first step still has the pose the last Update wrote.
            if (_stepFrame != Time.frameCount)
            {
                _stepFrame = Time.frameCount;
                return;
            }
            if (!Active || Sim == null || Sim.State != StateKind.Act && !Sim.AirAttackState.HasValue)
            {
                return;
            }
            // This step hasn't run yet: the sim and clock are a step behind Time.time here.
            float step = Time.fixedDeltaTime;
            Pose(Sim, Time.time - _posedAt, (_acc + step) / Consts.DT, _clock + step, feet: false);
            _posedAt = Time.time;
            _pose?.Write();
        }

        private static bool _clothFailed;

        private static void HookBeforeCloth()
        {
            if (_clothFailed)
            {
                return;
            }
            try
            {
                MagicaCloth2.MagicaManager.UpdateMethod now = MagicaCloth2.MagicaManager.beforeLateUpdateDelegate;
                if (now != null && ReferenceEquals(now, _hooked))
                {
                    return;
                }
                System.Delegate[] list = now?.GetInvocationList();
                if (list == null || list.Length == 0 || !list[0].Equals(BeforeCloth))
                {
                    var rest = (MagicaCloth2.MagicaManager.UpdateMethod)System.Delegate.Remove(now, BeforeCloth);
                    MagicaCloth2.MagicaManager.beforeLateUpdateDelegate = (MagicaCloth2.MagicaManager.UpdateMethod)System.Delegate.Combine(BeforeCloth, rest);
                }
                _hooked = MagicaCloth2.MagicaManager.beforeLateUpdateDelegate;
            }
            catch (System.Exception e)
            {
                // Without MagicaCloth the LateUpdate write still poses the body.
                Plugin.Report("cloth hook failed: " + e.Message);
                _clothFailed = true;
            }
        }

        private void LateUpdate()
        {
            WritePose();
            if (Settings.Enabled.Value)
            {
                // Also while Valheim moves the player (a workbench): two sheathed weapons hang crossed, not one over the other.
                CrossSheathed();
            }
            if (!Active)
            {
                return;
            }
            // Locked on, the camera (which follows the player's look) turns to the target.
            Vector3? at = LockPoint();
            if (at.HasValue)
            {
                Vector3 dir = at.Value - _vp.GetEyePoint();
                dir.y = Mathf.Min(dir.y, 0f) - 0.25f * dir.magnitude * 0.3f;
                _vp.SetLookDir(Vector3.Slerp(_vp.GetLookDir(), dir.normalized, 1f - Mathf.Exp(-12f * Time.deltaTime)));
            }
            else if (TakeRecenter())
            {
                _vp.SetLookDir(transform.forward, 0.2f);
            }
        }

        // --- The physics step (Character.UpdateMotion is replaced by this) -------

        public void FixedTick(float dt)
        {
            _clock += dt;
            RootMotion(_vp) = Vector3.zero;
            // Valheim's own landing (fall damage, land sound) reads the highest
            // point since the ground: track it as its UpdateMotion would.
            // The two timers the replaced UpdateMotion advances: without them
            // OnCollisionStay ignores every contact (m_jumpTimer < 0.1), so
            // Valheim never touches ground: no fall damage, no ground material.
            LastGroundTouch(_vp) += dt;
            JumpTimer(_vp) += dt;
            if (!_vp.IsOnGround())
            {
                MaxAirAltitude(_vp) = Mathf.Max(MaxAirAltitude(_vp), _body.position.y);
            }
            Running(_vp) = false;
            _body.useGravity = false;
            _steps?.Silence();

            // Collision is PhysX's: wherever the body fell short of where it
            // was sent, the sim is held back by as much.
            Vector3 miss = _body.position - _expected;
            if (miss.magnitude > 2f)
            {
                // Teleported (or pushed far): start again from the body.
                Sim.Pos = Space.ToSim(_body.position);
                _tickFrom = _tickTo = _expected = _body.position;
            }
            else
            {
                if (Mathf.Abs(miss.y) <= 0.3f)
                {
                    miss.y = 0f;
                }
                if (new Vector2(miss.x, miss.z).magnitude > 0.01f || miss.y != 0f)
                {
                    Vec3 shift = Space.ToSim(_expected + miss) - Space.ToSim(_expected);
                    Sim.Pos.X += shift.X;
                    Sim.Pos.Z += shift.Z;
                    Sim.Pos.Y += shift.Y;
                    _tickFrom += miss;
                    _tickTo += miss;
                }
            }

            // Valheim's pushback (traps only, see Patches.RpcDamaged): at 20 m/s its way while it lasts.
            Vector3 push = PushForce(_vp);
            if (push != Vector3.zero && Sim.State != StateKind.Dead)
            {
                _level.Slide(ref Sim.Pos, Space.ToSim(push.normalized * (20f * dt)));
            }
            // Standing on a moving body (a ship's deck, a lox's back): carried along,
            // as Character.ApplyGroundForce. Off it (a jump, a fall) its speed carries on until the ground.
            if (!Sim.Airborne() && Sim.State != StateKind.Air)
            {
                Rigidbody under = _level.GroundBody;
                _carry = under != null ? under.GetPointVelocity(_body.position) : Vector3.zero;
                _carry.y = 0f;
            }
            Sim.Pos.X -= _carry.x * dt;
            Sim.Pos.Z += _carry.z * dt;

            _acc += dt;
            while (_acc >= Consts.DT)
            {
                _acc -= Consts.DT;
                _tickFrom = Space.ToUnity(Sim.Pos);
                _yawFrom = Space.YawToUnityDeg(Sim.Yaw);
                Tick(_bindings.Take());
                _tickTo = Space.ToUnity(Sim.Pos);
                _yawTo = Space.YawToUnityDeg(Sim.Yaw);
            }

            GroundContact();

            SimPlayer p = Sim;
            float alpha = Mathf.Clamp01(_acc / Consts.DT);
            Vector3 target = Vector3.Lerp(_tickFrom, _tickTo, alpha);
            _body.linearVelocity = (target - _body.position) / dt;
            _expected = target;
            _body.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(_yawFrom, _yawTo, alpha), 0f);
            HandVisuals(p);
            Publish(true);
            DrawHitboxes();
            if (_chooser != null && _chooser.Shown != null)
            {
                Vector3 v = (_tickTo - _tickFrom) / Consts.DT;
                _steps?.Feet(p, _chooser.Shown, new Vector2(v.x, v.z).magnitude);
            }
        }

        /// <summary>
        /// Valheim's ground contact from the sim's ground (OnCollisionStay sees
        /// few contacts of a body moved by velocity without gravity): footstep
        /// and landing materials, landings and fall damage (UpdateGroundContact),
        /// the slide sound, and steps on steep ground sprinted up (wall run).
        /// </summary>
        private void GroundContact()
        {
            bool grounded = !Sim.Airborne() && Sim.State != StateKind.Air && Sim.State != StateKind.Dead;
            if (grounded && _level.GroundCollider != null)
            {
                HasGroundContact(_vp) = true;
                ContactNormal(_vp) = _level.GroundNormal;
                ContactPoint(_vp) = _level.GroundPoint;
                LowestContact(_vp) = _level.GroundCollider;
            }
            Sliding(_vp) = grounded && _level.Slippage > 0.5f;
            WallRunning(_vp) = grounded && Sim.Sprinting && _level.OnSteep;
        }

        /// <summary>HitboxDebug: each held item's hit capsule always (green, red while a hit window is open) and the sim's ER blade capsule in its windows (yellow).</summary>
        private void DrawHitboxes()
        {
            Hitboxes.Begin();
            if (Settings.HitboxDebug.Value && Active)
            {
                Color c = _combat.Live ? Hitboxes.Live : Hitboxes.Idle;
                foreach (Combat.Blade b in new[] { _rightBlade, _leftBlade })
                {
                    if (b.Valid)
                    {
                        Hitboxes.Capsule(b.A, b.B, Mathf.Max(b.Radius, _combat.Live ? _combat.Radius : 0.05f), c);
                    }
                }
                if (_combat.Live)
                {
                    foreach ((Vector3 a, Vector3 e) in _combat.ErCapsules)
                    {
                        Hitboxes.Capsule(a, e, _combat.Radius, Hitboxes.ErBlade);
                    }
                }
                var aoe = _combat.AoeShown;
                if (Time.time - aoe.Time < 0.5f)
                {
                    // The slam's sphere, as a flat disc seen from above: a short fat capsule.
                    Hitboxes.Capsule(aoe.At - Vector3.up * 0.01f, aoe.At + Vector3.up * 0.01f, aoe.Radius, Hitboxes.Live);
                }
            }
            if (Settings.IframeDebug.Value && Active && Sim.Invincible())
            {
                // The whole body, while hits pass through it.
                Hitboxes.Capsule(transform.position + Vector3.up * 0.35f, transform.position + Vector3.up * 1.55f, 0.35f, Hitboxes.Iframes);
            }
            else if (Settings.IframeDebug.Value && Active && LegsClear() && _retarget?.Hips != null)
            {
                // Only the legs, in a jump: blows below the hips pass under.
                Hitboxes.Capsule(transform.position + Vector3.up * 0.15f, _retarget.Hips.position, 0.25f, Hitboxes.Iframes);
            }
            Hitboxes.End();
        }

        private void Tick(Input inp)
        {
            Exhaustion();
            // The sim's stamina is a view of Valheim's, in the sandbox's units.
            float scale = ErData.MAX_STAMINA / Mathf.Max(_vp.GetMaxStamina(), 1f);
            Sim.Stamina = _vp.GetStamina() * scale;
            // As Valheim: asking to sprint, roll or jump on an empty bar flashes it.
            if (Sim.Stamina <= 0f && (inp.Jump.Pressed || inp.Dodge.Held || inp.Dodge.Released))
            {
                Hud.instance?.StaminaBarEmptyFlash();
            }
            Sim.Hp = ErData.MAX_HP;
            _level.CastFrom = Space.ToUnity(Sim.Pos).y + 1.2f;

            TrackBlades();
            Locking(inp);
            FinishSwap();
            if (Hands.IsPair(Right))
            {
                // Paired weapons are one item for both hands: always held as the pair.
                inp.TwoHandRight = inp.TwoHandLeft = false;
            }
            if (_leftShield)
            {
                // A shield is never held two-handed.
                inp.TwoHandLeft = false;
            }
            Sim.EmptyHandGuards = Settings.EmptyHandGuard.Value;
            Sim.InstantJump = Settings.InstantJump.Value;
            Sim.InstantSprint = Settings.SeparateSprint.Value;
            // Paired weapons work as claws do: light attacks (their power-stance
            // set), guard on the guard button.
            Sim.PairedOnLight = Hands.IsPair(Right) && Right.m_shared.m_skillType != Skills.SkillType.Unarmed;
            Sim.HeavyDebounce = 1f;
            Sim.DodgeIframes = Settings.ValheimIframes.Value ? ValheimDodgeFrames() : 0f;
            if (_vp.IsEncumbered() || StatusSpeed() <= 0f)
            {
                // As Valheim: encumbered (or held fast, Immobilized) there's no jumping, dodging or running.
                inp.Jump = default;
                inp.Dodge = default;
            }
            AirAndSkills(inp);
            Character aim = LockTarget != null ? LockTarget : AutoTarget(inp);
            Vec3? target = aim != null ? Space.ToSim(aim.transform.position) : ThrowFacing();
            bool wasActing = Sim.State == StateKind.Act;
            ActionId wasAct = Sim.Act.Id;
            float wasF = Sim.Act.F;
            bool wasAir = Sim.AirAttackState.HasValue;
            _level.NoSlide = Sim.Sprinting;
            Vec3 before = Sim.Pos;
            Sim.Step(inp, _level, target, InCombat());
            Ice(before);
            Combat.AimAt = LockTarget != null ? LockTarget.GetCenterPoint() : (Vector3?)null;
            _combat.AfterTick(_vp, Sim, _rightBlade, _leftBlade, Log);
            FlushSwingLog();
            Steps.Started started = _steps?.AfterTick(Sim) ?? Steps.Started.None;
            GuardCounterSound();

            // Stamina is Valheim's: its own costs for moving, the weapon's for attacking.
            switch (started)
            {
                case Steps.Started.Jump:
                    JumpStamina();
                    // As Character.Jump: leave the ground behind for a moment.
                    LastGroundTouch(_vp) = 1f;
                    JumpTimer(_vp) = 0f;
                    break;
                case Steps.Started.Dodge:
                    Use("dodge", DodgeStamina?.Invoke(_vp, null) is float d ? d : _vp.m_dodgeStaminaUsage);
                    _vp.RaiseSkill(Skills.SkillType.Dodge, 0.1f);
                    break;
            }
            if (Sim.Sprinting && Sim.State == StateKind.Ground)
            {
                RunStamina(Consts.DT);
            }
            ChargeAttacks(wasActing && wasAct.Equals(Sim.Act.Id) && Sim.Act.F >= wasF ? wasF : -1f, wasAir);
            PauseRegen();
            Sneak(Consts.DT);
            NoiseFor(Sim);
        }

        private static readonly AccessTools.FieldRef<VPlayer, float> StaminaField = AccessTools.FieldRefAccess<VPlayer, float>("m_stamina");
        private static readonly AccessTools.FieldRef<VPlayer, float> RegenTimer = AccessTools.FieldRefAccess<VPlayer, float>("m_staminaRegenTimer");

        /// <summary>
        /// Valheim stops regenerating only in its own attacks and dodges: here,
        /// whenever the sim doesn't (an action before its recovery frame, a
        /// press waiting for one, the air), Valheim's regeneration waits too.
        /// </summary>
        private void PauseRegen()
        {
            bool waiting = Sim.Buffer == Req.Light || Sim.Buffer == Req.Heavy || Sim.Buffer == Req.Left;
            if (!Sim.Regenerating || waiting)
            {
                RegenTimer(_vp) = Mathf.Max(RegenTimer(_vp), 0.05f);
            }
        }

        // Running dry in an attack: the bar stays empty until that attack is
        // over, so nothing can start meanwhile and the chain can't go on (the
        // next press is a first attack). Then Valheim's regeneration as usual.
        private bool _exhausted;

        /// <summary>
        /// An attack's cost. Regeneration restarts from the sim's recovery
        /// frame (PauseRegen), not after Valheim's own 1 s delay.
        /// </summary>
        private void UseAttack(string what, float stamina)
        {
            float left = _vp.GetStamina() - stamina * Game.m_staminaRate;
            Use(what, stamina);
            RegenTimer(_vp) = 0f;
            if (Settings.NegativeStamina.Value && left <= 0f)
            {
                _exhausted = true;
                Log.Add("stamina: out");
            }
        }

        /// <summary>Before each tick: while exhausted, the bar stays at zero.</summary>
        private void Exhaustion()
        {
            if (!_exhausted)
            {
                return;
            }
            bool attacking = Sim.State == StateKind.Act && Sim.Act.Id.IsAttack || Sim.AirAttackState.HasValue;
            if (!attacking)
            {
                _exhausted = false;
                Log.Add("stamina: recovered");
                return;
            }
            StaminaField(_vp) = 0f;
            Hud.instance?.StaminaBarEmptyFlash();
        }

        private static readonly System.Reflection.MethodInfo OnSneaking = AccessTools.Method(typeof(VPlayer), "OnSneaking");
        private float _sneakTimer;

        /// <summary>
        /// Per tick: Valheim's slow fall (the feather cape's fall-speed cap)
        /// with stronger steering, a little air control otherwise, and the Run
        /// and Jump skills' scaling (sprint speed 1 + 0.25 × Run, as
        /// GetRunSpeedFactor; jump 1 + 0.4 × Jump, as Character.Jump), and
        /// status effects' speed (Wind Run, meads) and jump (Lightfoot) mods.
        /// </summary>
        private void AirAndSkills(Input inp)
        {
            var fall = new Vector3(0f, -1000f, 0f);
            _vp.GetSEMan().ModifyWalkVelocity(ref fall);
            bool slow = fall.y > -999f;
            Sim.MaxFallSpeed = slow ? -fall.y : 0f;
            bool sprint = inp.Dodge.Held && Sim.DodgeHold >= Consts.SPRINT_HOLD_FRAMES;
            Sim.AirSpeed = slow ? (sprint ? ErData.SPRINT_SPEED : ErData.RUN_SPEED) : ErData.RUN_SPEED;
            Sim.AirAccel = slow ? 12f : 2.5f;
            float speed = StatusSpeed() * (1f - DeepSnow());
            if (_vp.IsEncumbered())
            {
                // Character.UpdateWalking: the crouch speed, and no moving at all on an empty bar (Player.CanMove).
                speed *= _vp.HaveStamina() ? _vp.m_crouchSpeed / ErData.RUN_SPEED : 0f;
                if (Sim.State == StateKind.Ground && Sim.Speed > 0.1f)
                {
                    _vp.UseStamina(_vp.m_encumberedStaminaDrain * Consts.DT);
                }
            }
            Sim.SprintScale = SprintScale() * speed;
            Sim.MoveScale = Mathf.Max(1f + GearMovement(), 0.1f) * speed;
            Sim.SprintScale *= Wading(ErData.SPRINT_SPEED * Sim.SprintScale);
            Sim.MoveScale *= Wading(ErData.RUN_SPEED * Sim.MoveScale);
            var jump = new Vector3(0f, _vp.m_jumpForce * (1f + _vp.GetSkillFactor(Skills.SkillType.Jump) * 0.4f), 0f);
            _vp.GetSEMan().ApplyStatusEffectJumpMods(ref jump);
            // Valheim scales the take-off speed: the height goes with its square.
            float up = jump.y / Mathf.Max(_vp.m_jumpForce, 0.01f);
            Sim.JumpScale = up * up;
        }

        private static readonly AccessTools.FieldRef<Character, Heightmap.Biome> LastBiome = AccessTools.FieldRefAccess<Character, Heightmap.Biome>("m_lastBiome");
        private static readonly AccessTools.FieldRef<Character, Heightmap> LastHeightmap = AccessTools.FieldRefAccess<Character, Heightmap>("m_lastHeightmap");
        private static readonly AccessTools.FieldRef<Character, Vector3> LastGroundHeight = AccessTools.FieldRefAccess<Character, Vector3>("m_lastGroundHeight");
        private static readonly AccessTools.FieldRef<Character, Vector3> LastDeepSnowWalkPos = AccessTools.FieldRefAccess<Character, Vector3>("m_lastDeepSnowWalkPos");
        private static readonly AccessTools.FieldRef<Character, Vector3> LastSnowPieceRemove = AccessTools.FieldRefAccess<Character, Vector3>("m_lastSnowPieceRemove");
        private static readonly AccessTools.FieldRef<Character, bool> IceShoes = AccessTools.FieldRefAccess<Character, bool>("m_iceShoes");
        private static readonly AccessTools.FieldRef<Character, bool> Skating = AccessTools.FieldRefAccess<Character, bool>("m_skating");
        private static readonly int SnowPieceMask = LayerMask.GetMask("piece", "piece_nonsolid", "item", "terrain");
        private static readonly Collider[] SnowPieces = new Collider[32];
        private static readonly int SlipperyMask = LayerMask.GetMask("static_solid", "Default");
        private Vector3 _iceVel;

        /// <summary>
        /// Character.UpdateWalking's Deep North snow: the trail it leaves (the
        /// walk object that clears the snow), the snow brushed off pieces, and
        /// the slow (a share of speed) for how deep the player stands in it.
        /// </summary>
        /// <summary>How deep the feet are in water or tar (m).</summary>
        private float LiquidDepth() => Mathf.Max(0f, _vp.GetLiquidLevel() - transform.position.y);

        /// <summary>
        /// Valheim swims once the feet leave the bottom: the sim hands over when the
        /// water is deeper than the swim depth (Valheim then floats the body, feet
        /// that far down), and takes back over when the feet touch the bottom again.
        /// Wading in between, still Valheim's "swimming" (Character.IsSwimming), is the sim's.
        /// </summary>
        private bool Swimming() =>
            LiquidDepth() > _vp.m_swimDepth + 0.1f || !Active && _vp.IsSwimming() && !_vp.IsOnGround();

        /// <summary>
        /// The share of a speed left wading, as Character.ApplyLiquidResistance takes it:
        /// speed − speed² × 0.05 (tar: 0.1) × how deep, full at a third of the body's height.
        /// </summary>
        private float Wading(float speed)
        {
            float depth = LiquidDepth();
            if (depth <= 0f || _vp.GetSEMan().HaveStatusEffect(SEMan.s_statusEffectTared))
            {
                return 1f;
            }
            float share = Mathf.Clamp01(depth / (_vp.GetCollider().height / 3f));
            return Mathf.Max(1f - speed * share * (_vp.InWater() ? 0.05f : 0.1f), 0.1f);
        }

        private float DeepSnow()
        {
            Vector3 pos = transform.position;
            float slow = 0f;
            Heightmap hm = LastHeightmap(_vp);
            if (LastBiome(_vp) == Heightmap.Biome.DeepNorth && _vp.m_deepSnowSlowMax != 0f && hm != null)
            {
                float depth = hm.GetCultivationMask(pos) * _vp.m_deepSnowSlowMaxHeight;
                if (depth > _vp.m_deepSnowSlowStartHeight)
                {
                    if (_vp.m_deepSnowWalkObj != null && Vector3.Distance(LastDeepSnowWalkPos(_vp), pos) > _vp.m_deepSnowWalkObjDist)
                    {
                        if (_vp.IsOnHeightmap())
                        {
                            Instantiate(_vp.m_deepSnowWalkObj, pos, transform.rotation);
                        }
                        LastDeepSnowWalkPos(_vp) = pos;
                    }
                    float top = LastGroundHeight(_vp).y + depth;
                    if (pos.y < top)
                    {
                        slow = (top - pos.y - _vp.m_deepSnowSlowStartHeight) / (_vp.m_deepSnowSlowMaxHeight - _vp.m_deepSnowSlowStartHeight) * _vp.m_deepSnowSlowMax;
                    }
                }
            }
            if (LastBiome(_vp) == Heightmap.Biome.DeepNorth && Vector3.Distance(LastSnowPieceRemove(_vp), pos) > _vp.m_snowWalkPieceDist)
            {
                LastSnowPieceRemove(_vp) = pos;
                int n = Physics.OverlapSphereNonAlloc(pos, _vp.m_snowWalkPieceRadius, SnowPieces, SnowPieceMask);
                for (int i = 0; i < n; i++)
                {
                    WearNTear piece = SnowPieces[i].GetComponentInParent<WearNTear>();
                    if (piece != null && piece.m_snowBuildup > _vp.m_snowWalkPieceMin)
                    {
                        piece.ChangeSnow(-_vp.m_snowWalkPieceRemove);
                        _vp.m_snowWalkPiecelEffect.Create(SnowPieces[i].transform.position, SnowPieces[i].transform.rotation);
                    }
                }
            }
            return Mathf.Clamp01(slow);
        }

        /// <summary>
        /// On ice (Character.ApplySlippery: a frictionless static solid, no ice
        /// shoes) the player keeps its momentum: moving, the velocity turns
        /// toward the sim's by Valheim's slippery control (1% a physics step,
        /// half that sprinting); with no input it coasts (99.9% kept a step).
        /// </summary>
        private void Ice(Vec3 before)
        {
            Vector3 moved = Space.ToUnity(Sim.Pos) - Space.ToUnity(before);
            Vector3 want = new Vector3(moved.x, 0f, moved.z) / Consts.DT;
            Collider ground = _level.GroundCollider;
            bool onIce = !Sim.Airborne() && Sim.State != StateKind.Air && ground != null && ground.material != null
                         && !(ground.material.dynamicFriction > 0f) && !IceShoes(_vp)
                         && (SlipperyMask & ground.gameObject.layer) != 0; // Valheim's own test (mask & layer number)
            if (!onIce)
            {
                _iceVel = want;
                return;
            }
            // Valheim's control is per 50 Hz physics step; the sim ticks at 60 Hz.
            float steps = Consts.DT * 50f;
            bool skating = Skating(_vp);
            float control = skating ? (Sim.Sprinting ? _vp.m_skatingControlRun : _vp.m_skatingControl)
                                    : (Sim.Sprinting ? _vp.m_slipperyControlRun : _vp.m_slipperyControl);
            if (want.sqrMagnitude > 0.01f)
            {
                _iceVel = Vector3.Lerp(_iceVel, want, 1f - Mathf.Pow(1f - control, steps));
            }
            else
            {
                _iceVel *= Mathf.Pow(skating ? _vp.m_skatingBreaking : _vp.m_slipperyBreaking, steps);
            }
            Vector3 slip = (_iceVel - want) * Consts.DT;
            _level.Slide(ref Sim.Pos, Space.ToSim(slip));
        }

        /// <summary>
        /// Status effects' speed factor (Tared, Slimed, Frost, Immobilized, meads,
        /// Moder's power, Wind Run), as Valheim's walk applies them: 0 held fast.
        /// </summary>
        private float StatusSpeed()
        {
            float speed = 1f;
            _vp.GetSEMan().ApplyStatusEffectSpeedMods(ref speed, Space.DirToUnity(Sim.MoveDir));
            return speed;
        }

        /// <summary>
        /// The gear's movement modifier (negative slows), as Valheim sums it,
        /// except that a weapon in the left hand counts none (Valheim never
        /// expects two: a power stance of battleaxes would pay twice). With
        /// GearPenalties off, only the items' bonuses count (Fenris armour).
        /// </summary>
        private float GearMovement()
        {
            ItemDrop.ItemData left = Left;
            bool skipLeft = left != null && left != Right && Hands.IsLeftWeapon(left);
            if (Settings.GearPenalties.Value)
            {
                return _vp.GetEquipmentMovementModifier() - (skipLeft ? left.m_shared.m_movementModifier : 0f);
            }
            float bonus = 0f;
            foreach (ItemDrop.ItemData item in _vp.GetInventory().GetEquippedItems())
            {
                if (!(skipLeft && item == left))
                {
                    bonus += Mathf.Max(item.m_shared.m_movementModifier, 0f);
                }
            }
            return bonus;
        }

        /// <summary>The gear's movement modifier as Valheim's run and jump stamina use it; only its bonuses with GearPenalties off.</summary>
        private float GearStaminaModifier() => Settings.GearPenalties.Value ? _vp.GetEquipmentMovementModifier() : GearMovement();

        /// <summary>
        /// GetRunSpeedFactor: 1 + 0.25 × Run skill, times 1 + 1.5 × the gear's
        /// movement modifier (GearMovement). Walking and running pay
        /// 1 + the modifier, as Valheim's jog (MoveScale), and the sprint
        /// never falls below 1.1 × that run.
        /// </summary>
        private float SprintScale()
        {
            float movement = GearMovement();
            float skill = 1f + _vp.GetSkillFactor(Skills.SkillType.Run) * 0.25f;
            float scale = skill * Mathf.Max(1f + movement * 1.5f, 0f);
            float run = Mathf.Max(1f + movement, 0.1f);
            return Mathf.Max(scale, ErData.RUN_SPEED * run * 1.1f / Mathf.Max(ErData.SPRINT_SPEED, 0.01f));
        }

        /// <summary>
        /// Sneaking (crouched and moving on the ground): Valheim's own
        /// OnSneaking (stamina and Sneak skill) with SneakStamina, else just the
        /// skill, raised as it does.
        /// </summary>
        private void Sneak(float dt)
        {
            // As Player.UpdateCrouch: an empty bar stands the player up, when sneaking costs stamina.
            if (Settings.SneakStamina.Value && Sim.Crouching && !_vp.HaveStamina())
            {
                Sim.Crouching = false;
            }
            if (!Sneaking)
            {
                return;
            }
            if (Settings.SneakStamina.Value && OnSneaking != null)
            {
                OnSneaking.Invoke(_vp, new object[] { dt });
                return;
            }
            _sneakTimer += dt;
            if (_sneakTimer > 1f)
            {
                _sneakTimer = 0f;
                _vp.RaiseSkill(Skills.SkillType.Sneak, BaseAI.InStealthRange(_vp) ? 1f : 0.1f);
            }
        }

        /// <summary>Crouched and moving on the ground (Valheim's IsSneaking).</summary>
        public bool Sneaking => Active && Sim != null && Sim.Crouching && Sim.State == StateKind.Ground && Sim.Speed > 0.1f;

        private bool _catapulted;

        /// <summary>Held by a catapult's arm: Valheim moves the player until it lands and is steered (Wanted).</summary>
        public void Catapulted() => _catapulted = true;

        private static readonly System.Reflection.FieldInfo Vagons = AccessTools.Field(typeof(Vagon), "m_instances");

        /// <summary>
        /// Where Valheim's own movement takes over: thrown by a catapult (until
        /// a move input once released), pulling a cart or battering ram, at a
        /// crafting station's menu.
        /// </summary>
        private string ValheimMoves()
        {
            if (_catapulted && _vp.transform.parent == null && _vp.GetMoveDir().sqrMagnitude > 0.01f)
            {
                _catapulted = false;
            }
            if (_catapulted)
            {
                return "catapult";
            }
            foreach (Vagon v in (List<Vagon>)Vagons.GetValue(null))
            {
                if (v != null && v.IsAttached(_vp))
                {
                    return "pulling";
                }
            }
            if (_vp.GetCurrentCraftingStation() != null && InventoryGui.IsVisible())
            {
                return "crafting";
            }
            return null;
        }

        private float _lastDodgeRoll = -1f;

        /// <summary>Valheim's perfect dodge (effect and Dodge skill): once per roll, not once per collider an enemy swing touches.</summary>
        public bool FirstDodgeThisRoll()
        {
            float start = _clock - Sim.Act.F / Consts.ANIM_FPS;
            if (Mathf.Abs(start - _lastDodgeRoll) < 0.05f)
            {
                return false;
            }
            _lastDodgeRoll = start;
            return true;
        }

        private void Use(string what, float stamina)
        {
            _vp.UseStamina(stamina);
            CombatLog.SpentStamina(what, stamina);
            if (what != "sprint")
            {
                Log.Add($"stamina: {what} {stamina:0.0}");
            }
        }

        private static readonly System.Reflection.MethodInfo DodgeStamina = AccessTools.Method(typeof(VPlayer), "GetDodgeStaminaUse");
        private float _runSkillTimer;

        /// <summary>Player.CheckRun's drain (Run skill, equipment, status effects), and its skill raise.</summary>
        private void RunStamina(float dt)
        {
            float drain = _vp.m_runStaminaDrain * Mathf.Lerp(1f, 0.5f, _vp.GetSkillFactor(Skills.SkillType.Run));
            drain -= drain * GearStaminaModifier();
            drain += drain * _vp.GetEquipmentRunStaminaModifier();
            // The sim's X is Unity's mirrored (see Space).
            _vp.GetSEMan().ModifyRunStaminaDrain(drain, ref drain, new Vector3(-Sim.MoveDir.X, 0f, Sim.MoveDir.Z));
            Use("sprint", dt * drain * Game.m_moveStaminaRate);
            _runSkillTimer += dt;
            if (_runSkillTimer > 1f && _vp.HaveStamina())
            {
                _runSkillTimer = 0f;
                _vp.RaiseSkill(Skills.SkillType.Run);
            }
        }

        /// <summary>Player.OnJump's cost, and Character.Jump's skill raise.</summary>
        private void JumpStamina()
        {
            float cost = _vp.m_jumpStaminaUsage - _vp.m_jumpStaminaUsage * GearStaminaModifier() + _vp.m_jumpStaminaUsage * _vp.GetEquipmentJumpStaminaModifier();
            _vp.GetSEMan().ModifyJumpStaminaUsage(cost, ref cost);
            Use("jump", cost * Game.m_moveStaminaRate);
            _vp.RaiseSkill(Skills.SkillType.Jump);
        }

        // --- Attack stamina ---------------------------------------------------------
        //
        // Each hit costs, when its window opens, the weapon's own Valheim
        // attack stamina (Attack.GetAttackStamina: skill, gear, status
        // effects) in ER's proportion: the moveset's first light attack costs
        // exactly one vanilla swing. Each hit is charged to the hand that
        // swings it (in power stance, each weapon its own cost). As in vanilla,
        // an attack whose first hit can't be paid for doesn't start, and what
        // starts is charged in full (it doesn't get cheaper near empty).

        private ItemDrop.ItemData Unarmed => _vp.m_unarmedWeapon != null ? _vp.m_unarmedWeapon.m_itemData : null;

        /// <summary>The Valheim item for an attack's hit (power stance: the nearer blade; -1: its first).</summary>
        private ItemDrop.ItemData AttackItem(ActionId id, int hit)
        {
            ItemDrop.ItemData item;
            Combat.Swinging hand = Combat.HandFor(Sim, id);
            if (hand == Combat.Swinging.Left || Sim.Grip == Grip.TwoHandLeft)
            {
                item = Left;
            }
            else if (hand == Combat.Swinging.Both && hit >= 0 && Sim.WindowHit(hit) is ActiveHit h)
            {
                Vector3 mid = (Space.ToUnity(h.SweepA[SimPlayer.SWEEP_STEPS - 1]) + Space.ToUnity(h.SweepB[SimPlayer.SWEEP_STEPS - 1])) * 0.5f;
                item = Combat.Nearer(_rightBlade, _leftBlade, mid)?.Item ?? Right;
            }
            else
            {
                item = Right;
            }
            return item ?? Unarmed;
        }

        /// <summary>A hit's (or air attack's) ER stamina, in this item's Valheim stamina.</summary>
        private float AttackCost(Moveset m, ItemDrop.ItemData item, float erStamina)
        {
            ActionDef light = (m.Has(AttackKind.Light1) ? m : new Moveset(m.Weapon, false)).Attack(AttackKind.Light1);
            // The sim charges a grounded attack per hit (its ActionDef.Stamina is not spent).
            float reference = light != null ? light.Hits.Sum(h => h.Stamina) : 0f;
            float weaponCost = WeaponStamina(item);
            return reference > 0f && weaponCost > 0f ? weaponCost * erStamina / reference : erStamina * _vp.GetMaxStamina() / ErData.MAX_STAMINA;
        }

        /// <summary>Charges the hits whose windows opened this tick (frames after fromF), and an air attack that began.</summary>
        private void ChargeAttacks(float fromF, bool wasAir)
        {
            if (Sim.AirAttackState is AirAttack air && !wasAir)
            {
                ItemDrop.ItemData item = (Sim.Grip == Grip.TwoHandLeft ? Left : Right) ?? Unarmed;
                UseAttack($"jump attack ({item?.m_shared.m_name})", AttackCost(air.Moveset, item, air.Def.Stamina));
            }
            // Landing from it: the air attack paid for the swing the landing carries on.
            if (wasAir || Sim.State != StateKind.Act || !Sim.Act.Id.IsAttack)
            {
                return;
            }
            ActionId id = Sim.Act.Id;
            Hit[] hits = id.Def().Hits;
            // A jump attack's landing: the hits within its first swing are the air attack's, already paid
            // (landing before the swing's window, they would come out after the landing's first tick).
            uint paid = SimPlayer.IsJumpLanding(id.Kind) ? SimPlayer.SwingHits(id.Def()) : 0u;
            for (int i = 0; i < hits.Length; i++)
            {
                if ((paid & (1u << i)) == 0 && hits[i].From > fromF && hits[i].From <= Sim.Act.F)
                {
                    ItemDrop.ItemData item = AttackItem(id, i);
                    UseAttack($"{id} hit {i + 1} ({item?.m_shared.m_name})", AttackCost(id.Moveset, item, hits[i].Stamina));
                }
            }
        }

        /// <summary>
        /// The sim asks before an attack starts. ER (NegativeStamina): any
        /// stamina left will do (the sim checks that itself). Vanilla: enough
        /// for its first hit.
        /// </summary>
        private bool CanAfford(ActionId id)
        {
            Hit[] hits = id.Def().Hits;
            if (hits.Length == 0 || Settings.NegativeStamina.Value)
            {
                return true;
            }
            float cost = AttackCost(id.Moveset, AttackItem(id, -1), hits[0].Stamina);
            if (_vp.HaveStamina(cost + 0.1f))
            {
                return true;
            }
            Hud.instance?.StaminaBarEmptyFlash();
            Log.Add($"stamina: {id} refused ({_vp.GetStamina():0.0} < {cost:0.0})");
            return false;
        }

        private bool CanAffordAir(Moveset m, AirAttackDef def)
        {
            if (Settings.NegativeStamina.Value)
            {
                return true;
            }
            ItemDrop.ItemData item = (Sim.Grip == Grip.TwoHandLeft ? Left : Right) ?? Unarmed;
            if (_vp.HaveStamina(AttackCost(m, item, def.Stamina) + 0.1f))
            {
                return true;
            }
            Hud.instance?.StaminaBarEmptyFlash();
            return false;
        }

        /// <summary>Attack.GetAttackStamina, for the weapon's primary attack.</summary>
        private float WeaponStamina(ItemDrop.ItemData item)
        {
            Attack attack = item?.m_shared.m_attack;
            if (attack == null || attack.m_attackStamina <= 0f)
            {
                return 0f;
            }
            float cost = attack.m_attackStamina * (1f + _vp.GetEquipmentAttackStaminaModifier());
            _vp.GetSEMan().ModifyAttackStaminaUsage(cost, ref cost);
            cost -= cost * 0.33f * _vp.GetSkillFactor(item.m_shared.m_skillType);
            return Mathf.Max(cost, 0f);
        }

        /// <summary>The guard counter's own ER sound, once as it comes out.</summary>
        private void GuardCounterSound(float late = 0f)
        {
            bool counter = Sim.State == StateKind.Act && Sim.Act.Id.IsAttack && Sim.Act.Id.Kind == AttackKind.GuardCounter;
            if (counter && !_counterSounded)
            {
                ErSounds.PlaySignature(_vp, Sim.Act.Id.Def().Source, late);
                Log.Add("guard counter sound " + Sim.Act.Id.Def().Source);
            }
            _counterSounded = counter;
        }

        /// <summary>
        /// Another player's ER sounds, from its synced state as it changes: the
        /// guard counter's (as far into the clip as the update arrived) and the
        /// guard break's (the owner plays it in Blocked).
        /// </summary>
        private void RemoteSounds()
        {
            GuardCounterSound(Sim.State == StateKind.Act ? Sim.Act.F / Consts.ANIM_FPS : 0f);
            bool broken = Sim.State == StateKind.Act && Sim.Act.Id == ActionId.GuardBreak;
            if (broken && !_breakSounded)
            {
                ErSounds.PlayGuardBreak(_vp);
            }
            _breakSounded = broken;
        }

        private bool _breakSounded;

        // --- I-frames -------------------------------------------------------------

        private static float _valheimDodge = -1f;

        /// <summary>
        /// Valheim's dodge i-frames, in ER frames (30 fps): from the roll's start
        /// to its clip's DodgeMortal event (CharacterAnimEvent → Player.OnDodgeMortal), read from the
        /// animator once.
        /// </summary>
        private float ValheimDodgeFrames()
        {
            if (_valheimDodge < 0f && _animator != null && _animator.runtimeAnimatorController != null)
            {
                _valheimDodge = 0f;
                foreach (AnimationClip clip in _animator.runtimeAnimatorController.animationClips)
                {
                    foreach (AnimationEvent e in clip.events)
                    {
                        if (e.functionName == "DodgeMortal" && e.time * Consts.ANIM_FPS > _valheimDodge)
                        {
                            _valheimDodge = e.time * Consts.ANIM_FPS;
                            Plugin.Report($"Valheim's dodge: {clip.name}, {clip.length:0.00} s, mortal at {e.time:0.00} s ({_valheimDodge:0.0} frames)");
                        }
                    }
                }
            }
            return Mathf.Max(_valheimDodge, 0f);
        }

        /// <summary>In the air and clear of the ground: hits below the hips miss (Incoming, the sim's Jumped).</summary>
        private bool LegsClear() => Sim != null && Sim.Airborne() && Sim.Pos.Y - _level.Height(Sim.Pos.X, Sim.Pos.Z) > Consts.JUMP_CLEARANCE;

        /// <summary>IframeDebug: the window the current dodge has, and where in it the player is.</summary>
        public string IframeStatus()
        {
            if (!Sim?.Invincible() ?? true)
            {
                if (LegsClear())
                {
                    return "in the air: legs invincible (hits below the hips)";
                }
            }
            if (Sim == null || Sim.State != StateKind.Act)
            {
                return Sim != null && Sim.Invincible() ? "invincible" : "";
            }
            ActionDef d = Sim.Act.Id.Def();
            bool dodge = Sim.Act.Id.Tag == ActionTag.Roll || Sim.Act.Id.Tag == ActionTag.CrouchRoll || Sim.Act.Id.Tag == ActionTag.Backstep;
            string window = dodge && Sim.DodgeIframes > 0f ? $"0-{Sim.DodgeIframes:0.#} (Valheim's)" : d.IframesTo > d.IframesFrom ? $"{d.IframesFrom:0}-{d.IframesTo:0} (Elden Ring's)" : "none";
            return $"{d.Name} frame {Sim.Act.F:0} of {d.Total:0}, i-frames {window}{(Sim.Invincible() ? "  INVINCIBLE" : "")}";
        }

        // --- Parries ----------------------------------------------------------------

        private static readonly int ParryKey = "eh_parry".GetStableHashCode();
        private EffectList _vanillaParry, _quietParry;
        private int _parries, _seenParries = -1;

        /// <summary>
        /// Humanoid.BlockAttack starts on the local player: with ParrySound on,
        /// Valheim's parry effect plays without its sound for this hit (its
        /// sound prefabs are networked, so nobody hears it), and ER's plays.
        /// </summary>
        public void QuietParry()
        {
            if (!Settings.Enabled.Value || !Settings.ParrySound.Value || _vp.m_perfectBlockEffect == null)
            {
                return;
            }
            if (_quietParry == null || _vanillaParry != _vp.m_perfectBlockEffect)
            {
                _vanillaParry = _vp.m_perfectBlockEffect;
                _quietParry = new EffectList
                {
                    m_effectPrefabs = _vanillaParry.m_effectPrefabs
                        .Where(e => e.m_prefab == null || e.m_prefab.GetComponentInChildren<ZSFX>(true) == null && e.m_prefab.GetComponentInChildren<AudioSource>(true) == null)
                        .ToArray(),
                };
            }
            _vp.m_perfectBlockEffect = _quietParry;
        }

        /// <summary>BlockAttack is over: Valheim's own parry effect back.</summary>
        public void LoudParry()
        {
            if (_vanillaParry != null && _vp.m_perfectBlockEffect == _quietParry)
            {
                _vp.m_perfectBlockEffect = _vanillaParry;
            }
        }

        /// <summary>The quiet parry effect is being played: a parry. ER's sound, here and (through the ZDO) for everyone else.</summary>
        public static void Parried(EffectList list)
        {
            Controller c = Local;
            if (c == null || list != c._quietParry)
            {
                return;
            }
            ErSounds.PlayParry(c._vp);
            if (c._nview != null && c._nview.IsValid() && c._nview.IsOwner())
            {
                c._nview.GetZDO().Set(ParryKey, ++c._parries);
            }
        }

        /// <summary>Another player's parries, by the count in its ZDO.</summary>
        private void RemoteParries(ZDO zdo)
        {
            int n = zdo.GetInt(ParryKey);
            if (_seenParries >= 0 && n != _seenParries && Settings.ParrySound.Value)
            {
                ErSounds.PlayParry(_vp);
            }
            _seenParries = n;
        }

        private void NoiseFor(SimPlayer p)
        {
            float noise = p.State == StateKind.Act ? 15f : p.Sprinting ? 20f : p.Speed > ErData.WALK_SPEED + 0.5f ? 10f : p.Speed > 0.1f ? 5f : 0f;
            if (p.Crouching)
            {
                noise *= 0.5f;
            }
            if (noise > 0f)
            {
                _vp.AddNoise(noise);
            }
        }

        private bool InCombat()
        {
            foreach (Character c in Character.GetAllCharacters())
            {
                BaseAI ai = c.GetBaseAI();
                if (ai != null && ai.IsAlerted() && BaseAI.IsEnemy(_vp, c) && Vector3.Distance(c.transform.position, transform.position) < 25f)
                {
                    return true;
                }
            }
            return false;
        }

        private void Locking(Input inp)
        {
            // A killed creature is destroyed at once (Character.OnDeath): the lock reads as a destroyed object, not as dead.
            bool killed = (object)LockTarget != null && (LockTarget == null || LockTarget.IsDead());
            if (killed || LockTarget != null && Vector3.Distance(LockTarget.transform.position, transform.position) > Settings.LockOnRange.Value * 1.5f)
            {
                // Elden Ring's Auto Lock-On: a kill moves the lock on (the camera is still looking that way).
                LockTarget = killed && Settings.AutoLockOn.Value ? NearestInView() : null;
            }
            if (!inp.Lock)
            {
                return;
            }
            if (LockTarget != null)
            {
                LockTarget = null;
                return;
            }
            LockTarget = NearestInView();
            if (LockTarget == null && !Settings.NoLockOnRecentre.Value)
            {
                _recenter = true;
            }
        }

        /// <summary>
        /// The enemy nearest the crosshair: the angle off the camera's ray
        /// decides, distance only between ones about as near it (1° per 2 m).
        /// </summary>
        private Character NearestInView()
        {
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : transform;
            Character best = null;
            float bestScore = float.MaxValue;
            foreach (Character c in LockCandidates())
            {
                Vector3 to = c.GetCenterPoint() - cam.position;
                float off = Vector3.Angle(to, cam.forward);
                if (off > 75f)
                {
                    continue;
                }
                float score = off + Vector3.Distance(c.transform.position, transform.position) * 0.5f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best;
        }

        private const float AUTO_TARGET_RANGE = 6f;
        private Character _autoTarget;

        /// <summary>
        /// UnlockedAimAssist (ER's Auto Target), unlocked: the enemy an attack turns toward. Picked when an
        /// attack is pressed, the nearest within range and 75° of the way pushed
        /// (or faced); kept through the combo, and only steered by until its
        /// last hit, so dodging out of it stays free.
        /// </summary>
        private Character AutoTarget(Input inp)
        {
            bool attacking = Sim.State == StateKind.Act && Sim.Act.Id.IsAttack;
            Hit[] hits = attacking ? Sim.Act.Id.Def().Hits : null;
            bool swinging = attacking && hits.Length > 0 && Sim.Act.F < hits[hits.Length - 1].To;
            if (!Settings.AutoTarget.Value || !swinging && !inp.Light.Pressed && !inp.Heavy.Pressed)
            {
                _autoTarget = null;
                return null;
            }
            if (_autoTarget != null && (_autoTarget.IsDead() || Vector3.Distance(_autoTarget.transform.position, transform.position) > AUTO_TARGET_RANGE * 1.5f))
            {
                _autoTarget = null;
            }
            if (_autoTarget == null && (inp.Light.Pressed || inp.Heavy.Pressed))
            {
                Vector3 way = Space.ToUnity(Sim.Pos + (inp.Wish(out Vec3 wish) ? wish : Sim.Facing())) - Space.ToUnity(Sim.Pos);
                way.y = 0f;
                float bestScore = float.MaxValue;
                foreach (Character c in LockCandidates())
                {
                    Vector3 to = c.transform.position - transform.position;
                    to.y = 0f;
                    float off = Vector3.Angle(way, to);
                    if (to.magnitude > AUTO_TARGET_RANGE || off > 75f)
                    {
                        continue;
                    }
                    float score = to.magnitude + off * 0.05f;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        _autoTarget = c;
                    }
                }
            }
            return swinging ? _autoTarget : null;
        }

        /// <summary>
        /// A Smithscript attack still to throw, with no target: the body turns
        /// the camera's way, as far as the attack can turn, for the throw to
        /// leave the way the player looks.
        /// </summary>
        private Vec3? ThrowFacing()
        {
            bool throwing = Sim.AirAttackState is AirAttack air ? air.Def.Throw != 0 && air.F < air.Def.To
                : Sim.State == StateKind.Act && Sim.Act.Id.IsAttack && Sim.Act.Id.Def().Hits.Any(h => h.Throw != 0 && Sim.Act.F < h.To);
            if (!throwing || GameCamera.instance == null)
            {
                return null;
            }
            Vector3 way = GameCamera.instance.transform.forward;
            way.y = 0f;
            return way.sqrMagnitude > 1e-4f ? Space.ToSim(transform.position + way.normalized * 20f) : (Vec3?)null;
        }

        /// <summary>What lock-on can target: living enemies (and PvP players) in range.</summary>
        private static readonly int SightMask = LayerMask.GetMask("Default", "static_solid", "piece", "terrain");

        private IEnumerable<Character> LockCandidates()
        {
            foreach (Character c in Character.GetAllCharacters())
            {
                // The training dummy is no one's enemy (its own faction).
                if (c == _vp || c.IsDead() || !(BaseAI.IsEnemy(_vp, c) || c.GetFaction() == Character.Faction.TrainingDummy || c.IsPlayer() && c.IsPVPEnabled()))
                {
                    continue;
                }
                // Not through walls, rock or the ground (small stones and bushes don't hide it);
                // a target on those layers itself (the dummy is a piece) doesn't hide itself.
                if (Physics.Linecast(_vp.GetEyePoint(), c.GetCenterPoint(), out RaycastHit block, SightMask, QueryTriggerInteraction.Ignore)
                    && block.collider.GetComponentInParent<Character>() != c)
                {
                    continue;
                }
                if (Vector3.Distance(c.transform.position, transform.position) <= Settings.LockOnRange.Value)
                {
                    yield return c;
                }
            }
        }

        // Switching targets, as in Elden Ring: a flick of the camera (mouse or
        // right stick) left or right moves the lock to the next target that way.
        private float _flick, _switchWait;
        private bool _stickCentred = true;

        private void LockSwitching(bool input)
        {
            _switchWait -= Time.deltaTime;
            float stick = 0f;
            UnityEngine.InputSystem.Gamepad pad = UnityEngine.InputSystem.Gamepad.current;
            if (pad != null)
            {
                stick = pad.rightStick.ReadValue().x;
            }
            bool centred = Mathf.Abs(stick) < 0.3f;
            if (LockTarget == null || !input)
            {
                _flick = 0f;
                _stickCentred = centred;
                return;
            }
            // Mouse travel, fading over ~0.15 s: a deliberate flick, not a drift.
            UnityEngine.InputSystem.Mouse mouse = UnityEngine.InputSystem.Mouse.current;
            float dx = mouse != null ? mouse.delta.ReadValue().x : 0f;
            _flick = _flick * Mathf.Exp(-Time.deltaTime / 0.15f) + dx;
            // The stick: armed once back in the middle, it switches as soon as it is
            // pushed half way (the moment it leaves the middle used to disarm it,
            // so a flick that took a few frames to get far did nothing), once per push.
            if (centred)
            {
                _stickCentred = true;
            }
            if (_stickCentred && Mathf.Abs(stick) > 0.5f)
            {
                _stickCentred = false;
                SwitchTarget(stick > 0f ? 1 : -1);
                return;
            }
            if (Mathf.Abs(_flick) < Settings.LockSwitchFlick.Value || _switchWait > 0f)
            {
                return;
            }
            int dir = _flick > 0f ? 1 : -1;
            _flick = 0f;
            _switchWait = 0.3f;
            SwitchTarget(dir);
        }

        /// <summary>The lock moves to the nearest target on that side (1: right), seen from the camera.</summary>
        public void SwitchTarget(int dir)
        {
            if (LockTarget == null)
            {
                return;
            }
            Transform cam = GameCamera.instance != null ? GameCamera.instance.transform : transform;
            Vector3 current = LockTarget.GetCenterPoint() - cam.position;
            current.y = 0f;
            Character best = null;
            float bestAngle = 120f;
            foreach (Character c in LockCandidates())
            {
                if (c == LockTarget)
                {
                    continue;
                }
                Vector3 to = c.GetCenterPoint() - cam.position;
                if (Vector3.Dot(to, cam.forward) <= 0f)
                {
                    continue;
                }
                to.y = 0f;
                float angle = Vector3.SignedAngle(current, to, Vector3.up) * dir;
                if (angle > 1f && angle < bestAngle)
                {
                    bestAngle = angle;
                    best = c;
                }
            }
            if (best != null)
            {
                LockTarget = best;
                Log.Add($"lock: switched {(dir > 0 ? "right" : "left")} to {best.name}");
            }
        }

        private static Texture2D _dot;

        /// <summary>The lock-on dot, on the target's centre.</summary>
        private void OnGUI()
        {
            if (!Active || !Settings.LockOnDot.Value || Event.current.type != EventType.Repaint || GameCamera.instance == null)
            {
                return;
            }
            Vector3? at = LockPoint();
            Camera cam = GameCamera.instance.GetComponent<Camera>();
            if (!at.HasValue || cam == null)
            {
                return;
            }
            // Viewport, not pixels: Valheim renders the 3D view below screen resolution.
            Vector3 v = cam.WorldToViewportPoint(at.Value);
            if (v.z <= 0f)
            {
                return;
            }
            if (_dot == null)
            {
                _dot = DotTexture(32);
            }
            float size = Mathf.Max(Screen.height / 70f, 10f);
            GUI.DrawTexture(new Rect(v.x * Screen.width - size / 2f, (1f - v.y) * Screen.height - size / 2f, size, size), _dot);
        }

        /// <summary>A white disc with a soft dark rim.</summary>
        private static Texture2D DotTexture(int n)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            float r = n / 2f;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x + 0.5f - r, y + 0.5f - r).magnitude / r;
                    float rim = Mathf.Clamp01((1f - d) * 8f);
                    float core = Mathf.Clamp01((0.62f - d) * 8f);
                    var colour = Color.Lerp(new Color(0.1f, 0.08f, 0.05f, 0.75f), new Color(1f, 0.97f, 0.88f, 1f), core);
                    colour.a *= rim;
                    tex.SetPixel(x, y, colour);
                }
            }
            tex.Apply();
            return tex;
        }

        /// <summary>Where the camera should look while locked on (Unity), if locked.</summary>
        public Vector3? LockPoint()
        {
            if (LockTarget != null)
            {
                return LockTarget.GetCenterPoint();
            }
            return null;
        }

        public bool TakeRecenter()
        {
            bool r = _recenter;
            _recenter = false;
            return r;
        }

        /// <summary>
        /// A hit reaching the local player (Character.RPC_Damage, on the owner).
        /// Returns false to drop it. The sim judges i-frames, jumps and the
        /// guard; Valheim applies whatever lands.
        /// </summary>
        public bool Incoming(HitData hit)
        {
            BlockingNow = false;
            _judging = true;
            if (!Active)
            {
                return true;
            }
            _fell = hit.m_hitType == HitData.HitType.Fall;
            if (_fell)
            {
                return true;
            }
            Character attacker = hit.GetAttacker();
            Vector3 from = attacker != null ? attacker.transform.position : hit.m_point - hit.m_dir;
            _hitFrom = Space.ToSim(from);
            // Infinite poise: hits hurt but neither stagger nor push; only a
            // heavy blow knocks down (Damaged). The sim judges i-frames and the
            // guard's arc; Valheim's block meter breaks the guard (Blocked).
            float stagger = hit.m_staggerMultiplier;
            hit.m_staggerMultiplier = 0f;
            // Blows don't shove (ER's poise). Traps (no attacker: spikes) and a
            // creature shaking the player off its back (the gjall) push, as Valheim's pushback.
            Rigidbody under = _level.GroundBody;
            if (attacker != null && !(under != null && under.GetComponent<Character>() == attacker))
            {
                hit.m_pushForce = 0f;
            }
            // As in ER, a jump carries the legs over a blow: a hit landing below
            // the hips misses while the body is in the air, clear of the ground (the sim's JUMP_CLEARANCE).
            Transform hips = _retarget?.Hips;
            bool low = hips != null && hit.m_point != Vector3.zero && hit.m_point.y < hips.position.y;
            HitResult result = Sim.ReceiveHit(new Incoming { Damage = 0f, Stamina = 0f, From = _hitFrom, Low = low, Level = HurtLevel.Small }, _level);
            Log.Add($"Incoming {hit.GetTotalDamage():0} at {(hips != null ? hit.m_point.y - hips.position.y : 0f):+0.00;-0.00} m from the hips → {result}");
            switch (result)
            {
                case HitResult.Dodged:
                    return !hit.m_dodgeable;
                case HitResult.Jumped:
                    return false;
                case HitResult.Blocked:
                case HitResult.GuardBroken:
                    BlockingNow = hit.m_blockable;
                    // As Valheim: what gets past the guard fills the block meter again in ApplyDamage.
                    hit.m_staggerMultiplier = stagger;
                    return true;
                default:
                    return true;
            }
        }

        private Vec3 _hitFrom;
        private bool _fell;

        // Inside RPC_Damage, between Incoming and Damaged: Valheim's IsBlocking is the sim's verdict on this hit.
        private bool _judging;
        // The block meter when this hit's BlockAttack began; -1 when it wasn't blocked.
        private float _meterBefore = -1f;

        /// <summary>Humanoid.BlockAttack starts: the block meter before it.</summary>
        public void BlockStarting(float meter) => _meterBefore = meter;

        /// <summary>
        /// Valheim's IsBlocking: the sim's verdict during a hit, otherwise its
        /// guard, from the frame it starts, so Valheim's parry timer runs.
        /// </summary>
        public bool Blocking => _judging ? BlockingNow
            : Sim != null && (Sim.GuardUp() || Sim.AttackGuard() || Sim.State == StateKind.Act && Sim.Act.Id == ActionId.GuardHit);

        /// <summary>The item that guards (Valheim's GetCurrentBlocker): its block power, parry bonus and sound.</summary>
        public ItemDrop.ItemData Blocker => Sim == null ? null : Sim.Grip == Grip.TwoHandRight || Sim.GuardsWithRight() || Sim.AttackGuard() ? Right : Left;

        /// <summary>After Valheim applied a hit: one that took a large share of maximum health, unblocked, knocks the player down.</summary>
        public void Damaged(float lost)
        {
            _judging = false;
            if (_meterBefore >= 0f)
            {
                // After all of Valheim's damage: both its block and the blow's remainder fill the meter.
                float before = _meterBefore;
                _meterBefore = -1f;
                Blocked(before, _vp.GetStaggerPercentage());
            }
            if (!Active || Sim == null || BlockingNow || _vp.IsDead() || lost < Settings.KnockdownPercent.Value / 100f * _vp.GetMaxHealth())
            {
                return;
            }
            if (_fell)
            {
                // A fall that hurts this much lands as ER's long fall: flat on the back.
                Sim.React(ActionId.LandHeavy);
                Log.Add($"heavy landing: fall took {lost:0} of {_vp.GetMaxHealth():0}");
                return;
            }
            Sim.Knockdown(_hitFrom);
            Log.Add($"knockdown: lost {lost:0} of {_vp.GetMaxHealth():0}");
        }

        /// <summary>
        /// After a blocked hit: a full block meter
        /// breaks the guard; one blow that alone fills most of it throws the
        /// player back.
        /// </summary>
        private void Blocked(float before, float after)
        {
            if (!Active || Sim == null)
            {
                return;
            }
            if (after >= 1f)
            {
                Sim.React(ActionId.GuardBreak);
                ErSounds.PlayGuardBreak(_vp);
                Log.Add("guard break");
            }
            else if (after - before >= Settings.GuardBlowPercent.Value / 100f)
            {
                Sim.React(ActionId.GuardBlow);
                Log.Add($"guard blow: meter {before:0.00} → {after:0.00}");
            }
        }

        // --- The pose -------------------------------------------------------------

        /// <summary>The pose at <paramref name="alpha"/> sim ticks past the last one (above 1: a frame drawn before the next tick).</summary>
        private void Pose(SimPlayer p, float dt, float alpha, float time, bool feet = true)
        {
            if (_pose == null)
            {
                return;
            }
            float want = Active ? 1f : 0f;
            _weight = Mathf.MoveTowards(_weight, want, dt / 0.15f);
            _pose.Weight = _weight;
            if (!Active || _chooser == null)
            {
                return;
            }
            if (!_chooser.Update(p, time, dt, alpha))
            {
                return;
            }
            _retarget.Solve(_chooser.Shown, _local, out Vector3 hips);

            // The sim snaps the body up and down whole steps; the body eases
            // onto the new height instead, and each foot finds its own ground.
            bool planted = !p.Airborne() && p.State != StateKind.Air && p.State != StateKind.Dead;
            // On foot the drawn height is the sim's (the body's, eased), not
            // the physics body's, which bounces against steep ground.
            float bodyY = transform.position.y;
            float simY = Mathf.Lerp(_tickFrom.y, _tickTo.y, Mathf.Clamp01(alpha));
            float rootY = planted && Mathf.Abs(simY - bodyY) < 0.5f ? simY : bodyY;
            if (!planted || Mathf.Abs(rootY - _bodyY) > 1f)
            {
                _bodyY = rootY;
            }
            else
            {
                _bodyY += (rootY - _bodyY) * (1f - Mathf.Exp(-16f * dt));
            }
            hips.y += _bodyY - bodyY;
            _retarget.PlaceFeet(_local, ref hips, charPoint =>
            {
                Vector3 world = transform.TransformPoint(charPoint);
                float groundY;
                if (Physics.Raycast(new Vector3(world.x, _bodyY + 0.6f, world.z), Vector3.down, out RaycastHit hit, 1.4f, PhysicsLevel.GroundMask, QueryTriggerInteraction.Ignore))
                {
                    groundY = hit.point.y;
                }
                else
                {
                    return null;
                }
                return groundY - _bodyY;
            }, planted, feet ? dt : 0f);
            // Two-handed, the left hand goes onto the handle.
            // (Not paired items: each hand holds its own axe, knife or claw.)
            // Not while a swap plays: the hands trade the weapon around the
            // body, and a hand pinned to the handle drags its arm through the torso.
            // Bare fists (and the hand-to-hand arts) have no handle to grip.
            bool gripping = !Hands.IsPair(Right) && !p.SwapState.HasValue && !Handless(p.Weapon)
                            && (p.Grip != Grip.OneHand || Chooser.GuardsTwoHanded(p));
            // Through a jump (take-off, the air, falls and the landing; not jump
            // attacks, which have their own grip) a two-handed weapon stays in
            // both hands, fading back to the clip's own grip after.
            bool jumping = !p.AirAttackState.HasValue && p.Grip != Grip.OneHand && (p.State == StateKind.Air || p.State == StateKind.Act && IsJumpAct(p.Act.Id.Tag));
            _gripHold = Mathf.MoveTowards(_gripHold, jumping ? 1f : 0f, dt / 0.2f);
            // The right arm out of the torso first: the left hand then grips the handle where it went.
            _retarget.ArmDepth = 0f;
            if (Settings.ArmsOutOfTorso)
            {
                _retarget.KeepArmOut(0, _local, hips);
            }
            bool guarding = p.Guarding || p.State == StateKind.Act && p.Act.Id == ActionId.GuardHit;
            _grip = gripping ? _retarget.GripLeftHand(_chooser.Shown, _local, hips, _gripHold, _chooser.GuardAlong, _chooser.GuardWeight) : 0f;
            // Kept out of the torso, the left hand comes off the handle: it opens as it leaves.
            // Not on guard: the hand stays on the weapon where ER's guard holds it.
            if (Settings.ArmsOutOfTorso && !(guarding && _grip > 0f))
            {
                _grip *= Mathf.Clamp01(1f - _retarget.KeepArmOut(1, _local, hips) / 0.08f);
            }
            ArmPeak = Mathf.Max(ArmPeak, _retarget.ArmDepth);
            if (_grip > 0f)
            {
                _retarget.CloseLeftHand(_local, _grip);
            }
            for (int i = 0; i < _local.Length; i++)
            {
                _pose.Set(i, _local[i]);
            }
            int slot = _local.Length;
            if (_retarget.RightAttach != null)
            {
                _pose.Set(slot++, _retarget.RightAttachRotation(_chooser.Shown, _bladeRight));
            }
            if (_retarget.LeftAttach != null)
            {
                _pose.Set(slot, _retarget.LeftAttachRotation(_chooser.Shown, _bladeLeft, _leftShield ? ShieldFaceAxis : LeftBladeAxis));
            }
            Vector3 world = transform.TransformPoint(hips);
            _pose.SetHipsLocal(_retarget.Hips.parent.InverseTransformPoint(world));
        }

        private float _gripHold;

        /// <summary>Valheim's shields as ER's: tower shields are greatshields (1), bucklers small shields (2), the rest medium (0).</summary>
        /// <summary>ER's guard style for a shield: 1 greatshield (any shield that can't parry: towers, the Serpent Scale), 2 small (bucklers), 0 medium.</summary>
        private static int ShieldStyle(ItemDrop.ItemData shield)
        {
            string name = shield.m_dropPrefab != null ? shield.m_dropPrefab.name : shield.m_shared.m_name;
            return shield.m_shared.m_timedBlockBonus <= 1f || name.IndexOf("Tower", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                : name.IndexOf("Buckler", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 0;
        }

        internal static bool IsJumpAct(ActionTag t) => t == ActionTag.Jump || t >= ActionTag.LandLight && t <= ActionTag.LandFall;

        public void FadeOutPose(float dt)
        {
            if (_pose == null)
            {
                return;
            }
            _weight = Mathf.MoveTowards(_weight, 0f, dt / 0.15f);
            _pose.Weight = _weight;
        }

        public string Status()
        {
            SimPlayer p = Sim;
            if (!Active)
            {
                Transform spine = Utils.FindChild(transform, "Spine1"), arm = Utils.FindChild(transform, "RightForeArm");
                return $"inactive ({Why}) spine {spine?.localRotation.eulerAngles:F1} forearm {arm?.localRotation.eulerAngles:F1} animator {_animator.enabled} {_animator.GetCurrentAnimatorStateInfo(0).shortNameHash}";
            }
            string state = p.State == StateKind.Act ? p.Act.Id + $" f={p.Act.F:0.0}" : p.State.ToString();
            string blade = "";
            if (_retarget?.RightAttach != null && _bladeRight != Vector3.zero && _chooser != null)
            {
                int w = Clips.Bone("R_Weapon");
                Vector3 have = _retarget.RightAttach.TransformDirection(_bladeRight);
                Vector3 want = transform.rotation * (_chooser.Shown.Rot[w] * Vector3.up);
                blade = $" blade {Vector3.Angle(have, want):0}deg off ER, pointing {have:F2}";
            }
            if (_retarget?.LeftAttach != null && _bladeLeft != Vector3.zero && _chooser != null)
            {
                Vector3 have = _retarget.LeftAttach.TransformDirection(_bladeLeft);
                Vector3 want = transform.rotation * _retarget.LeftWeaponAxis(_chooser.Shown, _leftShield ? ShieldFaceAxis : LeftBladeAxis);
                Vector3 local = transform.InverseTransformDirection(have);
                blade += $" left {(_leftShield ? "shield face" : "blade")} {Vector3.Angle(have, want):0}deg off ER, pointing {local:F2} (char)";
            }
            if (_grip > 0f && _retarget?.Bones != null)
            {
                Transform wrist = _retarget.Bones.Find(b => b.name == "LeftHand");
                if (wrist != null)
                {
                    blade += $" grip off by {Vector3.Distance(wrist.position, transform.TransformPoint(_retarget.GripTarget)):0.00} m" +
                             $" (attach computed {transform.InverseTransformPoint(transform.TransformPoint(_retarget.GripAttach)):F2} vs real {transform.InverseTransformPoint(_retarget.RightAttach.position):F2}," +
                             $" wrist {transform.InverseTransformPoint(wrist.position):F2}, wrist to shaft line {ShaftDistance(wrist.position):0.00} m, ER {_retarget.GripErOff:0.00} m)";
                }
            }
            return $"{(Active ? "active" : "inactive (" + Why + ")")}{blade} weapon {ErData.Weapons[p.Weapon].Name}/{ErData.Weapons[p.Left].Name} {p.Grip} " +
                   $"state {state} speed {p.Speed:0.00} stamina {p.Stamina:0.0} clip {_chooser?.Clip} f={_chooser?.Frame:0.0} pose weight {_weight:0.00} guard {_chooser?.GuardClip} " +
                   $"crouch {p.Crouching} sneaking {Sneaking} vh crouch {_vp.IsCrouching()} sneak {_vp.IsSneaking()} worst bone {_pose?.WorstBone()} grip {_grip:0.00} arms in (peak) {ArmPeak:0.000} m fall top {MaxAirAltitude(_vp):0.00} y {transform.position.y:0.00} steps '{_steps?.Last}' lock {(LockTarget != null ? LockTarget.name : "-")} last hit {_combat.LastHit}";
        }

        /// <summary>
        /// Debug: where the held item's shaft really is, from its vertices in
        /// thin slices across the axis (attach space): centroid of the cross
        /// section at several distances along the axis.
        /// </summary>
        public string ShaftSections()
        {
            Transform attach = _retarget?.RightAttach;
            if (attach == null || _bladeRight == Vector3.zero)
            {
                return "no item";
            }
            var parts = new List<string>();
            foreach (float at in new[] { -0.6f, -0.3f, 0f, 0.3f, 0.6f, 1.0f })
            {
                Vector3 sum = Vector3.zero;
                int n = 0;
                foreach (MeshFilter mf in attach.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null || !mf.sharedMesh.isReadable)
                    {
                        continue;
                    }
                    foreach (Vector3 v in mf.sharedMesh.vertices)
                    {
                        Vector3 local = attach.InverseTransformPoint(mf.transform.TransformPoint(v));
                        if (Mathf.Abs(Vector3.Dot(local, _bladeRight) - at) < 0.05f)
                        {
                            sum += local;
                            n++;
                        }
                    }
                }
                parts.Add(n > 0 ? $"{at:+0.0;-0.0}: {sum / n:F3} ({n})" : $"{at:+0.0;-0.0}: -");
            }
            foreach (MeshFilter mf in attach.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                Bounds b = mf.sharedMesh.bounds;
                var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 hi = -lo;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 l = attach.InverseTransformPoint(mf.transform.TransformPoint(corner));
                    lo = Vector3.Min(lo, l);
                    hi = Vector3.Max(hi, l);
                }
                parts.Add($"part {mf.name} [{mf.GetComponent<Renderer>()?.enabled}] centre {(lo + hi) * 0.5f:F3} size {hi - lo:F3}");
            }
            Transform lg = _retarget.LeftAttach;
            string left = lg != null ? $" left grip point in right attach space {attach.InverseTransformPoint(lg.position):F3}" : "";
            return string.Join("; ", parts) + left;
        }

        private float ShaftDistance(Vector3 p)
        {
            Vector3 o = _retarget.RightAttach.position, d = _retarget.RightAttach.TransformDirection(_bladeRight).normalized;
            Vector3 v = p - o;
            return (v - d * Vector3.Dot(v, d)).magnitude;
        }

        public Retarget Retargeter => _retarget;

        /// <summary>The deepest an elbow or wrist went into the torso (before being pushed out) since the test last reset it.</summary>
        public float ArmPeak;
        public Chooser Chooser => _chooser;
    }
}
