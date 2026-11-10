using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Eldenheim
{
    /// <summary>
    /// Test harness: commands.txt beside the DLL, one per line, consumed once;
    /// results go to results.log there.
    ///   status | effects | quit | wait S
    ///   press ACTION [S] | hold ACTION | release ACTION | stick X Y [S]   (actions as in the config)
    ///   demo [FROM_TICK] | demo stop
    ///   equip PREFAB [left]        equip an inventory item (left: into the left hand)
    ///   deform start|stop        worst bone rotations away from the bind pose, over a window
    ///   tp X Y Z | face DEG
    /// </summary>
    internal static class Commands
    {
        private static readonly Queue<string> Queue = new Queue<string>();
        private static float _waitUntil;
        private static readonly List<(float at, int frame, Action act)> Timers = new List<(float, int, Action)>();
        /// <summary>Items the give command added this session (remove only takes these back).</summary>
        private static readonly HashSet<ItemDrop.ItemData> Given = new HashSet<ItemDrop.ItemData>();
        private static Dictionary<string, float> _deform;
        /// <summary>probe: deepest left-arm point inside the torso meshes, per action frame (depth, which point).</summary>
        private static Dictionary<string, (float depth, string point)> _probe;
        private static readonly Dictionary<SkinnedMeshRenderer, int[]> TorsoVerts = new Dictionary<SkinnedMeshRenderer, int[]>();
        private static Mesh _baked;
        private static Vector3? _cam;
        private static float _probeWorst;
        private static string _probeRun;
        private static List<float> _smooth;
        private static List<string> _ytrace;
        private static Dictionary<string, float> _cape;
        private static int _capeFrames, _capeOver, _capeInside;
        private static float _capeDeep, _capeDeepest, _capeBase;
        private static readonly Dictionary<string, float> _capeIn = new Dictionary<string, float>();

        /// <summary>capeprobe: the simulated cape's furthest particle in front of the chest this frame.</summary>
        private static void SampleCape(Player vp)
        {
            Transform chest = vp.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "Spine2");
            Transform hips = vp.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "Hips");
            Transform neck = vp.GetComponentsInChildren<Transform>().FirstOrDefault(t => t.name == "Neck");
            var sim = MagicaCloth2.MagicaManager.Simulation;
            var teams = MagicaCloth2.MagicaManager.Team?.teamDataArray;
            if (chest == null || sim?.dispPosArray == null || teams == null)
            {
                return;
            }
            float most = float.MinValue;
            foreach (MagicaCloth2.MagicaCloth mc in vp.GetComponentsInChildren<MagicaCloth2.MagicaCloth>())
            {
                int team = mc.Process?.TeamId ?? 0;
                if (team <= 0 || team >= teams.Count)
                {
                    continue;
                }
                var chunk = teams[team].particleChunk;
                for (int i = chunk.startIndex; i < chunk.startIndex + chunk.dataLength && i < sim.dispPosArray.Count; i++)
                {
                    Vector3 v = sim.dispPosArray[i];
                    most = Mathf.Max(most, Vector3.Dot(v - chest.position, vp.transform.forward));
                    if (i < sim.basePosArray.Count)
                    {
                        _capeBase = Mathf.Max(_capeBase, Vector3.Dot((Vector3)sim.basePosArray[i] - chest.position, vp.transform.forward));
                    }
                    if (hips != null && neck != null)
                    {
                        // Inside the torso: within 14 cm of the hips-to-neck line.
                        Vector3 ab = neck.position - hips.position;
                        float t = Mathf.Clamp01(Vector3.Dot(v - hips.position, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                        Vector3 spine = hips.position + ab * t;
                        // Only the front half: the cape's pinned edge hangs on the back, by the spine.
                        if (Vector3.Dot(v - spine, vp.transform.forward) > 0f)
                        {
                            _capeDeep = Mathf.Max(_capeDeep, 0.14f - Vector3.Distance(v, spine));
                        }
                    }
                }
            }
            if (most == float.MinValue)
            {
                return;
            }
            Controller c = Controller.Local;
            string at = c?.Sim == null ? "?" : c.Sim.State == Eldenheim.Sim.StateKind.Act ? $"{c.Sim.Act.Id} f{c.Sim.Act.F:0}" : c.Sim.State.ToString();
            _capeFrames++;
            if (most > 0.1f)
            {
                _capeOver++;
            }
            if (!_cape.TryGetValue(at, out float was) || most > was)
            {
                _cape[at] = most;
            }
            if (_capeDeep > 0f)
            {
                _capeInside++;
                _capeDeepest = Mathf.Max(_capeDeepest, _capeDeep);
                if (!_capeIn.TryGetValue(at, out float deep) || _capeDeep > deep)
                {
                    _capeIn[at] = _capeDeep;
                }
            }
            _capeDeep = 0f;
        }
        private static Vector3 _smoothLast;

        public static void Poll(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                foreach (string l in File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")))
                {
                    Queue.Enqueue(l);
                }
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        public static void Tick()
        {
            for (int i = Timers.Count - 1; i >= 0; i--)
            {
                if (Time.time >= Timers[i].at && Time.frameCount >= Timers[i].frame)
                {
                    Action a = Timers[i].act;
                    Timers.RemoveAt(i);
                    a();
                }
            }
            if (_deform != null)
            {
                SampleDeform();
            }
            if (_cape != null && Player.m_localPlayer != null)
            {
                SampleCape(Player.m_localPlayer);
            }
            if (_ytrace != null && Player.m_localPlayer != null && Controller.Local?.Sim is Eldenheim.Sim.Player sp)
            {
                Vector3 b = Player.m_localPlayer.transform.position;
                _ytrace.Add($"{Time.time:0.000} body {b.x:0.00},{b.y:0.000},{b.z:0.00} sim y {sp.Pos.Y:0.000} {sp.State} {(sp.State == Eldenheim.Sim.StateKind.Act ? sp.Act.Id.ToString() : "")} speed {sp.Speed:0.0} slip {Controller.Local.Level?.Slippage:0.00} n.y {Controller.Local.Level?.GroundNormal.y:0.00} miss {Controller.Local.LastMiss.y:0.000},{Controller.Local.LastMiss.z:0.000} hips {Controller.Local.HipsY:0.000} aim {sp.AimPitch * Mathf.Rad2Deg:0} shown {Controller.Local.AimShown * Mathf.Rad2Deg:0.00} chest {Controller.Local.ChestPitch:0.00} clip {Controller.Local.ShownClip}");
            }
            if (_smooth != null && Player.m_localPlayer != null && Time.deltaTime > 0f)
            {
                Vector3 now = Player.m_localPlayer.transform.position;
                _smooth.Add(new Vector2(now.x - _smoothLast.x, now.z - _smoothLast.z).magnitude / Time.deltaTime);
                _smoothLast = now;
            }
            Controller c = Controller.Local;
            if (c != null && c.Log.Count > 0)
            {
                if (Settings.EventLog.Value)
                {
                    foreach (string line in c.Log)
                    {
                        Plugin.Report("  " + line);
                    }
                }
                c.Log.Clear();
            }
            while (Queue.Count > 0 && Time.time >= _waitUntil)
            {
                string line = Queue.Peek();
                if (Player.m_localPlayer == null && !line.StartsWith("wait"))
                {
                    return;
                }
                Queue.Dequeue();
                try
                {
                    Run(line);
                }
                catch (Exception e)
                {
                    Plugin.Report($"{line}: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static string Tree(Transform t, int depth)
        {
            var mf = t.GetComponent<MeshFilter>();
            var smr = t.GetComponent<SkinnedMeshRenderer>();
            string self = $"{t.name}(lp {t.localPosition:F2} lr {t.localEulerAngles:F0}{(mf != null ? " mesh " + mf.sharedMesh?.bounds.size.ToString("F2") : "")}{(smr != null ? " skinned" : "")}{(t.gameObject.activeSelf ? "" : " off")})";
            if (depth > 3 || t.childCount == 0)
            {
                return self;
            }
            var kids = new List<string>();
            for (int i = 0; i < t.childCount; i++)
            {
                kids.Add(Tree(t.GetChild(i), depth + 1));
            }
            return self + " [" + string.Join("; ", kids) + "]";
        }

        private static Vector3 Axis(string s)
        {
            switch (s)
            {
                case "up": return Vector3.up;
                case "down": return Vector3.down;
                case "forward": return Vector3.forward;
                case "back": return Vector3.back;
                case "left": return Vector3.left;
                default: return Vector3.right;
            }
        }

        private static void Run(string line)
        {
            string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Player vp = Player.m_localPlayer;
            Controller c = vp != null ? Controller.For(vp) : null;
            switch (a[0].ToLowerInvariant())
            {
                case "wait":
                    _waitUntil = Time.time + F(a[1]);
                    break;
                case "status":
                    Plugin.Report($"status: pos {vp.transform.position:F2} yaw {vp.transform.eulerAngles.y:0} health {vp.GetHealth():0}/{vp.GetMaxHealth():0} " +
                                  $"stamina {vp.GetStamina():0}/{vp.GetMaxStamina():0} | {c.Status()}");
                    break;
                case "effects":
                    Plugin.Report("effects: " + Combat.DescribeEffects(c.Right ?? (vp.m_unarmedWeapon != null ? vp.m_unarmedWeapon.m_itemData : null)));
                    break;
                case "press":
                {
                    string action = a[1];
                    float secs = a.Length > 2 ? F(a[2]) : 0.12f;
                    Bindings.Forced[action] = true;
                    Timers.Add((Time.time + secs, Time.frameCount + 3, () => Bindings.Forced.Remove(action)));
                    break;
                }
                case "hold":
                    Bindings.Forced[a[1]] = true;
                    break;
                case "release":
                    Bindings.Forced[a[1]] = false;
                    Timers.Add((Time.time + 0.1f, Time.frameCount + 3, () => Bindings.Forced.Remove(a[1])));
                    break;
                case "stick":
                    Bindings.ForcedStick = new Vector2(F(a[1]), F(a[2]));
                    if (a.Length > 3)
                    {
                        Timers.Add((Time.time + F(a[3]), Time.frameCount + 3, () => Bindings.ForcedStick = null));
                    }
                    break;
                case "equip":
                {
                    // equip PREFAB [left] | equip none: test the left-hand slot without the keyboard.
                    if (a[1] == "none")
                    {
                        vp.UnequipAllItems();
                        break;
                    }
                    var matching = vp.GetInventory().GetAllItems().Where(i => i.m_dropPrefab != null && i.m_dropPrefab.name == a[1]).ToList();
                    ItemDrop.ItemData item = matching.FirstOrDefault(i => !i.m_equipped) ?? matching.FirstOrDefault();
                    if (item == null)
                    {
                        Plugin.Report($"equip: no {a[1]} in the inventory");
                        break;
                    }
                    if (a.Length > 2 && a[2] == "left")
                    {
                        Bindings.Forced["TwoHandModifier"] = true;
                    }
                    bool ok = vp.EquipItem(item);
                    Bindings.Forced.Remove("TwoHandModifier");
                    Plugin.Report($"equip: {a[1]} {(ok ? "ok" : "refused")}; right {c.Right?.m_dropPrefab?.name} left {c.Left?.m_dropPrefab?.name}");
                    break;
                }
                case "cleanup":
                {
                    // cleanup PREFAB_PREFIX: destroy pieces of that kind near the player that
                    // nobody built (spawned by a test); pieces a player built keep their creator.
                    foreach (Piece piece in UnityEngine.Object.FindObjectsOfType<Piece>())
                    {
                        if (piece == null || !piece.name.StartsWith(a[1]) || Vector3.Distance(piece.transform.position, vp.transform.position) > 20f)
                        {
                            continue;
                        }
                        long creator = piece.GetCreator();
                        Plugin.Report($"cleanup: {piece.name} at {piece.transform.position:F1} creator {creator}{(creator == 0 ? " → removed" : " (kept)")}");
                        if (creator == 0)
                        {
                            ZNetScene.instance.Destroy(piece.gameObject);
                        }
                    }
                    break;
                }
                case "duals":
                    // Items Valheim dual-wields as one (their attack animation says "dual"), and fist weapons.
                    Plugin.Report("duals: " + string.Join(", ", ObjectDB.instance.m_items.Select(g => g.GetComponent<ItemDrop>())
                        .Where(d => d != null && (d.m_itemData.m_shared.m_skillType == Skills.SkillType.Unarmed || (d.m_itemData.m_shared.m_attack?.m_attackAnimation ?? "").Contains("dual")))
                        .Select(d => $"{d.name} [{d.m_itemData.m_shared.m_name}, {d.m_itemData.m_shared.m_attack?.m_attackAnimation}]")));
                    break;
                case "dumpvis":
                {
                    // dumpvis: what VisEquipment built for the hands and back, as a tree.
                    VisEquipment vis = vp.GetComponent<VisEquipment>();
                    foreach (string field in new[] { "m_rightItemInstance", "m_leftItemInstance", "m_rightBackItemInstance", "m_leftBackItemInstance" })
                    {
                        GameObject go = Traverse.Create(vis).Field(field).GetValue<GameObject>();
                        Plugin.Report($"dumpvis {field}: {(go != null ? Tree(go.transform, 0) : "none")}");
                    }
                    break;
                }
                case "clipfeet":
                {
                    // clipfeet CLIP: feet apart, pelvis height and knee bend in a clip's first frame (is it a braced stance?).
                    Anim.Clip clip = Controller.Clips.Get(a[1]);
                    if (clip == null)
                    {
                        Plugin.Report($"clipfeet: no {a[1]}");
                        break;
                    }
                    var pose = new Anim.ErPose(Controller.Clips.Bones.Length);
                    Controller.Clips.Sample(clip, 10f, true, pose);
                    int lf = Controller.Clips.Bone("L_Foot"), rf = Controller.Clips.Bone("R_Foot"), pel = Controller.Clips.Bone("Pelvis");
                    Plugin.Report($"clipfeet {a[1]}: feet {pose.Pos[lf]:F2} / {pose.Pos[rf]:F2}, apart {Vector3.Distance(pose.Pos[lf], pose.Pos[rf]):0.00} m, pelvis y {pose.Pos[pel].y:0.00}");
                    break;
                }
                case "stamina":
                    // stamina N: set the player's stamina (to test the low-stamina rules).
                    vp.UseStamina(vp.GetStamina() - F(a[1]));
                    break;
                case "hitdebug":
                    Combat.Debug = a[1] == "on";
                    break;
                case "unequip":
                    foreach (ItemDrop.ItemData i in vp.GetInventory().GetAllItems().Where(i => i.m_equipped && i.m_dropPrefab != null && i.m_dropPrefab.name == a[1]).ToList())
                    {
                        vp.UnequipItem(i);
                    }
                    break;
                case "remove":
                {
                    // remove PREFAB [any]: take back what give added of that kind; "any" also the character's own (the test character is disposable).
                    int n = 0;
                    bool any = a.Length > 2 && a[2] == "any";
                    foreach (ItemDrop.ItemData i in vp.GetInventory().GetAllItems().Where(i => (any || Given.Contains(i)) && i.m_dropPrefab != null && i.m_dropPrefab.name == a[1]).ToList())
                    {
                        vp.UnequipItem(i, false);
                        vp.GetInventory().RemoveItem(i);
                        Given.Remove(i);
                        n++;
                    }
                    Plugin.Report($"remove: {a[1]} x{n}");
                    break;
                }
                case "inv":
                    Plugin.Report("inv: " + string.Join(", ", vp.GetInventory().GetAllItems().Select(i =>
                        $"{i.m_dropPrefab?.name}{(i.m_equipped ? "*" : "")}{(i.m_customData.ContainsKey(Hands.LeftMark) ? "(L)" : "")}@{i.m_gridPos.x},{i.m_gridPos.y}")));
                    break;
                case "cloth":
                    // cloth: each cloth on the player (its settings as JSON, cut) and the colliders it meets.
                    foreach (MagicaCloth2.MagicaCloth mc in vp.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true))
                    {
                        var sd = mc.SerializeData;
                        if (mc.Process != null && MagicaCloth2.MagicaManager.Team?.teamDataArray is var teams && teams != null && mc.Process.TeamId > 0 && mc.Process.TeamId < teams.Count)
                        {
                            var td = teams[mc.Process.TeamId];
                            Plugin.Report($"cloth: {mc.name} team {mc.Process.TeamId}: runs as {td.updateMode} (set {td.originalUpdateMode}), this frame {td.updateCount} steps, frame time {td.frameDeltaTime:0.0000}");
                        }
                        string json = sd.ExportJson();
                        Plugin.Report($"cloth: {mc.name} under {mc.transform.parent?.name} type {sd.clothType} roots {string.Join(",", sd.rootBones.Where(b => b != null).Select(b => b.name))} " +
                                      $"mode {sd.colliderCollisionConstraint.mode} colliders " + string.Join("; ", sd.colliderCollisionConstraint.colliderList.Where(cc => cc != null)
                                          .Select(cc => $"{cc.GetType().Name} on {cc.transform.name} size {cc.GetSize():F3} centre {cc.center:F3}")));
                        File.WriteAllText(Path.Combine(Plugin.Dir, $"cloth-{vp.GetInventory().GetAllItems().FirstOrDefault(i => i.m_equipped && i.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder)?.m_dropPrefab?.name}-{mc.name}.json"), json);
                    }
                    break;
                case "vanilla":
                    // vanilla: Valheim's own movement numbers for this player, and its skills.
                    Plugin.Report($"vanilla: walk {vp.m_walkSpeed} jog {vp.m_speed} run {vp.m_runSpeed} crouch {vp.m_crouchSpeed} jumpForce {vp.m_jumpForce} fwd {vp.m_jumpForceForward} " +
                                  $"gravity {Physics.gravity.y} drag {vp.GetComponent<Rigidbody>().linearDamping} run skill {vp.GetSkillFactor(Skills.SkillType.Run):0.00} jump skill {vp.GetSkillFactor(Skills.SkillType.Jump):0.00}");
                    break;
                case "vjump":
                    // vjump: Valheim's own jump (run with Eldenheim off: setting Enabled false).
                    vp.Jump(true);
                    break;
                case "skill":
                    // skill NAME LEVEL: set a skill's level (Jump, Run...).
                    vp.GetSkills().CheatResetSkill(a[1]);
                    vp.GetSkills().CheatRaiseSkill(a[1], F(a[2]), false);
                    break;
                case "landings":
                    // landings: the held moveset's jump-attack landings (frames: total, cancel by moving, first hit) and its air swings.
                    {
                        var m = c.Sim.Moveset();
                        foreach (var k in new[] { Eldenheim.Sim.AttackKind.JumpLightLand, Eldenheim.Sim.AttackKind.JumpLightLandShort, Eldenheim.Sim.AttackKind.JumpHeavyLand, Eldenheim.Sim.AttackKind.JumpHeavyLandShort })
                        {
                            var d = m.Attack(k);
                            Plugin.Report(d == null ? $"landings: {k} none" : $"landings: {k} total {d.Total:0} move {d.CancelMove:0} light {d.CancelLight:0} first hit {d.FirstHit?.From:0}-{d.FirstHit?.To:0} src {d.Source}");
                        }
                        foreach (bool heavy in new[] { false, true })
                        {
                            var d = m.Air(heavy);
                            Plugin.Report($"landings: air {(heavy ? "heavy" : "light")} {d?.From:0}-{d?.To:0} src {d?.Source}");
                        }
                    }
                    break;
                case "capeprobe":
                    // capeprobe start|stop: per frame, how far the simulated cape (MagicaCloth's particles) gets in front of the chest
                    // (along the body's facing, from the Spine2 bone); the worst per action frame.
                    if (a[1] == "start")
                    {
                        _cape = new Dictionary<string, float>();
                        _capeFrames = _capeOver = _capeInside = 0;
                        _capeDeepest = _capeBase = 0f;
                        _capeIn.Clear();
                    }
                    else if (_cape != null)
                    {
                        Plugin.Report($"capeprobe: {_capeFrames} frames, {_capeOver} with the cape over 10 cm in front of the chest; worst: " +
                                      string.Join(", ", _cape.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} {kv.Value:+0.00;-0.00}")) +
                                      $" | the skinned cape's own worst {_capeBase:+0.00}" + $" | INSIDE the torso on {_capeInside} frames, deepest {_capeDeepest * 100f:0} cm: " +
                                      string.Join(", ", _capeIn.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value * 100f:0}")));
                        _cape = null;
                    }
                    break;
                case "clothset":
                    // clothset PATH VALUE: set one of the player's cloth settings (e.g. inertiaConstraint.worldInertia 0.5) and rebuild its parameters.
                    foreach (MagicaCloth2.MagicaCloth mc in vp.GetComponentsInChildren<MagicaCloth2.MagicaCloth>())
                    {
                        object o = mc.SerializeData;
                        string[] path = a[1].Split('.');
                        for (int i = 0; i < path.Length - 1; i++)
                        {
                            o = o.GetType().GetField(path[i]).GetValue(o);
                        }
                        var field = o.GetType().GetField(path[path.Length - 1]);
                        field.SetValue(o, Convert.ChangeType(a[2], field.FieldType, CultureInfo.InvariantCulture));
                        mc.SetParameterChange();
                        Plugin.Report($"clothset: {mc.name} {a[1]} = {field.GetValue(o)}");
                    }
                    break;
                case "heal":
                    vp.Heal(1000f);
                    break;
                case "ytrace":
                    // ytrace start|stop: per rendered frame, the body's height and the sim's state (bounces on slopes).
                    if (a[1] == "start")
                    {
                        _ytrace = new List<string>();
                    }
                    else if (_ytrace != null)
                    {
                        Plugin.Report("ytrace:\n" + string.Join("\n", _ytrace));
                        _ytrace = null;
                    }
                    break;
                case "ramp":
                {
                    // ramp DEG: a 12 m slope of DEG degrees (a box collider, drawn) rising ahead of the player from its feet.
                    float deg = F(a[1]);
                    // ramp DEG bumpy: a terrain-like mesh instead (1 m facets, heights off by up to 15 cm).
                    if (a.Length > 2 && a[2] == "bumpy")
                    {
                        var rampGo = new GameObject("eh_test_ramp");
                        rampGo.layer = LayerMask.NameToLayer("terrain");
                        Vector3 f2 = vp.transform.forward;
                        f2.y = 0f;
                        f2.Normalize();
                        Vector3 r2 = Vector3.Cross(Vector3.up, f2);
                        Vector3 o = vp.transform.position + f2 * 1f;
                        float tan = Mathf.Tan(F(a[1]) * Mathf.Deg2Rad);
                        var rng = new System.Random(7);
                        var verts = new List<Vector3>();
                        var tris = new List<int>();
                        const int W = 6, L = 13;
                        for (int j = 0; j < L; j++)
                        {
                            for (int i = 0; i < W; i++)
                            {
                                float bump = j == 0 ? 0f : (float)(rng.NextDouble() * 0.3 - 0.15);
                                verts.Add(o + r2 * (i - (W - 1) / 2f) + f2 * j + Vector3.up * (j * tan + bump));
                            }
                        }
                        for (int j = 0; j < L - 1; j++)
                        {
                            for (int i = 0; i < W - 1; i++)
                            {
                                int v = j * W + i;
                                tris.AddRange(new[] { v, v + W, v + 1, v + 1, v + W, v + W + 1 });
                            }
                        }
                        var mesh = new Mesh { vertices = verts.ToArray(), triangles = tris.ToArray() };
                        mesh.RecalculateNormals();
                        rampGo.AddComponent<MeshFilter>().sharedMesh = mesh;
                        rampGo.AddComponent<MeshCollider>().sharedMesh = mesh;
                        Plugin.Report($"ramp: bumpy {a[1]}° from {o:F1}");
                        break;
                    }
                    GameObject ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    ramp.name = "eh_test_ramp";
                    ramp.layer = LayerMask.NameToLayer("static_solid");
                    Vector3 fwd = vp.transform.forward;
                    fwd.y = 0f;
                    fwd.Normalize();
                    Quaternion tilt = Quaternion.LookRotation(fwd) * Quaternion.Euler(-deg, 0f, 0f);
                    ramp.transform.rotation = tilt;
                    ramp.transform.localScale = new Vector3(4f, 1f, 12f);
                    // The top face's near edge at the feet, 1 m ahead.
                    Vector3 edge = vp.transform.position + fwd * 1f;
                    ramp.transform.position = edge + tilt * new Vector3(0f, -0.5f, 6f);
                    Plugin.Report($"ramp: {deg}° from {edge:F1} toward yaw {Quaternion.LookRotation(fwd).eulerAngles.y:0}");
                    break;
                }
                case "steep":
                {
                    // steep R [MIN MAX]: ground within R m sloped MIN-MAX degrees (default 38-65: a slope that slides), nearest first.
                    float r = a.Length > 1 ? F(a[1]) : 40f, lo = a.Length > 2 ? F(a[2]) : 38f, hi = a.Length > 3 ? F(a[3]) : 65f;
                    var found = new List<(float d, string what)>();
                    for (float x = -r; x <= r; x += 1f)
                    {
                        for (float z = -r; z <= r; z += 1f)
                        {
                            Vector3 at = vp.transform.position + new Vector3(x, 50f, z);
                            if (Physics.Raycast(at, Vector3.down, out RaycastHit h, 120f, PhysicsLevel.GroundMask))
                            {
                                float deg = Mathf.Acos(Mathf.Clamp01(h.normal.y)) * Mathf.Rad2Deg;
                                if (deg >= lo && deg <= hi)
                                {
                                    Vector3 down = Vector3.Cross(h.normal, Vector3.Cross(h.normal, Vector3.up));
                                    found.Add((new Vector2(x, z).magnitude, $"{deg:0}° at {h.point:F1} on {h.collider.name}, uphill yaw {Quaternion.LookRotation(new Vector3(-down.x, 0f, -down.z)).eulerAngles.y:0}"));
                                }
                            }
                        }
                    }
                    Plugin.Report($"steep: {found.Count} points; " + string.Join(" | ", found.OrderBy(f => f.d).Take(12).Select(f => f.what)));
                    // Where they cluster (5 m cells, most first): a slope rather than a stray rock.
                    Plugin.Report("steep cells: " + string.Join(" | ", found.Select(f => f.what)
                        .GroupBy(w => { string[] q = w.Split('(', ')')[1].Split(','); return $"{Mathf.Round(F(q[0]) / 5f) * 5f},{Mathf.Round(F(q[2]) / 5f) * 5f}"; })
                        .OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Key} x{g.Count()} ({g.First()})")));
                    break;
                }
                case "smooth":
                    // smooth start|stop: how evenly the player moves from one rendered frame to the next.
                    if (a[1] == "start")
                    {
                        _smooth = new List<float>();
                        _smoothLast = vp.transform.position;
                    }
                    else if (_smooth != null)
                    {
                        List<float> s = _smooth.Where(d => d > 1e-4f).ToList();
                        float mean = s.Count > 0 ? s.Average() : 0f;
                        float sd = s.Count > 0 ? Mathf.Sqrt(s.Average(d => (d - mean) * (d - mean))) : 0f;
                        Plugin.Report($"smooth: {s.Count} frames, speed {mean:0.00} m/s, frame-to-frame spread {sd / Mathf.Max(mean, 1e-4f) * 100f:0.0}%, worst {(s.Count > 0 ? s.Max() / mean : 0f):0.00}x mean");
                        _smooth = null;
                    }
                    break;
                case "sound":
                    // sound CLIP: play a clip's signature sound (the guard counter's is a0xx_03x700).
                    ErSounds.PlaySignature(vp, a[1]);
                    Plugin.Report($"sound: {a[1]} requested");
                    break;
                case "leftaxes":
                    // leftaxes BLADE SHIELD (each up|down|forward|back|left|right): try other ER bone axes for the left hand.
                    Controller.LeftBladeAxis = Axis(a[1]);
                    Controller.ShieldFaceAxis = Axis(a[2]);
                    break;
                case "mv":
                {
                    // mv WEAPON: motion values per attack, for checking charged and counter bonuses.
                    int wi = System.Array.FindIndex(Sim.ErData.Weapons, w => w.Name == string.Join(" ", a.Skip(1)));
                    foreach (bool two in new[] { false, true })
                    {
                        var m = new Sim.Moveset(wi, two);
                        var parts = new List<string>();
                        foreach (Sim.AttackKind k in System.Enum.GetValues(typeof(Sim.AttackKind)))
                        {
                            Sim.ActionDef d = m.Attack(k);
                            if (d != null && d.Hits.Length > 0)
                            {
                                parts.Add($"{k} {string.Join("+", d.Hits.Select(h => h.Mv.ToString("0.00")))}");
                            }
                        }
                        Plugin.Report($"mv {(two ? "2H" : "1H")}: {string.Join(", ", parts)}");
                    }
                    break;
                }
                case "inventory":
                    // inventory open|close: Valheim's inventory screen, to test moving with it open.
                    if (a[1] == "open")
                    {
                        InventoryGui.instance.Show(null);
                    }
                    else
                    {
                        InventoryGui.instance.Hide();
                    }
                    break;
                case "hide":
                    vp.HideHandItems();
                    break;
                case "show":
                    Traverse.Create(vp).Method("ShowHandItems", false, true).GetValue();
                    break;
                case "give":
                {
                    // give PREFAB: one into the inventory (remembered: remove takes back only what give added).
                    GameObject prefab = ObjectDB.instance.GetItemPrefab(a[1]);
                    var before = new HashSet<ItemDrop.ItemData>(vp.GetInventory().GetAllItems());
                    bool added = prefab != null && vp.GetInventory().AddItem(prefab, 1);
                    foreach (ItemDrop.ItemData i in vp.GetInventory().GetAllItems().Where(i => !before.Contains(i)))
                    {
                        Given.Add(i);
                    }
                    Plugin.Report($"give: {a[1]} {(prefab == null ? "not found" : added ? "ok" : "FAILED (inventory full?)")}");
                    break;
                }
                case "deform":
                    if (a[1] == "start")
                    {
                        _deform = new Dictionary<string, float>();
                    }
                    else if (_deform != null)
                    {
                        Plugin.Report("deform: " + string.Join(" ", _deform.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"{kv.Key}={kv.Value:0}")));
                        _deform = null;
                    }
                    break;
                case "tp":
                    vp.transform.position = new Vector3(F(a[1]), F(a[2]), F(a[3]));
                    vp.GetComponent<Rigidbody>().position = vp.transform.position;
                    // Not a fall from the old height (Valheim's landing reads it).
                    Traverse.Create(vp).Field("m_maxAirAltitude").SetValue(-10000f);
                    if (c.Sim != null)
                    {
                        c.Sim.Pos = Space.ToSim(vp.transform.position);
                    }
                    break;
                case "face":
                    vp.GetComponent<Rigidbody>().rotation = Quaternion.Euler(0f, F(a[1]), 0f);
                    vp.SetLookDir(Quaternion.Euler(0f, F(a[1]), 0f) * Vector3.forward);
                    if (c.Sim != null)
                    {
                        c.Sim.Yaw = Space.YawToSim(F(a[1]));
                    }
                    break;
                case "look":
                    // look DEG: turn the camera only (the body keeps its facing), e.g. to see the front.
                    vp.SetLookDir(Quaternion.Euler(F(a.Length > 2 ? a[2] : "10"), F(a[1]), 0f) * Vector3.forward);
                    // The player's look is its mouse yaw and pitch, set again from them every frame.
                    Traverse.Create(vp).Field("m_lookYaw").SetValue(Quaternion.Euler(0f, F(a[1]), 0f));
                    Traverse.Create(vp).Field("m_lookPitch").SetValue(F(a.Length > 2 ? a[2] : "10"));
                    break;
                case "shot":
                    // shot NAME: the game's own screenshot, this frame, to shots/NAME.png beside the DLL.
                    Directory.CreateDirectory(Path.Combine(Plugin.Dir, "shots"));
                    ScreenCapture.CaptureScreenshot(Path.Combine(Plugin.Dir, "shots", a[1] + ".png"));
                    Plugin.Report($"shot: {a[1]} | {c.Status()}");
                    break;
                case "clear":
                    // clear PREFAB_PREFIX: remove creatures a test spawned (e.g. clear Greyling).
                    foreach (Character ch in Character.GetAllCharacters().ToList())
                    {
                        if (ch != vp && ch.name.StartsWith(a[1]))
                        {
                            ZNetScene.instance.Destroy(ch.gameObject);
                            Plugin.Report($"clear: {ch.name}");
                        }
                    }
                    break;
                case "near":
                    foreach (Character ch in Character.GetAllCharacters())
                    {
                        Vector3 to = ch.transform.position - vp.transform.position;
                        if (ch != vp && to.magnitude < (a.Length > 1 ? F(a[1]) : 15f))
                        {
                            Vector3 look = GameCamera.instance != null ? GameCamera.instance.transform.forward : vp.transform.forward;
                            Plugin.Report($"near: {ch.name} {to.magnitude:0.0} m at {ch.transform.position:F1} height {ch.GetHeight():0.0}, dot {Vector3.Dot(to.normalized, look):0.00}, enemy {BaseAI.IsEnemy(vp, ch)}, dead {ch.IsDead()}, layer {LayerMask.LayerToName(ch.gameObject.layer)}");
                        }
                    }
                    Plugin.Report($"near: guard held? sim guarding {c.Sim?.Guarding} up {c.Sim?.GuardUp()} t {c.Sim?.GuardT} forced {(Bindings.Forced.TryGetValue("Guard", out bool g) && g)}");
                    break;
                case "spawn":
                {
                    // spawn PREFAB DISTANCE: a creature (or anything) ahead of the player.
                    GameObject prefab = ZNetScene.instance.GetPrefab(a[1]);
                    Vector3 at = vp.transform.position + vp.transform.forward * (a.Length > 2 ? F(a[2]) : 3f);
                    if (prefab != null)
                    {
                        UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(-vp.transform.forward));
                    }
                    Plugin.Report($"spawn: {a[1]} {(prefab != null ? "at " + at.ToString("F1") : "not found")}");
                    break;
                }
                case "noon":
                    // Daylight for screenshots.
                    EnvMan.instance.m_debugTimeOfDay = true;
                    EnvMan.instance.m_debugTime = 0.5f;
                    break;
                case "turn":
                    // turn DEG: the body only (the camera stays), e.g. to show a side in screenshots.
                    vp.GetComponent<Rigidbody>().rotation = Quaternion.Euler(0f, F(a[1]), 0f);
                    if (c.Sim != null)
                    {
                        c.Sim.Yaw = Space.YawToSim(F(a[1]));
                    }
                    break;
                case "cam":
                    // cam X Y Z | cam off: hold the camera at that offset in the body's frame, looking at the chest.
                    _cam = a[1] == "off" ? (Vector3?)null : new Vector3(F(a[1]), F(a[2]), F(a[3]));
                    GameCamera.instance.enabled = _cam == null;
                    break;
                case "parryfx":
                    // parryfx: what Valheim's parry effect plays (each prefab, and whether it sounds).
                    foreach (EffectList.EffectData e in vp.m_perfectBlockEffect.m_effectPrefabs)
                    {
                        Plugin.Report($"parryfx: {e.m_prefab?.name} enabled {e.m_enabled} sound {e.m_prefab?.GetComponentInChildren<ZSFX>(true) != null || e.m_prefab?.GetComponentInChildren<AudioSource>(true) != null} " +
                                      $"particles {e.m_prefab?.GetComponentInChildren<ParticleSystem>(true) != null}");
                    }
                    break;
                case "camdist":
                    Traverse.Create(GameCamera.instance).Field("m_distance").SetValue(F(a[1]));
                    break;
                case "comps":
                    // comps: every Animator on the player (and its avatar), and any cloth (Magica) component.
                    foreach (Animator an in vp.GetComponentsInChildren<Animator>(true))
                    {
                        Plugin.Report($"comps: Animator on {an.name} (parent {an.transform.parent?.name}) enabled {an.enabled} avatar {(an.avatar == null ? "none" : an.avatar.name + (an.avatar.isValid ? " valid" : " INVALID") + (an.avatar.isHuman ? " human" : " generic"))} init {an.isInitialized} update {an.updateMode} cull {an.cullingMode}");
                    }
                    foreach (MonoBehaviour mb in vp.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null && m.GetType().FullName.IndexOf("Magica", StringComparison.OrdinalIgnoreCase) >= 0).Take(12))
                    {
                        Plugin.Report($"comps: {mb.GetType().FullName} on {mb.name} enabled {mb.enabled}");
                    }
                    break;
                case "compsof":
                    // compsof NAME: component types on the player's objects whose name contains NAME, and under them.
                    foreach (Transform t in vp.GetComponentsInChildren<Transform>(true).Where(t => t.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0).Take(4))
                    {
                        Plugin.Report($"compsof: {t.name} under {t.parent?.name}: " + string.Join(", ", t.GetComponentsInChildren<Component>(true)
                            .Where(c => c != null).GroupBy(c => c.GetType().Name).Select(g => $"{g.Key} x{g.Count()}")));
                    }
                    break;
                case "items":
                    // items TEXT: item prefabs whose name or token contains TEXT.
                    Plugin.Report("items: " + string.Join(", ", ObjectDB.instance.m_items.Select(g => g.GetComponent<ItemDrop>())
                        .Where(d => d != null && (d.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0 || d.m_itemData.m_shared.m_name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) >= 0))
                        .Select(d => $"{d.name} [{d.m_itemData.m_shared.m_name}, {d.m_itemData.m_shared.m_itemType}, {d.m_itemData.m_shared.m_skillType}]")));
                    break;
                case "goto":
                {
                    // goto PREFIX DIST: stand DIST m in front of the nearest object whose name starts with PREFIX, facing it.
                    GameObject best = null;
                    foreach (Collider col in Physics.OverlapSphere(vp.transform.position, 60f))
                    {
                        Transform root = col.transform.root;
                        if (root.name.StartsWith(a[1]) && (best == null || Vector3.Distance(root.position, vp.transform.position) < Vector3.Distance(best.transform.position, vp.transform.position)))
                        {
                            best = root.gameObject;
                        }
                    }
                    if (best == null)
                    {
                        Plugin.Report($"goto: no {a[1]} near");
                        break;
                    }
                    Vector3 fwd = (a.Length > 3 ? Quaternion.Euler(0f, F(a[3]), 0f) : Quaternion.identity) * best.transform.forward;
                    fwd.y = 0f;
                    Bounds bb = best.GetComponentsInChildren<Collider>().Select(col => col.bounds).Aggregate((x, y) => { x.Encapsulate(y); return x; });
                    Vector3 at = new Vector3(bb.center.x, bb.min.y, bb.center.z) - fwd.normalized * (a.Length > 2 ? F(a[2]) : 1.5f);
                    if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out RaycastHit ground, 10f, PhysicsLevel.GroundMask))
                    {
                        at = ground.point + Vector3.up * 0.05f;
                    }
                    vp.transform.position = at;
                    vp.GetComponent<Rigidbody>().position = at;
                    float yaw = Quaternion.LookRotation(fwd).eulerAngles.y;
                    vp.GetComponent<Rigidbody>().rotation = Quaternion.Euler(0f, yaw, 0f);
                    if (c.Sim != null)
                    {
                        c.Sim.Yaw = Space.YawToSim(yaw);
                        c.Sim.Pos = Space.ToSim(at);
                    }
                    vp.SetLookDir(Quaternion.Euler(10f, yaw, 0f) * Vector3.forward);
                    Plugin.Report($"goto: {best.name} at {best.transform.position:F1}, standing at {at:F1} facing {yaw:0}");
                    break;
                }
                case "colliders":
                {
                    // colliders PREFIX: the nearest such object's colliders (type, layer, centre, size, rotation).
                    GameObject best = null;
                    foreach (Collider col in Physics.OverlapSphere(vp.transform.position, 60f))
                    {
                        Transform root = col.transform.root;
                        if (root.name.StartsWith(a[1]) && (best == null || Vector3.Distance(root.position, vp.transform.position) < Vector3.Distance(best.transform.position, vp.transform.position)))
                        {
                            best = root.gameObject;
                        }
                    }
                    foreach (Collider col in best != null ? best.GetComponentsInChildren<Collider>(true) : new Collider[0])
                    {
                        string shape = col is BoxCollider bc ? $"box {bc.center:F2} size {bc.size:F2}" : col is MeshCollider mc ? $"mesh {mc.sharedMesh?.name} convex {mc.convex}" : col is CapsuleCollider cc ? $"capsule r {cc.radius:F2} h {cc.height:F2}" : col.GetType().Name;
                        Plugin.Report($"colliders: {col.name} {shape} layer {LayerMask.LayerToName(col.gameObject.layer)} trigger {col.isTrigger} enabled {col.enabled} rot {col.transform.rotation.eulerAngles:F0} bounds {col.bounds.center:F2} {col.bounds.size:F2}");
                    }
                    break;
                }
                case "timescale":
                    // timescale X: slow the game (and the sim with it) to catch poses in screenshots.
                    Time.timeScale = F(a[1]);
                    break;
                case "probe":
                    // probe start|stop: how deep the left forearm, wrist and fingers go into the torso
                    // (the body's and the armour's own skinned meshes, as drawn), per action frame.
                    if (a[1] == "start")
                    {
                        _probe = new Dictionary<string, (float, string)>();
                        _probeWorst = 0f;
                        _probeRun = a.Length > 2 ? a[2] : "worst";
                        Directory.CreateDirectory(Path.Combine(Plugin.Dir, "shots"));
                        TorsoVerts.Clear();
                        break;
                    }
                    if (_probe != null)
                    {
                        foreach (var kv in _probe.OrderByDescending(kv => kv.Value.depth).Take(a.Length > 2 ? int.Parse(a[2]) : 25))
                        {
                            Plugin.Report($"probe: {kv.Key} {kv.Value.depth * 100f:0.0} cm ({kv.Value.point})");
                        }
                        Plugin.Report($"probe: {_probe.Count} frames, {_probe.Count(kv => kv.Value.depth > 0.005f)} deeper than 0.5 cm");
                    }
                    _probe = null;
                    break;
                case "shaft":
                    // shaft: the right item's cross-section centres along its axis, and the left grip point, in the right attach's space.
                    Plugin.Report("shaft: " + c.ShaftSections());
                    break;
                case "setting":
                    // setting KEY VALUE: any F1 setting, by its key (e.g. setting ThrowSpears true).
                    switch (a[1])
                    {
                        case "ThrowSpears": Settings.ThrowSpears.Value = bool.Parse(a[2]); break;
                        case "NegativeStamina": Settings.NegativeStamina.Value = bool.Parse(a[2]); break;
                        case "CombatLog": Settings.CombatLog.Value = bool.Parse(a[2]); break;
                        case "EventLog": Settings.EventLog.Value = bool.Parse(a[2]); break;
                        case "HitboxDebug": Settings.HitboxDebug.Value = bool.Parse(a[2]); break;
                        case "ValheimIframes": Settings.ValheimIframes.Value = bool.Parse(a[2]); break;
                        case "IframeDebug": Settings.IframeDebug.Value = bool.Parse(a[2]); break;
                        case "Enabled": Settings.Enabled.Value = bool.Parse(a[2]); break;
                        case "ValheimEmptyHanded": Settings.ValheimEmptyHanded.Value = bool.Parse(a[2]); break;
                        case "GuardTowardCamera": Settings.GuardTowardCamera.Value = bool.Parse(a[2]); break;
                        case "ArmsOutOfTorso": Settings.ArmsOutOfTorso = bool.Parse(a[2]); c.ArmPeak = 0f; break;
                        default: Plugin.Report($"setting: {a[1]} unknown"); break;
                    }
                    break;
                case "target":
                {
                    // target PREFAB DISTANCE: a creature that takes hits but never dies or fights back (to count hits).
                    GameObject prefab = ZNetScene.instance.GetPrefab(a[1]);
                    Vector3 at = vp.transform.position + vp.transform.forward * (a.Length > 2 ? F(a[2]) : 2f);
                    if (prefab == null)
                    {
                        Plugin.Report($"target: {a[1]} not found");
                        break;
                    }
                    GameObject go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(-vp.transform.forward));
                    Character ch = go.GetComponent<Character>();
                    if (ch != null)
                    {
                        ch.SetMaxHealth(100000f);
                        ch.SetHealth(100000f);
                    }
                    foreach (BaseAI ai in go.GetComponents<BaseAI>())
                    {
                        ai.enabled = false;
                    }
                    Plugin.Report($"target: {a[1]} at {at:F1}");
                    break;
                }
                case "quit":
                    Plugin.Report("quit");
                    Application.Quit();
                    break;
                default:
                    Plugin.Report($"{line}: unknown command");
                    break;
            }
        }

        public static void Late()
        {
            if (_cam is Vector3 cam && Player.m_localPlayer != null && Camera.main != null)
            {
                Transform body = Player.m_localPlayer.transform;
                Camera.main.transform.position = body.TransformPoint(cam);
                Camera.main.transform.LookAt(body.position + Vector3.up * 1.1f);
            }
            if (_probe != null)
            {
                SampleProbe();
            }
        }

        private static readonly HashSet<string> TorsoBones = new HashSet<string> { "Hips", "Spine", "Spine1", "Spine2" };

        private static void SampleProbe()
        {
            Controller c = Controller.Local;
            Player vp = Player.m_localPlayer;
            if (c?.Retargeter == null || !c.Active || vp == null)
            {
                return;
            }
            // Points along the left forearm and hand: wrist, mid-hand, finger joints.
            var points = new List<(string, Vector3)>();
            Transform fore = c.Retargeter.Bones.Find(b => b.name == "LeftForeArm"), hand = c.Retargeter.Bones.Find(b => b.name == "LeftHand");
            if (fore == null || hand == null)
            {
                return;
            }
            points.Add(("forearm", Vector3.Lerp(fore.position, hand.position, 0.5f)));
            points.Add(("wrist", hand.position));
            foreach (Transform finger in hand)
            {
                points.Add((finger.name, finger.position));
                if (finger.childCount > 0)
                {
                    points.Add((finger.GetChild(0).name, finger.GetChild(0).position));
                }
            }
            _baked = _baked ?? new Mesh();
            float worst = 0f;
            string at = "";
            foreach (SkinnedMeshRenderer smr in vp.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (!smr.enabled || smr.sharedMesh == null)
                {
                    continue;
                }
                if (!TorsoVerts.TryGetValue(smr, out int[] torso))
                {
                    // Vertices that follow the spine most: the trunk, without the arms.
                    Transform[] bones = smr.bones;
                    BoneWeight[] w = smr.sharedMesh.boneWeights;
                    torso = Enumerable.Range(0, w.Length).Where(i => w[i].weight0 > 0.5f && w[i].boneIndex0 < bones.Length && bones[w[i].boneIndex0] != null
                                                                       && TorsoBones.Contains(bones[w[i].boneIndex0].name)).ToArray();
                    TorsoVerts[smr] = torso;
                }
                if (torso.Length == 0)
                {
                    continue;
                }
                smr.BakeMesh(_baked);
                Vector3[] v = _baked.vertices, n = _baked.normals;
                Matrix4x4 m = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                foreach ((string name, Vector3 p) in points)
                {
                    Vector3 local = m.inverse.MultiplyPoint3x4(p);
                    int best = -1;
                    float bestD = 0.12f * 0.12f;
                    foreach (int i in torso)
                    {
                        float d = (v[i] - local).sqrMagnitude;
                        if (d < bestD)
                        {
                            bestD = d;
                            best = i;
                        }
                    }
                    // Behind the nearest surface vertex's normal: inside.
                    float depth = best >= 0 ? -Vector3.Dot(local - v[best], n[best].normalized) : 0f;
                    if (depth > worst)
                    {
                        worst = depth;
                        // Where, in the torso oval's frame (Retarget.OutOfTorso): x side, y along the spine, z forward.
                        Transform low = c.Retargeter.Bones.Find(b => b.name == "Spine"), high = c.Retargeter.Bones.Find(b => b.name == "Neck"),
                            chest = c.Retargeter.Bones.Find(b => b.name == "Spine2");
                        Vector3 axis = high.position - low.position;
                        float t = Mathf.Clamp01(Vector3.Dot(p - low.position, axis) / axis.sqrMagnitude);
                        Vector3 o = Quaternion.Inverse(chest.rotation) * (p - low.position - axis * t);
                        at = $"{name} in {smr.name}, oval frame {o.x:0.00} {o.y:0.00} {o.z:0.00} t {t:0.00}";
                    }
                }
            }
            Eldenheim.Sim.Player s = c.Sim;
            string key = s.State == Eldenheim.Sim.StateKind.Act ? $"{Eldenheim.Sim.ErData.Action(s.Act.Id)?.Name ?? s.Act.Id.ToString()} F{(int)s.Act.F,3}" : s.State.ToString();
            if (!_probe.TryGetValue(key, out var was) || worst > was.depth)
            {
                _probe[key] = (worst, at);
            }
            if (worst > _probeWorst + 0.005f)
            {
                // The deepest frame so far, as drawn (overwritten as deeper ones come).
                _probeWorst = worst;
                ScreenCapture.CaptureScreenshot(Path.Combine(Plugin.Dir, "shots", $"probe_{_probeRun}.png"));
                Plugin.Report($"probe shot {_probeRun}: {key} {worst * 100f:0.0} cm");
            }
        }

        /// <summary>How far each driven bone turns from its bind pose: twists past ~120° are where skin wraps.</summary>
        private static void SampleDeform()
        {
            Controller c = Controller.Local;
            if (c?.Retargeter == null || !c.Active)
            {
                return;
            }
            foreach (Transform t in c.Retargeter.Bones)
            {
                float twist = TwistAngle(t);
                if (!_deform.TryGetValue(t.name, out float w) || twist > w)
                {
                    _deform[t.name] = twist;
                }
            }
        }

        /// <summary>The bone's twist about its own length (towards its first child), relative to the rest it had at load.</summary>
        private static readonly Dictionary<Transform, Quaternion> Rest = new Dictionary<Transform, Quaternion>();

        private static float TwistAngle(Transform t)
        {
            if (!Rest.TryGetValue(t, out Quaternion rest))
            {
                Rest[t] = rest = t.localRotation;
            }
            if (t.childCount == 0)
            {
                return 0f;
            }
            Vector3 axis = t.GetChild(0).localPosition.normalized;
            Quaternion delta = Quaternion.Inverse(rest) * t.localRotation;
            // Swing-twist decomposition: the twist part is the rotation about the bone axis.
            var v = new Vector3(delta.x, delta.y, delta.z);
            Vector3 p = Vector3.Project(v, axis);
            var twist = new Quaternion(p.x, p.y, p.z, delta.w);
            float n = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (n < 1e-6f)
            {
                return 0f;
            }
            float w = Mathf.Clamp(Mathf.Abs(twist.w / n), 0f, 1f);
            return 2f * Mathf.Acos(w) * Mathf.Rad2Deg;
        }
    }
}
