using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Eldenheim.Anim;

using HarmonyLib;
using UnityEngine;

namespace Eldenheim
{
    [BepInPlugin(Guid, "Eldenheim", "0.6.17")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.gargantua.eldenheim";

        internal static ManualLogSource Log;
        internal static string Dir;
        /// <summary>Which data build this client has (0: none); sent with the pose state.</summary>
        internal static int DataHash;
        private static string _results;

        private float _nextPoll;

        private void Awake()
        {
            Log = Logger;
            Dir = Path.GetDirectoryName(Info.Location);
            _results = Path.Combine(Dir, "results.log");
            Settings.Bind(Config);
            Application.runInBackground = true;
            LoadData();
            var harmony = new Harmony(Guid);
            Patches.Apply(harmony);
            Hands.Apply(harmony);
            BalanceSync.Apply(harmony, Config);
            Report($"loaded {Info.Metadata.Version}");
        }

        private static void LoadData()
        {
            // Generated from the user's own game files; never shipped.
            string data = Path.Combine(Dir, "eldenheim_data.bin"), anims = Path.Combine(Dir, "eldenheim_anims_all.bin");
            try
            {
                if (File.Exists(data))
                {
                    Eldenheim.Sim.ErData.Load(data);
                    // Other players' poses index this build's weapons and actions: only the same build can show them.
                    uint h = 2166136261;
                    foreach (byte b in File.ReadAllBytes(data))
                    {
                        h = (h ^ b) * 16777619;
                    }
                    DataHash = (int)h ^ (File.Exists(anims) ? (int)new FileInfo(anims).Length : 0);
                }
                else
                {
                    Report("missing eldenheim_data.bin: generate it (see README); Eldenheim stays off");
                }
                if (File.Exists(anims))
                {
                    Controller.Clips = Clips.Load(anims);
                    Report($"data: {Eldenheim.Sim.ErData.Weapons?.Length} weapons, {Controller.Clips.Count} clips");
                }
                else
                {
                    Report("missing eldenheim_anims_all.bin: generate it (see README); Eldenheim stays off");
                }
            }
            catch (Exception e)
            {
                Report("data failed to load: " + e);
            }
        }

        /// <summary>Measurements made on every equip and swing: only with F1 → Debug → EventLog.</summary>
        public static void Trace(string line)
        {
            if (Settings.EventLog.Value)
            {
                Report(line);
            }
        }

        public static void Report(string line)
        {
            Log?.LogInfo(line);
            try
            {
                File.AppendAllText(_results, $"[{DateTime.Now:HH:mm:ss.fff}] {line}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        private void Update()
        {
            if (Settings.ToggleKey.Value.IsDown())
            {
                Settings.Enabled.Value = !Settings.Enabled.Value;
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, "Eldenheim " + (Settings.Enabled.Value ? "on" : "off"));
            }
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + 0.25f;
                Commands.Poll(Path.Combine(Dir, "commands.txt"));
            }
            Commands.Tick();
            Bindings.TrackModifier();
        }

        private void LateUpdate()
        {
            Commands.Late();
        }

        private GUIStyle _iframeStyle;

        private void OnGUI()
        {
            CombatLog.OnGUI();
            if (Settings.IframeDebug.Value && Controller.Local is Controller c && c.Active && c.IframeStatus() is string iframes && iframes.Length > 0)
            {
                _iframeStyle = _iframeStyle ?? new GUIStyle(GUI.skin.box) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
                GUI.Box(new Rect(Screen.width / 2f - 300f, 110f, 600f, 30f), iframes, _iframeStyle);
            }
        }
    }
}
