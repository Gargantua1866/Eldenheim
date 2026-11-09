using System.Collections.Generic;
using System.Linq;
using Eldenheim.Sim;
using HarmonyLib;
using UnityEngine;
using SimPlayer = Eldenheim.Sim.Player;
using VPlayer = global::Player;

namespace Eldenheim
{
    /// <summary>
    /// Outgoing hits: the sim's blade, swept each tick, against Valheim's
    /// colliders. A hit is built the way Attack.DoMeleeAttack builds one
    /// (item damage, skill factor, tool tier, push, stagger, effects, skill
    /// raise, durability), scaled by the ER motion value relative to the
    /// moveset's first light attack. Also the swing's sound and trail, on the
    /// hit's own active frames.
    /// </summary>
    public class Combat
    {
        private static readonly int AttackMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid",
            "character", "character_net", "character_ghost", "hitbox", "character_noenv", "vehicle");
        private static readonly int GroundMask = LayerMask.GetMask("terrain", "static_solid", "piece", "Default");
        private readonly Collider[] _overlap = new Collider[32];
        private readonly HashSet<GameObject> _hitThisWindow = new HashSet<GameObject>();
        private int _actSerial;
        private ActionId _lastAct;
        private float _lastF = -1f;
        private bool _lastAir;
        // The trail runs to this frame of the swing that started it (-1: off).
        private float _trailUntil = -1f;
        private int _trailSerial;
        private bool _terrainEffectDone, _impactDone;

        public string LastHit = "";
        public string SwingLog;

        /// <summary>
        /// What one hand holds, and where its striking part is in the world
        /// now and was a tick ago (the held item's own length, from its mesh).
        /// </summary>
        public class Blade
        {
            public ItemDrop.ItemData Item;
            public GameObject Instance;
            public Vector3 A, B, PrevA, PrevB;
            public bool Valid, PrevValid;
            /// <summary>The capsule's radius: just over the half-width of the item's head (0 for a fist: the hit's own).</summary>
            public float Radius;

            public void Move(Vector3 a, Vector3 b)
            {
                PrevA = A;
                PrevB = B;
                PrevValid = Valid;
                A = a;
                B = b;
                Valid = true;
            }

            public void Clear() => Valid = PrevValid = false;
        }

        /// <summary>Which hand swings in this act: right, left, or both (paired).</summary>
        public enum Swinging { Right, Left, Both }

        public static Swinging WhichHand(SimPlayer p)
        {
            if (p.Grip == Grip.TwoHandLeft)
            {
                // The left armament is held in both hands, on the right side.
                return Swinging.Right;
            }
            if (p.AirAttackState is AirAttack air)
            {
                return air.Paired ? Swinging.Both : Swinging.Right;
            }
            return p.State == StateKind.Act ? HandFor(p, p.Act.Id) : Swinging.Right;
        }

        /// <summary>Which hand an attack swings: left-hand chains the left, power-stance attacks both.</summary>
        public static Swinging HandFor(SimPlayer p, ActionId id)
        {
            if (p.Grip == Grip.TwoHandLeft || !id.IsAttack)
            {
                return Swinging.Right;
            }
            AttackKind k = id.Kind;
            if (k >= AttackKind.LeftLight1 && k <= AttackKind.LeftLight6)
            {
                return Swinging.Left;
            }
            if (k >= AttackKind.PairedLight1 && k <= AttackKind.PairedJumpLandShort)
            {
                return Swinging.Both;
            }
            return Swinging.Right;
        }

        // Fists and paired items (claws, Skoll and Hati, dual axes) strike with both hands.
        private static Swinging Hand(SimPlayer p, Blade right) =>
            right.Item == null || Hands.IsPair(right.Item) ? Swinging.Both : WhichHand(p);

        /// <summary>
        /// Another player's swing trails, from its synced state, and only the
        /// swing sounds the owner plays locally (the charged heavy's deeper
        /// copies); its plain ones are networked effects already.
        /// </summary>
        public void RemoteTrails(VPlayer vp, SimPlayer p, Blade right, Blade left)
        {
            Swinging hand = Hand(p, right);
            Swings(vp, p, hand == Swinging.Left ? left : right, hand == Swinging.Both ? left : null, remote: true);
        }

        /// <summary>
        /// Call after each sim tick: effects for windows that opened, then
        /// hits. right/left are the blades at each hand's attach point (with
        /// the left armament two-handed, it is the right one).
        /// </summary>
        public void AfterTick(VPlayer vp, SimPlayer p, Blade right, Blade left, List<string> log)
        {
            Live = false;
            ErCapsules.Clear();
            Swinging hand = Hand(p, right);
            Swings(vp, p, hand == Swinging.Left ? left : right, hand == Swinging.Both ? left : null);
            if (_actSerial != _hitsSerial)
            {
                _hitsSerial = _actSerial;
                _struck.Clear();
                _owners.Clear();
                _terrainEffectDone = false;
                _impactDone = false;
                _slammed.Clear();
            }
            // Every open window sweeps for its whole length: one contact (a bush,
            // the first of a group) must not use the swing up, as it does the
            // sandbox's single dummy. Each window strikes each target once.
            if (p.AirAttackState.HasValue)
            {
                ActiveHit air = p.ActiveHitNow();
                if (air == null)
                {
                    return;
                }
                // Both hands striking at once (power stance, two-handed fists):
                // each blade its own blow, as the landing's simultaneous hits are.
                ActionDef landing = p.AirAttackState.Value.Moveset.Attack(SimPlayer.LandingOf(p.AirAttackState.Value));
                uint blows = landing != null ? SimPlayer.SwingHits(landing) : 0u;
                bool twoBlows = hand == Swinging.Both && right.Valid && left.Valid && (blows & (blows - 1)) != 0;
                bool struck = twoBlows
                    ? Window(vp, p, air, 15, Swinging.Right, right, left, log) | Window(vp, p, air, 16, Swinging.Left, right, left, log)
                    : Window(vp, p, air, 15, hand, right, left, log);
                if (struck)
                {
                    p.MarkHit();
                }
                return;
            }
            if (p.State != StateKind.Act || !p.Act.Id.IsAttack)
            {
                return;
            }
            Hit[] hits = p.Act.Id.Def().Hits;
            int count = hits.Length;
            for (int i = 0; i < count; i++)
            {
                if ((p.Act.Landed & (1u << i)) != 0)
                {
                    // A jump attack's landing whose swing already struck in the air.
                    continue;
                }
                ActiveHit hit = p.WindowHit(i);
                if (hit != null)
                {
                    Window(vp, p, hit, i, hand, right, left, log);
                }
            }
            GroundSlam(vp, p, hits, hand == Swinging.Left ? left : right, log);
        }

        /// <summary>
        /// A sledge's heavy that struck nothing slams where its head meets the
        /// ground: from its first window to a few frames past its last (the
        /// head often comes down after the window closes).
        /// </summary>
        private void GroundSlam(VPlayer vp, SimPlayer p, Hit[] hits, Blade blade, List<string> log)
        {
            ItemDrop.ItemData weapon = Item(vp, blade);
            // (Not an attack that throws the sledge: it slams where it lands.)
            if (hits.Length == 0 || _slammed.Count > 0 || blade == null || !blade.Valid || !Slams(weapon) || !Heavy(p) || Throws(p))
            {
                return;
            }
            Hit last = hits[hits.Length - 1];
            if (p.Act.F < hits[0].From || p.Act.F > last.To + 6f)
            {
                return;
            }
            float r = Mathf.Max(blade.Radius, 0.05f) + 0.05f;
            if (!Physics.CheckCapsule(blade.A, blade.B, r, GroundMask, QueryTriggerInteraction.Ignore))
            {
                return;
            }
            Vector3 at = Physics.Raycast(blade.B + Vector3.up, Vector3.down, out RaycastHit ground, 1f + 2f * r, GroundMask) ? ground.point : blade.B;
            _slammed.Add(-1);
            var hit = new ActiveHit { Attack = p.Act.Id.Moveset.Info.Attack, Mv = last.Mv, GuardDamage = last.GuardDamage, Stop = last.Stop, Radius = last.Radius };
            _hitThisWindow.Clear();
            Slam(vp, p, weapon, hit, at, hit.Stop * Consts.HIT_STOP_SCALE, log);
        }

        /// <summary>A thrown hit's damage and poise scales, taken as it leaves the hand (the attack may be over when it lands).</summary>
        public sealed class Thrown
        {
            public float Motion, Guard;
        }

        /// <summary>Thrown sledges still in flight, and the slam each makes where it lands.</summary>
        private static readonly Dictionary<Projectile, System.Action<Collider, Vector3>> PendingSlams =
            new Dictionary<Projectile, System.Action<Collider, Vector3>>();

        /// <summary>Projectile.OnHit (on the thrower's client): a thrown sledge slams where it struck.</summary>
        public static void ThrownImpact(Projectile projectile, Collider collider, Vector3 point)
        {
            if (projectile != null && PendingSlams.TryGetValue(projectile, out System.Action<Collider, Vector3> slam))
            {
                PendingSlams.Remove(projectile);
                slam(collider, point);
            }
        }

        /// <summary>The Smithscript weapons' throws: hit windows the data flags as throws (tools/extract.py THROWS).</summary>
        private static bool IsThrow(SimPlayer p, int index)
        {
            if (p.AirAttackState is AirAttack air)
            {
                return air.Def.Throw != 0;
            }
            Hit[] hits = p.State == StateKind.Act && p.Act.Id.IsAttack ? p.Act.Id.Def().Hits : null;
            return hits != null && index < hits.Length && hits[index].Throw != 0;
        }

        /// <summary>An attack that ends in a throw: a sledge's slam waits for it to land.</summary>
        private static bool Throws(SimPlayer p)
        {
            if (p.AirAttackState is AirAttack air)
            {
                return air.Def.Throw != 0;
            }
            return p.State == StateKind.Act && p.Act.Id.IsAttack && p.Act.Id.Def().Hits.Any(h => h.Throw != 0);
        }

        /// <summary>
        /// Frames a thrown Smithscript weapon is gone from the hand before it
        /// reforms there, as ER's do (a spear thrown away for good, ThrowSpears,
        /// leaves the inventory instead). ESTIMATE, by eye from ER.
        /// </summary>
        public const float ThrowReturnFrames = 20f;

        /// <summary>Whether a throw has emptied the right / left hand at this frame (every client works it out from the sim).</summary>
        public static (bool Right, bool Left) ThrownHands(SimPlayer p)
        {
            if (p.AirAttackState is AirAttack air)
            {
                bool away = air.Def.Throw != 0 && air.F >= air.Def.From && air.F < air.Def.From + ThrowReturnFrames;
                return (away && air.Def.Throw == 1, away && air.Def.Throw == 2);
            }
            if (p.State != StateKind.Act || !p.Act.Id.IsAttack)
            {
                return (false, false);
            }
            bool right = false, left = false;
            foreach (Hit h in p.Act.Id.Def().Hits)
            {
                if (h.Throw != 0 && p.Act.F >= h.From && p.Act.F < h.From + ThrowReturnFrames)
                {
                    right |= h.Throw == 1;
                    left |= h.Throw == 2;
                }
            }
            return (right, left);
        }

        /// <summary>How far above level a throw leaves, as a slope: every throw flies the same arc from the way the body faces.</summary>
        private const float ThrowLift = 0.08f;

        /// <summary>Thrown from the middle of the body, at chest height, a little ahead.</summary>
        private static Vector3 ThrowOrigin(VPlayer vp)
        {
            Vector3 chest = vp.transform.position + Vector3.up * 1.3f;
            Vector3 from = chest + vp.transform.forward * 0.5f;
            return Physics.Linecast(chest, from, GroundMask, QueryTriggerInteraction.Ignore) ? chest : from;
        }

        /// <summary>The projectile ZDO's spin: 1 end over end (an axe), 2 flat (a hammer), 0 none.</summary>
        public const string ThrownSpin = "eldenheim.thrownspin";

        /// <summary>
        /// Valheim's own spear throw (the weapon's secondary attack: its
        /// projectile, speed, sounds and trail), hitting with this hit's
        /// share of the weapon's damage at any range. The spear stays in the
        /// hand (ER's Smithscript weapons come back): no item rides along.
        /// </summary>
        private bool Throw(VPlayer vp, SimPlayer p, ActiveHit hit, ItemDrop.ItemData weapon, List<string> log)
        {
            Attack throwing = weapon?.m_shared.m_secondaryAttack != null && weapon.m_shared.m_secondaryAttack.m_attackProjectile != null
                ? weapon.m_shared.m_secondaryAttack : weapon?.m_shared.m_attack;
            // Any other weapon on the Smithscript Spear's moveset flies as a flint spear (its trail), carrying its own model.
            bool borrowed = weapon != null && throwing?.m_attackProjectile == null;
            if (borrowed)
            {
                GameObject flint = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("SpearFlint") : null;
                throwing = flint != null ? flint.GetComponent<ItemDrop>().m_itemData.m_shared.m_secondaryAttack : null;
            }
            if (throwing?.m_attackProjectile == null)
            {
                return false;
            }
            Vector3 from = ThrowOrigin(vp);
            Vector3 dir = (vp.transform.forward + Vector3.up * ThrowLift).normalized;
            GameObject go = Object.Instantiate(throwing.m_attackProjectile, from, Quaternion.LookRotation(dir));
            if (borrowed && weapon.m_dropPrefab != null)
            {
                // Every client (this one too) shows it from the projectile's ZDO (Patches: Projectile.UpdateVisual).
                ZDO zdo = go.GetComponent<ZNetView>()?.GetZDO();
                zdo?.Set(ThrownModel, weapon.m_dropPrefab.name);
                string cls = ErData.Weapons[SwingMoveset(p).Weapon].Name;
                zdo?.Set(ThrownSpin, cls == "Smithscript Axe" ? 1 : cls == "Smithscript Hammer" ? 2 : 0);
                ShowThrownModel(go.GetComponent<Projectile>());
            }
            float factor = vp.GetRandomSkillFactor(weapon.m_shared.m_skillType) * MotionScale(p, hit) * Settings.DamageScale.Value;
            var data = new HitData
            {
                m_toolTier = (short)weapon.m_shared.m_toolTier,
                m_pushForce = weapon.m_shared.m_attackForce * throwing.m_forceMultiplier * factor,
                m_backstabBonus = weapon.m_shared.m_backstabBonus,
                m_staggerMultiplier = throwing.m_staggerMultiplier * GuardScale(p, hit) * Settings.PoiseScale.Value,
                m_skillLevel = vp.GetSkillLevel(weapon.m_shared.m_skillType),
                m_itemLevel = (short)weapon.m_quality,
                m_itemWorldLevel = (byte)weapon.m_worldLevel,
                m_blockable = weapon.m_shared.m_blockable,
                m_dodgeable = weapon.m_shared.m_dodgeable,
                m_skill = weapon.m_shared.m_skillType,
                m_skillRaiseAmount = throwing.m_raiseSkillAmount,
                m_hitType = HitData.HitType.PlayerHit,
                m_damage = weapon.GetDamage(),
                // As Attack.FireProjectileBurst; Projectile.Setup takes the hit's effect over its own (0 would drop the harpoon's).
                m_statusEffectHash = weapon.m_shared.m_attackStatusEffect != null && (weapon.m_shared.m_attackStatusEffectChance >= 1f
                    || UnityEngine.Random.Range(0f, 1f) < weapon.m_shared.m_attackStatusEffectChance) ? weapon.m_shared.m_attackStatusEffect.NameHash() : 0,
            };
            data.SetAttacker(vp);
            data.m_damage.Modify(factor);
            vp.GetSEMan().ModifyAttack(weapon.m_shared.m_skillType, ref data);
            // ThrowSpears: the spear itself flies (the projectile drops it where
            // it lands) and leaves the inventory, as vanilla's throw does. Never
            // the harpoon: its line stays in the hand.
            bool consume = Settings.ThrowSpears.Value && throwing.m_consumeItem && !IsHarpoon(weapon) && !borrowed;
            Projectile projectile = go.GetComponent<Projectile>();
            Vector3 velocity = dir * throwing.m_projectileVel;
            go.GetComponent<IProjectile>()?.Setup(vp, velocity, throwing.m_attackHitNoise, data, consume ? weapon : null, null);
            if (Slams(weapon) && projectile != null)
            {
                // A thrown sledge (the Smithscript Hammer's throws) slams where it
                // lands: its shockwave and area blow, scaled as this hit's are.
                var thrown = new Thrown { Motion = MotionScale(p, hit), Guard = GuardScale(p, hit) };
                foreach (Projectile gone in PendingSlams.Keys.Where(k => k == null).ToList())
                {
                    PendingSlams.Remove(gone);
                }
                PendingSlams[projectile] = (collider, at) =>
                {
                    // The struck target already took the projectile's own blow; the
                    // slam's state (a swing's first contact) is left as it was.
                    Vector3? slamAt = _slamAt;
                    _hitThisWindow.Clear();
                    GameObject direct = collider != null ? Projectile.FindHitObject(collider) : null;
                    if (direct != null)
                    {
                        _hitThisWindow.Add(direct);
                    }
                    var lines = new List<string>();
                    Slam(vp, p, weapon, hit, at, hit.Stop * Consts.HIT_STOP_SCALE, lines, thrown);
                    _slamAt = slamAt;
                    lines.ForEach(Plugin.Trace);
                };
            }
            Quaternion facing = Quaternion.LookRotation(dir);
            throwing.m_triggerEffect.Create(from, facing, null, 1f, -1, vp.GetZDOID());
            throwing.m_trailStartEffect.Create(from, facing, null, 1f, -1, vp.GetZDOID());
            throwing.m_startEffect.Create(from, facing, null, 1f, -1, vp.GetZDOID());
            if (consume)
            {
                Controller.Local?.SyncInstantly();
                if (weapon.m_shared.m_maxStackSize > 1 && weapon.m_stack > 1)
                {
                    weapon.m_stack--;
                }
                else
                {
                    vp.UnequipItem(weapon, false);
                    vp.GetInventory().RemoveItem(weapon);
                }
            }
            log.Add($"throw: {throwing.m_attackProjectile.name} at {throwing.m_projectileVel:0} m/s, x{factor:0.00} ({data.GetTotalDamage():0}){(consume ? ", spear thrown away" : "")}");
            CombatLog.Dealt($"{p.Act.Id} → thrown {weapon.m_shared.m_name} (x{factor:0.00}, on impact)", data.GetTotalDamage());
            return true;
        }

        private readonly Dictionary<int, HashSet<GameObject>> _struck = new Dictionary<int, HashSet<GameObject>>();
        private readonly Dictionary<int, Blade> _owners = new Dictionary<int, Blade>();
        private int _hitsSerial = -1;

        /// <summary>
        /// The hand a window belongs to. In power stance (and with fists or
        /// paired weapons) both hands move, but each of ER's hits is one
        /// weapon's: the Valheim blade nearest ER's own capsule as the window
        /// opens. Only that weapon strikes in it, with its own damage (when the
        /// hands hold different weapons).
        /// </summary>
        public static Blade OwnerOf(ActiveHit hit, Swinging hand, Blade right, Blade left)
        {
            if (hand != Swinging.Both)
            {
                return hand == Swinging.Left ? left : right;
            }
            int last = SimPlayer.SWEEP_STEPS - 1;
            Vector3 mid = (Space.ToUnity(hit.SweepA[last]) + Space.ToUnity(hit.SweepB[last])) * 0.5f;
            return Nearer(right, left, mid);
        }

        /// <summary>One hit window's sweeps this tick. True if it struck something new.</summary>
        private bool Window(VPlayer vp, SimPlayer p, ActiveHit hit, int index, Swinging hand, Blade right, Blade left, List<string> log)
        {
            if (!_owners.TryGetValue(index, out Blade main))
            {
                _owners[index] = main = OwnerOf(hit, hand, right, left);
            }
            if (!_struck.TryGetValue(index, out HashSet<GameObject> struck))
            {
                _struck[index] = struck = new HashSet<GameObject>();
                if (IsThrow(p, index) && Throw(vp, p, hit, Item(vp, main), log))
                {
                    struck.Add(vp.gameObject);
                    if (p.AirAttackState.HasValue)
                    {
                        // Thrown in the air: the landing doesn't throw again.
                        p.MarkHit();
                    }
                }
            }
            if (struck.Contains(vp.gameObject))
            {
                // A thrown weapon: the projectile does the hitting.
                return false;
            }
            _hitThisWindow.Clear();
            _hitThisWindow.UnionWith(struck);
            bool landed = false;
            float stop = hit.Stop * Consts.HIT_STOP_SCALE;
            float radius = Mathf.Max(hit.Radius, 0.05f, main != null ? main.Radius : 0f);
            Live = true;
            Radius = radius;
            ErCapsules.Add((Space.ToUnity(hit.SweepA[SimPlayer.SWEEP_STEPS - 1]), Space.ToUnity(hit.SweepB[SimPlayer.SWEEP_STEPS - 1])));

            // The sim's capsule (ER's weapon, on ER's skeleton)...
            for (int i = 0; i < SimPlayer.SWEEP_STEPS; i++)
            {
                Vector3 a = Space.ToUnity(hit.SweepA[i]), b = Space.ToUnity(hit.SweepB[i]);
                Vector3 mid = (a + b) * 0.5f;
                int n = Physics.OverlapCapsuleNonAlloc(a, b, radius, _overlap, AttackMask, QueryTriggerInteraction.Ignore);
                for (int c = 0; c < n; c++)
                {
                    landed |= Strike(vp, p, Item(vp, main), hit, _overlap[c], mid, stop, log);
                }
            }
            // ...and the window's Valheim item itself, swept from where it was
            // a tick ago: a longer Valheim weapon reaches as far as it is drawn.
            // One item in both hands (claws, fists): either hand may land it.
            bool sameItem = hand == Swinging.Both && Item(vp, right) == Item(vp, left);
            foreach (Blade b in sameItem ? new[] { right, left } : new[] { main })
            {
                if (b == null || !b.Valid)
                {
                    continue;
                }
                float r = Mathf.Max(hit.Radius, 0.05f, b.Radius);
                for (int i = 0; i < SimPlayer.SWEEP_STEPS; i++)
                {
                    float t = b.PrevValid ? (float)i / (SimPlayer.SWEEP_STEPS - 1) : 1f;
                    Vector3 a = Vector3.Lerp(b.PrevA, b.A, t), e = Vector3.Lerp(b.PrevB, b.B, t);
                    int n = Physics.OverlapCapsuleNonAlloc(a, e, r, _overlap, AttackMask, QueryTriggerInteraction.Ignore);
                    for (int c = 0; c < n; c++)
                    {
                        landed |= Strike(vp, p, Item(vp, b), hit, _overlap[c], (a + e) * 0.5f, stop, log);
                    }
                }
            }
            ItemDrop.ItemData mainItem = Item(vp, main);
            if (_slamAt.HasValue && _slammed.Count == 0 && Slams(mainItem) && Heavy(p) && !Throws(p) && _slammed.Add(index))
            {
                landed |= Slam(vp, p, mainItem, hit, _slamAt.Value, stop, log);
            }
            _slamAt = null;
            struck.UnionWith(_hitThisWindow);
            if (landed && Settings.HitStop.Value)
            {
                p.HitStop = stop;
            }
            if (Debug && !landed)
            {
                log.Add($"window {index} f={p.Act.F:0.0}: {Nearest(vp, hit, right, left)}");
            }
            return landed;
        }

        /// <summary>A hit window swept this tick, its radius and the sim's own capsules (ER's blade), for the HitboxDebug view.</summary>
        public bool Live;
        public float Radius = 0.05f;
        public readonly List<(Vector3 A, Vector3 B)> ErCapsules = new List<(Vector3, Vector3)>();

        /// <summary>Set by the hitdebug command: log how near each unlanded window's sweeps come to characters around.</summary>
        public static bool Debug;

        private static string Nearest(VPlayer vp, ActiveHit hit, Blade right, Blade left)
        {
            var parts = new List<string>();
            foreach (Character ch in Character.GetAllCharacters())
            {
                if (ch == vp || Vector3.Distance(ch.transform.position, vp.transform.position) > 4f)
                {
                    continue;
                }
                Vector3 c = ch.GetCenterPoint();
                float er = float.MaxValue, item = float.MaxValue;
                for (int i = 0; i < SimPlayer.SWEEP_STEPS; i++)
                {
                    er = Mathf.Min(er, SegmentDistance(Space.ToUnity(hit.SweepA[i]), Space.ToUnity(hit.SweepB[i]), c));
                }
                foreach (Blade b in new[] { right, left })
                {
                    if (b != null && b.Valid)
                    {
                        item = Mathf.Min(item, SegmentDistance(b.A, b.B, c));
                    }
                }
                parts.Add($"{ch.name} centre: ER capsule {er:0.00} m, hands/items {item:0.00} m");
            }
            return parts.Count > 0 ? string.Join("; ", parts) : "nobody near";
        }

        private static float SegmentDistance(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(a + ab * t, p);
        }

        public static Blade Nearer(Blade right, Blade left, Vector3 point)
        {
            // (A bare left fist has no item but is still a hand: Item() makes it Valheim's unarmed weapon.)
            if (left == null || !left.Valid)
            {
                return right;
            }
            if (right == null || !right.Valid)
            {
                return left;
            }
            float dr = Vector3.Distance(point, (right.A + right.B) * 0.5f), dl = Vector3.Distance(point, (left.A + left.B) * 0.5f);
            return dl < dr ? left : right;
        }

        /// <summary>The item a hand strikes with: bare hands are Valheim's unarmed weapon.</summary>
        private static ItemDrop.ItemData Item(VPlayer vp, Blade b) =>
            b?.Item ?? (vp.m_unarmedWeapon != null ? vp.m_unarmedWeapon.m_itemData : null);

        private bool Strike(VPlayer vp, SimPlayer p, ItemDrop.ItemData weapon, ActiveHit hit, Collider collider, Vector3 bladeMid, float stop, List<string> log,
                            Thrown thrown = null)
        {
            GameObject go = Projectile.FindHitObject(collider);
            if (weapon == null || go == null || go == vp.gameObject || _hitThisWindow.Contains(go))
            {
                return false;
            }
            Character character = go.GetComponent<Character>();
            if (character != null)
            {
                bool enemy = BaseAI.IsEnemy(vp, character) || character.GetBaseAI() != null && character.GetBaseAI().IsAggravatable();
                if (!enemy && !(character.IsPlayer() && vp.IsPVPEnabled() && character.IsPVPEnabled()))
                {
                    return false;
                }
                if (weapon.m_shared.m_dodgeable && character.IsDodgeInvincible())
                {
                    return false;
                }
            }
            Vector3 point = collider.ClosestPoint(bladeMid);
            if (point == bladeMid)
            {
                point = collider.bounds.center;
            }
            // Something solid was struck: a sledge's heavy slams here.
            _slamAt = _slamAt ?? point;
            if (thrown == null && Slams(weapon) && !Heavy(p) && !_impactDone)
            {
                // A sledge's hit sound is in its trigger list (its hit list is empty): a swing that doesn't slam still sounds it, once.
                _impactDone = true;
                foreach (EffectList list in new[] { weapon.m_shared.m_triggerEffect, weapon.m_shared.m_attack?.m_triggerEffect })
                {
                    Impact(list)?.Create(point, Quaternion.identity, null, 1f, -1, vp.GetZDOID());
                }
            }
            Attack attack = weapon.m_shared.m_attack;
            IDestructible destructible = go.GetComponent<IDestructible>();
            if (destructible == null)
            {
                // A wall or the ground: the weapon's terrain effect, once a swing.
                if (!_terrainEffectDone && go.GetComponent<Heightmap>() != null)
                {
                    _terrainEffectDone = true;
                    weapon.m_shared.m_hitTerrainEffect.Create(point, Quaternion.identity, null, 1f, -1, vp.GetZDOID());
                    attack?.m_hitTerrainEffect.Create(point, Quaternion.identity, null, 1f, -1, vp.GetZDOID());
                }
                return false;
            }
            _hitThisWindow.Add(go);

            weapon.m_shared.m_hitEffect.Create(point, Quaternion.identity, null, 1f, -1, vp.GetZDOID());
            attack?.m_hitEffect.Create(point, Quaternion.identity, null, 1f, -1, vp.GetZDOID());

            Skills.SkillType skill = weapon.m_shared.m_skillType;
            float factor = vp.GetRandomSkillFactor(skill) * (thrown?.Motion ?? MotionScale(p, hit)) * Settings.DamageScale.Value;
            var data = new HitData
            {
                m_toolTier = (short)weapon.m_shared.m_toolTier,
                m_skillLevel = vp.GetSkillLevel(skill),
                m_itemLevel = (short)weapon.m_quality,
                m_itemWorldLevel = (byte)weapon.m_worldLevel,
                m_pushForce = weapon.m_shared.m_attackForce * factor * (attack?.m_forceMultiplier ?? 1f),
                m_backstabBonus = weapon.m_shared.m_backstabBonus,
                m_staggerMultiplier = (attack?.m_staggerMultiplier ?? 1f) * (thrown?.Guard ?? GuardScale(p, hit)) * Settings.PoiseScale.Value,
                m_dodgeable = weapon.m_shared.m_dodgeable,
                m_blockable = weapon.m_shared.m_blockable,
                m_skill = skill,
                m_skillRaiseAmount = attack?.m_raiseSkillAmount ?? 1f,
                m_damage = weapon.GetDamage(),
                m_point = point,
                m_dir = (point - vp.transform.position).normalized,
                m_hitCollider = collider,
                m_hitType = HitData.HitType.PlayerHit,
            };
            data.SetAttacker(vp);
            data.m_damage.Modify(factor);
            vp.GetSEMan().ModifyAttack(skill, ref data);
            destructible.Damage(data);

            if (weapon.m_shared.m_useDurability)
            {
                weapon.m_durability -= weapon.m_shared.m_useDurabilityDrain * Game.m_durabilityRate;
            }
            vp.RaiseSkill(skill, (attack?.m_raiseSkillAmount ?? 1f) * (character != null ? 1.5f : 1f));
            vp.AddNoise(attack?.m_attackHitNoise ?? 30f);
            if (character != null && Settings.HitStop.Value)
            {
                character.FreezeFrame(stop);
            }
            LastHit = $"{go.name} x{factor:0.00} ({data.GetTotalDamage():0})";
            CombatLog.Dealt($"{(p.State == StateKind.Act ? p.Act.Id.ToString() : "air attack")} → {go.name.Replace("(Clone)", "")} ({weapon.m_shared.m_name}, x{factor:0.00})", data.GetTotalDamage());
            log.Add("Hit " + LastHit);
            return true;
        }

        // A window's first contact (anything solid, the ground too), and the windows that slammed this swing.
        private Vector3? _slamAt;
        private readonly HashSet<int> _slammed = new HashSet<int>();

        /// <summary>The projectile ZDO's key naming the item a borrowed spear projectile shows.</summary>
        public const string ThrownModel = "eldenheim.thrown";
        private const string ModelName = "EldenheimThrownModel";

        /// <summary>
        /// A borrowed spear projectile shows the thrown weapon named in its ZDO,
        /// on every client, once: the item's model (its "attach", as an item
        /// stand shows it) with its tip along the flight, as a spear's. The tip
        /// is the far end of the longest mesh's long axis from the grip (the
        /// model's origin).
        /// </summary>
        public static void ShowThrownModel(Projectile projectile)
        {
            ZNetView nview = projectile != null ? projectile.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid())
            {
                return;
            }
            Transform shown = projectile.transform.Find(ModelName);
            if (shown != null)
            {
                int spin = nview.GetZDO().GetInt(ThrownSpin);
                if (spin != 0)
                {
                    // The axe head over forwards; the hammer counterclockwise seen from above.
                    shown.localRotation = Quaternion.AngleAxis(Time.time * (spin == 1 ? 900f : -720f), spin == 1 ? Vector3.right : Vector3.up);
                }
                return;
            }
            string item = nview.GetZDO().GetString(ThrownModel);
            GameObject prefab = item.Length > 0 && ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
            GameObject attach = prefab != null ? ItemStand.GetAttachPrefab(prefab) : null;
            if (attach == null)
            {
                return;
            }
            foreach (MeshRenderer r in projectile.GetComponentsInChildren<MeshRenderer>())
            {
                r.enabled = false;
            }
            // Under a pivot at the flight line, which spins it (see above).
            Transform pivot = new GameObject(ModelName).transform;
            pivot.SetParent(projectile.transform, false);
            GameObject model = Object.Instantiate(ItemStand.GetAttachGameObject(attach), pivot, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            foreach (Collider c in model.GetComponentsInChildren<Collider>())
            {
                Object.Destroy(c);
            }
            MeshFilter longest = model.GetComponentsInChildren<MeshFilter>().Where(m => m.sharedMesh != null)
                .OrderByDescending(m => m.transform.TransformVector(m.sharedMesh.bounds.size).magnitude).FirstOrDefault();
            if (longest == null)
            {
                return;
            }
            Bounds b = longest.sharedMesh.bounds;
            Vector3 e = b.size;
            Vector3 axis = e.x >= e.y && e.x >= e.z ? new Vector3(e.x, 0f, 0f) : e.y >= e.z ? new Vector3(0f, e.y, 0f) : new Vector3(0f, 0f, e.z);
            Vector3 a = model.transform.InverseTransformPoint(longest.transform.TransformPoint(b.center + axis * 0.5f));
            Vector3 z = model.transform.InverseTransformPoint(longest.transform.TransformPoint(b.center - axis * 0.5f));
            Vector3 tip = a.sqrMagnitude >= z.sqrMagnitude ? a - z : z - a;
            model.transform.localRotation = Quaternion.FromToRotation(tip, Vector3.forward);
            if (nview.GetZDO().GetInt(ThrownSpin) == 1)
            {
                // An axe tumbles in its blade's plane, edge leading: the head's second-longest
                // axis, on the side the mesh's middle sits from the haft (the grip, at the origin).
                Vector3 across = axis.x != 0f ? (e.y >= e.z ? new Vector3(0f, e.y, 0f) : new Vector3(0f, 0f, e.z))
                    : axis.y != 0f ? (e.x >= e.z ? new Vector3(e.x, 0f, 0f) : new Vector3(0f, 0f, e.z))
                    : e.x >= e.y ? new Vector3(e.x, 0f, 0f) : new Vector3(0f, e.y, 0f);
                Vector3 edge = Vector3.ProjectOnPlane(model.transform.InverseTransformPoint(longest.transform.TransformPoint(b.center + across * 0.5f))
                    - model.transform.InverseTransformPoint(longest.transform.TransformPoint(b.center - across * 0.5f)), tip);
                if (Vector3.Dot(edge, (a + z) * 0.5f) < 0f)
                {
                    edge = -edge;
                }
                if (edge.sqrMagnitude > 1e-6f)
                {
                    // Head forward, edge down: a quarter turn earlier the head is up and the edge leads.
                    model.transform.localRotation = Quaternion.Inverse(Quaternion.LookRotation(tip, -edge));
                }
            }
            if (nview.GetZDO().GetInt(ThrownSpin) != 0)
            {
                // A spinning item turns about its middle, not its grip.
                model.transform.localPosition = -(model.transform.localRotation * ((a + z) * 0.5f));
            }
        }

        /// <summary>The Abyssal Harpoon: its throw harpoons (SE_Harpooned).</summary>
        private static bool IsHarpoon(ItemDrop.ItemData weapon) => weapon?.m_shared.m_attackStatusEffect is SE_Harpooned;

        /// <summary>Valheim's sledges: their primary attack is an area attack (the shockwave).</summary>
        public static bool Slams(ItemDrop.ItemData weapon) => weapon?.m_shared.m_attack?.m_attackType == Attack.AttackType.Area;

        private static bool Heavy(SimPlayer p)
        {
            AttackKind k = p.AirAttackState.HasValue ? SimPlayer.LandingOf(p.AirAttackState.Value)
                : p.State == StateKind.Act && p.Act.Id.IsAttack ? p.Act.Id.Kind : AttackKind.Light1;
            return k == AttackKind.Heavy1 || k == AttackKind.Heavy1Charge || k == AttackKind.Heavy2 || k == AttackKind.Heavy2Charge ||
                   k == AttackKind.RunHeavy || k == AttackKind.JumpHeavyLand || k == AttackKind.JumpHeavyLandShort || k == AttackKind.GuardCounter;
        }

        /// <summary>
        /// Attack.DoAreaAttack where the heavy struck: the shockwave (the
        /// weapon's trigger effects) and a blow to everything in its sphere.
        /// </summary>
        private bool Slam(VPlayer vp, SimPlayer p, ItemDrop.ItemData weapon, ActiveHit hit, Vector3 at, float stop, List<string> log, Thrown thrown = null)
        {
            Attack attack = weapon.m_shared.m_attack;
            foreach (EffectList list in new[] { weapon.m_shared.m_triggerEffect, attack.m_triggerEffect })
            {
                Shockwave(list)?.Create(at, vp.transform.rotation, null, 1f, -1, vp.GetZDOID());
            }
            float radius = attack.m_attackRayWidth + attack.m_attackRayWidthCharExtra;
            bool landed = false;
            int n = Physics.OverlapSphereNonAlloc(at, radius, _overlap, AttackMask, QueryTriggerInteraction.Ignore);
            for (int c = 0; c < n; c++)
            {
                landed |= Strike(vp, p, weapon, hit, _overlap[c], at, stop, log, thrown);
            }
            log.Add($"slam: {weapon.m_shared.m_name} at {at:F1}, radius {radius:0.0} m, {(landed ? "hit" : "nothing")} around");
            AoeShown = (at, radius, Time.time);
            return landed;
        }

        // Sledges keep their swing (sfx_sledge_swing) in the start and trail lists, and their hit sound with the shockwave in the trigger list.
        private static bool IsSwing(EffectList.EffectData e) => e.m_prefab.name.IndexOf("swing", System.StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsShockwave(EffectList list, ItemDrop.ItemData weapon, EffectList.EffectData e) =>
            (list == weapon.m_shared.m_triggerEffect || list == weapon.m_shared.m_attack?.m_triggerEffect) && !IsSwing(e);

        /// <summary>A trigger list without its swing sound: the shockwave.</summary>
        private static EffectList Shockwave(EffectList list)
        {
            if (list?.m_effectPrefabs == null)
            {
                return null;
            }
            return new EffectList { m_effectPrefabs = list.m_effectPrefabs.Where(e => e != null && e.m_prefab != null && !IsSwing(e)).ToArray() };
        }

        /// <summary>A sledge trigger list's impact sounds alone (sfx_sledge_hit), without the swing or the shockwave's visuals.</summary>
        private static EffectList Impact(EffectList list)
        {
            if (list?.m_effectPrefabs == null)
            {
                return null;
            }
            return new EffectList { m_effectPrefabs = list.m_effectPrefabs.Where(e => e != null && e.m_prefab != null && !IsSwing(e) && e.m_prefab.name.StartsWith("sfx_")).ToArray() };
        }

        /// <summary>The last slam's sphere (HitboxDebug shows it for a moment).</summary>
        public (Vector3 At, float Radius, float Time) AoeShown = (Vector3.zero, 0f, -10f);

        /// <summary>
        /// Motion value against its moveset's whole first light attack: Light1
        /// deals the weapon's own damage in total, so an attack ER lands as two
        /// hits (claws, paired swings) deals about half of it with each.
        /// </summary>
        public static float MotionScale(SimPlayer p, ActiveHit hit)
        {
            ActionDef light = SwingMoveset(p).Attack(AttackKind.Light1);
            float total = light != null ? light.Hits.Sum(h => h.Mv) : 0f;
            return total > 0f ? hit.Mv / total : 1f;
        }

        private static float GuardScale(SimPlayer p, ActiveHit hit)
        {
            Hit first = SwingMoveset(p).Attack(AttackKind.Light1)?.FirstHit;
            return first != null && first.GuardDamage > 0f ? hit.GuardDamage / first.GuardDamage : 1f;
        }

        /// <summary>The moveset the current attack comes from (the left weapon's, for a left-hand attack).</summary>
        public static Moveset SwingMoveset(SimPlayer p)
        {
            if (p.AirAttackState is AirAttack air)
            {
                return air.Moveset;
            }
            if (p.State == StateKind.Act && p.Act.Id.IsAttack)
            {
                Moveset m = p.Act.Id.Moveset;
                // Paired and left-hand kinds only exist in the one-handed set.
                return m.Has(AttackKind.Light1) ? m : new Moveset(m.Weapon, false);
            }
            return p.Moveset();
        }

        /// <summary>
        /// Valheim plays a swing's sound and starts its trail on the clip's
        /// trigger frame. Here that is each hit's own first active frame.
        /// </summary>
        public void Swings(VPlayer vp, SimPlayer p, Blade swing, Blade second = null, bool remote = false)
        {
            bool air = p.AirAttackState.HasValue;
            bool acting = p.State == StateKind.Act;
            ActionId act = acting ? p.Act.Id : default;
            float f = air ? p.AirAttackState.Value.F : acting ? p.Act.F : -1f;
            bool same = air == _lastAir && (air || acting && act == _lastAct) && f >= _lastF;
            // A jump attack's landing carries on its swing: its first window's sound has played.
            bool landing = _lastAir && !air && acting && act.IsAttack && SimPlayer.IsJumpLanding(act.Kind);
            if (!same)
            {
                _actSerial++;
                _lastF = -1f;
            }
            float from = _lastF;
            _lastAct = act;
            _lastAir = air;
            _lastF = f;

            float[] opens = null, closes = null;
            if (air)
            {
                AirAttackDef d = p.AirAttackState.Value.Def;
                opens = new[] { d.From };
                closes = new[] { d.To };
            }
            else if (acting && act.IsAttack)
            {
                Hit[] hits = act.Def().Hits;
                opens = new float[hits.Length];
                closes = new float[hits.Length];
                for (int i = 0; i < hits.Length; i++)
                {
                    opens[i] = hits[i].From;
                    closes[i] = hits[i].To;
                }
            }
            if (!same && !landing)
            {
                _sounded.Clear();
            }
            if (opens != null)
            {
                // A fully charged heavy sounds heavier.
                bool charged = !air && act.IsAttack && (act.Kind == AttackKind.Heavy1Charge || act.Kind == AttackKind.Heavy2Charge);
                // Not before the charge is kept: let go earlier, it becomes the uncharged heavy, with its own sounds.
                float earliest = charged && act.Def().HasCharge ? act.Def().ChargeTo : 0f;
                for (int i = 0; i < opens.Length; i++)
                {
                    if (f < closes[i])
                    {
                        Blade early = second == null ? swing : EarlyOwner(p, air, act, i, swing, second);
                        SwingSounds(vp, Item(vp, early), i, opens[i], f, earliest, charged, remote, IsThrow(p, i));
                    }
                    if (from < opens[i] && f >= opens[i])
                    {
                        // Both hands moving: the trail is the window's own
                        // weapon's (its owner, as for damage).
                        Blade by = swing;
                        if (second != null)
                        {
                            ActiveHit h = air ? p.ActiveHitNow() : p.WindowHit(i);
                            by = h != null ? OwnerOf(h, Swinging.Both, swing, second) : swing;
                        }
                        SwingLog = $"swing: {(air ? "air attack" : act.ToString())} at frame {f:0.0}, window {opens[i]:0.#}-{closes[i]:0.#} ({Item(vp, by)?.m_shared.m_name})";
                        if (Settings.SwingTrails.Value)
                        {
                            Trails(by, true);
                            if (by != null && !_trailed.Contains(by))
                            {
                                _trailed.Add(by);
                            }
                        }
                        // Held a few frames past the window, as the vanilla clips do.
                        _trailUntil = Mathf.Max(_trailSerial == _actSerial ? _trailUntil : -1f, closes[i] + 4f);
                        _trailSerial = _actSerial;
                    }
                }
            }
            if (_trailUntil >= 0f && (_trailSerial != _actSerial || f >= _trailUntil))
            {
                _trailUntil = -1f;
                StopTrails();
            }
        }

        private readonly List<Blade> _trailed = new List<Blade>();

        public void StopTrails()
        {
            foreach (Blade b in _trailed)
            {
                Trails(b, false);
            }
            _trailed.Clear();
        }

        /// <summary>The white swing trail of whatever that hand holds (VisEquipment only does the right item).</summary>
        private static void Trails(Blade b, bool on)
        {
            if (b?.Instance == null)
            {
                return;
            }
            if (on && b.Valid && b.Item?.m_shared.m_skillType == Skills.SkillType.Spears
                && b.Instance.GetComponentInChildren<MeleeWeaponTrail>(true) == null && Controller.MovesetClass(b.Item) == "Guardian's Swordspear")
            {
                AddTrail(b);
            }
            foreach (MeleeWeaponTrail trail in b.Instance.GetComponentsInChildren<MeleeWeaponTrail>(true))
            {
                trail.Emit = on;
            }
        }

        private static readonly string[] TrailLooks = { "_emitTime", "_material", "_lifeTime", "_colors", "_sizes", "_minVertexDistance", "_maxVertexDistance", "_maxAngle", "subdivisions" };

        /// <summary>
        /// Valheim's spears have no swing trail; slashing as a swordspear, one is
        /// added like the iron sword's, along the head's part of the blade (its
        /// outer 40%), on child points of the item so it swings with it.
        /// </summary>
        private static void AddTrail(Blade b)
        {
            GameObject donor = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("SwordIron") : null;
            MeleeWeaponTrail look = donor != null ? donor.GetComponentInChildren<MeleeWeaponTrail>(true) : null;
            if (look == null)
            {
                return;
            }
            Transform item = b.Instance.transform;
            var from = new GameObject("EldenheimTrailBase").transform;
            var to = new GameObject("EldenheimTrailTip").transform;
            from.SetParent(item, false);
            to.SetParent(item, false);
            from.position = Vector3.Lerp(b.A, b.B, 0.6f);
            to.position = b.B;
            MeleeWeaponTrail trail = to.gameObject.AddComponent<MeleeWeaponTrail>();
            foreach (string f in TrailLooks)
            {
                var field = AccessTools.Field(typeof(MeleeWeaponTrail), f);
                field.SetValue(trail, field.GetValue(look));
            }
            AccessTools.Field(typeof(MeleeWeaponTrail), "_base").SetValue(trail, from);
            AccessTools.Field(typeof(MeleeWeaponTrail), "_tip").SetValue(trail, to);
            trail.Emit = false;
        }

        // --- Swing sounds ------------------------------------------------------
        //
        // Valheim's swing sounds are recordings whose loudest moment comes some
        // way in (the unarmed swing's well after its start: vanilla plays it as
        // the attack begins). Each sound is started early by as much, so its
        // peak lands on the window's first active frame; a sound played slower
        // (a charged heavy's deeper copies) reaches its peak later still and
        // starts earlier again. Effects without sound (camera shake) fire on
        // the frame itself. One that can't start that early (the attack, or its
        // kept charge, begins later) starts part way into its recording instead.

        private readonly HashSet<int> _sounded = new HashSet<int>();
        private static readonly Dictionary<GameObject, float> PeakOf = new Dictionary<GameObject, float>();
        private static readonly Dictionary<AudioClip, float> ClipPeak = new Dictionary<AudioClip, float>();
        private static readonly System.Reflection.MethodInfo GetData = typeof(AudioClip).GetMethod("GetData", new[] { typeof(float[]), typeof(int) });

        /// <summary>One effect of a swing: a single entry of one of the weapon's lists, played at a pitch and volume.</summary>
        private struct SwingPart
        {
            public EffectList.EffectData Effect;
            public float Pitch, Volume;
            public bool Changed => Pitch != 1f || Volume != 1f;
        }

        /// <summary>The swing's effects: the weapon's own lists; charged, louder and lower, plus each sound again an octave down.</summary>
        private static List<SwingPart> PartsOf(ItemDrop.ItemData weapon, bool charged, bool thrown = false)
        {
            var parts = new List<SwingPart>();
            bool harpoon = IsHarpoon(weapon);
            if (harpoon && !thrown)
            {
                // The harpoon has no swing sound of its own: the flint spear's (its throws keep their own throw sound).
                GameObject flint = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab("SpearFlint") : null;
                weapon = flint != null ? flint.GetComponent<ItemDrop>().m_itemData : weapon;
            }
            if (harpoon && thrown && weapon.m_shared.m_startEffect?.m_effectPrefabs != null)
            {
                foreach (EffectList.EffectData e in weapon.m_shared.m_startEffect.m_effectPrefabs)
                {
                    if (e != null && e.m_enabled && e.m_prefab != null)
                    {
                        parts.Add(new SwingPart { Effect = e, Pitch = 1f, Volume = 1f });
                    }
                }
                return parts;
            }
            // A sledge's trigger effect is its shockwave: played by Slam, where a heavy lands.
            // A sledge's trigger effect is its swing sound and its shockwave together: the swing plays here, the rest where a heavy slams (Slam).
            bool slams = Slams(weapon);
            var lists = new List<EffectList> { weapon.m_shared.m_triggerEffect, weapon.m_shared.m_trailStartEffect };
            Attack attack = weapon.m_shared.m_attack;
            if (attack != null)
            {
                lists.Add(attack.m_triggerEffect);
                lists.Add(attack.m_trailStartEffect);
            }
            // Bare hands: unarmed's swing sound is its start effect.
            if (weapon.m_shared.m_skillType == Skills.SkillType.Unarmed)
            {
                lists.Add(weapon.m_shared.m_startEffect);
            }
            foreach (EffectList list in lists)
            {
                if (list?.m_effectPrefabs == null)
                {
                    continue;
                }
                foreach (EffectList.EffectData e in list.m_effectPrefabs)
                {
                    if (e == null || !e.m_enabled || e.m_prefab == null || slams && IsShockwave(list, weapon, e))
                    {
                        continue;
                    }
                    parts.Add(new SwingPart { Effect = e, Pitch = charged ? 0.8f : 1f, Volume = charged ? 1.6f : 1f });
                    if (charged && Sounding(e.m_prefab))
                    {
                        parts.Add(new SwingPart { Effect = e, Pitch = 0.5f, Volume = 1.6f });
                    }
                }
            }
            return parts;
        }

        /// <summary>Starts each of a window's swing effects when its peak would reach the window's first frame (once each).</summary>
        private void SwingSounds(VPlayer vp, ItemDrop.ItemData weapon, int window, float open, float f, float earliest, bool charged, bool remote, bool thrown)
        {
            if (weapon == null)
            {
                return;
            }
            List<SwingPart> parts = PartsOf(weapon, charged, thrown);
            for (int k = 0; k < parts.Count; k++)
            {
                int key = window * 64 + k;
                float start = open - Peak(parts[k].Effect.m_prefab) / parts[k].Pitch * Consts.ANIM_FPS;
                if (_sounded.Contains(key) || f < Mathf.Max(start, earliest) || remote && !parts[k].Changed)
                {
                    continue;
                }
                _sounded.Add(key);
                if (k == 0 && !remote)
                {
                    Plugin.Trace($"swing sound: window {window} from {weapon.m_shared.m_name} (right {Controller.Local?.Right?.m_shared.m_name ?? "-"}, left {Controller.Local?.Left?.m_shared.m_name ?? "-"})");
                }
                Play(vp, parts[k], (f - start) / Consts.ANIM_FPS);
            }
        }

        private static void Play(VPlayer vp, SwingPart part, float late = 0f)
        {
            Transform origin = vp.transform;
            Quaternion facing = Quaternion.LookRotation(origin.forward);
            Vector3 at = origin.position + Vector3.up * 1.2f;
            var one = new EffectList { m_effectPrefabs = new[] { part.Effect } };
            // A changed copy plays on each client by itself: through the network
            // it would arrive at its prefab's own pitch and volume.
            ZNetView.m_forceDisableInit = part.Changed;
            GameObject[] made;
            try
            {
                made = one.Create(at, facing, origin, 1f, -1, vp.GetZDOID());
            }
            finally
            {
                ZNetView.m_forceDisableInit = false;
            }
            if (made != null && part.Changed)
            {
                foreach (GameObject go in made)
                {
                    Deepen(go, part.Volume, part.Pitch);
                }
            }
            if (made != null && late > 0.03f)
            {
                foreach (GameObject go in made)
                {
                    SkipInto(go, late, part.Pitch);
                }
            }
        }

        /// <summary>Plays an effect's sounds now, from as far into the recording as they are late (in seconds; slower when lower).</summary>
        private static void SkipInto(GameObject go, float late, float pitch)
        {
            if (go == null)
            {
                return;
            }
            foreach (ZSFX z in go.GetComponentsInChildren<ZSFX>())
            {
                float skip = late * pitch - Mathf.Max(z.m_minDelay, 0f);
                if (skip <= 0.03f)
                {
                    continue;
                }
                // Played here rather than in its Start, so it can be moved on.
                Delay(z) = -1f;
                z.Play();
                AudioSource source = z.GetComponent<AudioSource>();
                if (source != null && source.isPlaying && source.clip != null && source.clip.loadState == AudioDataLoadState.Loaded && skip < source.clip.length)
                {
                    source.time = skip;
                }
            }
        }

        /// <summary>The swing's Valheim sounds and effects, all at once (strength above 1: louder and deeper).</summary>
        public static void SwingEffects(VPlayer vp, ItemDrop.ItemData weapon, float strength = 1f)
        {
            if (weapon == null)
            {
                return;
            }
            foreach (SwingPart part in PartsOf(weapon, strength > 1f))
            {
                Play(vp, part);
            }
        }

        /// <summary>The sounds of one of Valheim's effect lists played again, at a volume and pitch (no visuals).</summary>
        public static void Sounds(VPlayer vp, EffectList list, float volume, float pitch)
        {
            if (list?.m_effectPrefabs == null || volume <= 0f)
            {
                return;
            }
            foreach (EffectList.EffectData e in list.m_effectPrefabs)
            {
                if (e != null && e.m_enabled && e.m_prefab != null && Sounding(e.m_prefab))
                {
                    Play(vp, new SwingPart { Effect = e, Pitch = pitch, Volume = volume });
                }
            }
        }

        private static bool Sounding(GameObject prefab) => prefab.GetComponentInChildren<ZSFX>(true) != null;

        /// <summary>
        /// Seconds from an effect's start to its sound's loudest moment (the
        /// mean over its recordings, plus its own start delay); 0 without sound.
        /// </summary>
        private static float Peak(GameObject prefab)
        {
            if (PeakOf.TryGetValue(prefab, out float known))
            {
                return known;
            }
            ZSFX z = prefab.GetComponentInChildren<ZSFX>(true);
            if (z == null || z.m_audioClips == null || z.m_audioClips.Length == 0)
            {
                return PeakOf[prefab] = 0f;
            }
            float sum = 0f;
            int n = 0;
            foreach (AudioClip clip in z.m_audioClips)
            {
                float t = ClipPeakTime(clip);
                if (t < 0f)
                {
                    // Still loading: ask again next time.
                    return 0f;
                }
                sum += t;
                n++;
            }
            float peak = (n > 0 ? sum / n : 0f) + Mathf.Max(z.m_minDelay, 0f);
            Plugin.Trace($"swing sound {prefab.name}: peak {peak * 1000f:0} ms after its start ({n} recordings)");
            return PeakOf[prefab] = peak;
        }

        /// <summary>The middle of a clip's loudest 20 ms within its first second (-1: not loaded yet; 0: unreadable).</summary>
        private static float ClipPeakTime(AudioClip clip)
        {
            if (clip == null)
            {
                return 0f;
            }
            if (ClipPeak.TryGetValue(clip, out float known))
            {
                return known;
            }
            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                if (clip.loadState == AudioDataLoadState.Unloaded)
                {
                    clip.LoadAudioData();
                }
                if (clip.loadState != AudioDataLoadState.Failed)
                {
                    return -1f;
                }
                return ClipPeak[clip] = 0f;
            }
            if (clip.loadType != AudioClipLoadType.DecompressOnLoad)
            {
                // A compressed clip's samples can't be read (Unity logs an error per
                // clip, e.g. the spear thrusts): played at the window, as Valheim does.
                return ClipPeak[clip] = 0f;
            }
            int channels = Mathf.Max(clip.channels, 1), rate = Mathf.Max(clip.frequency, 1);
            int frames = Mathf.Min(clip.samples, rate);
            var data = new float[clip.samples * channels];
            float peak = 0f;
            try
            {
                // net472 can't bind Unity 6's Span overload: the float[] one by reflection.
                if (frames > 0 && GetData?.Invoke(clip, new object[] { data, 0 }) is bool got && got)
                {
                    int window = Mathf.Max(rate / 50, 1);
                    float best = -1f;
                    for (int start = 0; start + window <= frames; start += Mathf.Max(window / 2, 1))
                    {
                        float energy = 0f;
                        for (int s = start * channels; s < (start + window) * channels; s++)
                        {
                            energy += data[s] * data[s];
                        }
                        if (energy > best)
                        {
                            best = energy;
                            peak = (start + window * 0.5f) / rate;
                        }
                    }
                }
            }
            catch (System.Exception)
            {
                peak = 0f;
            }
            return ClipPeak[clip] = peak;
        }

        /// <summary>
        /// Which hand a window will belong to, before it opens (for its sound):
        /// the side of the body ER's own blade is on as the window opens.
        /// </summary>
        private static Blade EarlyOwner(SimPlayer p, bool air, ActionId act, int window, Blade right, Blade left)
        {
            if (left == null || !left.Valid)
            {
                return right;
            }
            if (right == null || !right.Valid)
            {
                return left;
            }
            float[][] blade = air ? p.AirAttackState.Value.Def.Blade : act.Def().Hits[window].Blade;
            if (blade == null || blade.Length == 0)
            {
                return right;
            }
            // The sim's X runs to the character's left.
            float side = blade[0][0] + blade[0][3];
            return side > 0f ? left : right;
        }

        private static readonly AccessTools.FieldRef<ZSFX, float> VolumeModifier = AccessTools.FieldRefAccess<ZSFX, float>("m_volumeModifier");
        private static readonly AccessTools.FieldRef<ZSFX, float> PitchModifier = AccessTools.FieldRefAccess<ZSFX, float>("m_pitchModifier");
        private static readonly AccessTools.FieldRef<ZSFX, float> Delay = AccessTools.FieldRefAccess<ZSFX, float>("m_delay");

        /// <summary>
        /// Louder and at a lower pitch. ZSFX sets its AudioSource's volume and
        /// pitch every frame from its own values, so its modifiers carry it.
        /// </summary>
        private static void Deepen(GameObject go, float volume, float pitch)
        {
            if (go == null)
            {
                return;
            }
            foreach (ZSFX z in go.GetComponentsInChildren<ZSFX>())
            {
                VolumeModifier(z) *= volume;
                PitchModifier(z) *= pitch;
            }
        }

        public static string DescribeEffects(ItemDrop.ItemData weapon)
        {
            if (weapon == null)
            {
                return "no weapon";
            }
            string List(EffectList e) => e == null || e.m_effectPrefabs == null ? "-" :
                string.Join(",", System.Array.ConvertAll(e.m_effectPrefabs, p => p.m_prefab != null ? p.m_prefab.name + (p.m_enabled ? "" : "(off)") : "null"));
            Attack a = weapon.m_shared.m_attack;
            return $"{weapon.m_shared.m_name}: shared trigger[{List(weapon.m_shared.m_triggerEffect)}] trailStart[{List(weapon.m_shared.m_trailStartEffect)}] " +
                   $"start[{List(weapon.m_shared.m_startEffect)}] hit[{List(weapon.m_shared.m_hitEffect)}]; attack '{a?.m_attackAnimation}' trigger[{List(a?.m_triggerEffect)}] " +
                   $"trailStart[{List(a?.m_trailStartEffect)}] start[{List(a?.m_startEffect)}] hit[{List(a?.m_hitEffect)}]";
        }
    }
}
