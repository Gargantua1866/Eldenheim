using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;

namespace Eldenheim
{
    /// <summary>
    /// The F1 sections marked (server) are the server's (PLAN §3.7): it changes how each
    /// player's own sim and hits work. The server (or the host) sends its values
    /// to each client as it joins and again when the admin changes one. A
    /// client uses them for the session without saving its config file
    /// meanwhile (a save writes every current value, the server's too), and
    /// gets its own values back when it leaves.
    /// </summary>
    internal static class BalanceSync
    {
        private const string RPC = "Eldenheim_Balance";
        private static ConfigFile _config;
        // The client's own values while the server's are in use (null: not in use).
        private static Dictionary<ConfigEntryBase, string> _own;
        private static bool _savedBefore, _applying;

        private static IEnumerable<ConfigEntryBase> Entries() =>
            _config.Keys.Where(k => Server(k.Section)).Select(k => _config[k]).OrderBy(e => e.Definition.Key);

        /// <summary>The sections the server decides (Settings.COMBAT, Settings.STAMINA).</summary>
        private static bool Server(string section) => section.EndsWith("(server)");

        public static void Apply(Harmony h, ConfigFile config)
        {
            _config = config;
            _config.SettingChanged += (_, args) =>
            {
                if (Server(args.ChangedSetting.Definition.Section))
                {
                    Changed();
                }
            };
            h.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(BalanceSync), nameof(Connected)));
            h.Patch(AccessTools.Method(typeof(ZNet), "RPC_PeerInfo"), postfix: new HarmonyMethod(typeof(BalanceSync), nameof(PeerInfo)));
            h.Patch(AccessTools.Method(typeof(ZNet), "OnDestroy"), postfix: new HarmonyMethod(typeof(BalanceSync), nameof(Left)));
        }

        private static ZPackage Values()
        {
            var pkg = new ZPackage();
            List<ConfigEntryBase> entries = Entries().ToList();
            pkg.Write(entries.Count);
            foreach (ConfigEntryBase e in entries)
            {
                pkg.Write(e.Definition.Key);
                pkg.Write(e.GetSerializedValue());
            }
            return pkg;
        }

        /// <summary>The server's admin changed a value: every client gets the new set.</summary>
        private static void Changed()
        {
            if (_applying || ZNet.instance == null || !ZNet.instance.IsServer())
            {
                return;
            }
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                peer.m_rpc.Invoke(RPC, Values());
            }
        }

        private static void Connected(ZNet __instance, ZNetPeer peer)
        {
            if (!__instance.IsServer())
            {
                peer.m_rpc.Register<ZPackage>(RPC, Received);
            }
        }

        // After the client's peer info: its handler is registered by then.
        private static void PeerInfo(ZNet __instance, ZRpc rpc)
        {
            if (__instance.IsServer() && rpc.IsConnected())
            {
                rpc.Invoke(RPC, Values());
            }
        }

        private static void Received(ZRpc rpc, ZPackage pkg)
        {
            var byKey = Entries().ToDictionary(e => e.Definition.Key);
            if (_own == null)
            {
                _own = byKey.Values.ToDictionary(e => e, e => e.GetSerializedValue());
                _savedBefore = _config.SaveOnConfigSet;
                _config.SaveOnConfigSet = false;
            }
            _applying = true;
            try
            {
                int n = pkg.ReadInt();
                for (int i = 0; i < n; i++)
                {
                    string key = pkg.ReadString(), value = pkg.ReadString();
                    // A key this version doesn't have (another plugin version) is skipped.
                    if (byKey.TryGetValue(key, out ConfigEntryBase e))
                    {
                        e.SetSerializedValue(value);
                    }
                }
            }
            finally
            {
                _applying = false;
            }
            Plugin.Report("balance: using the server's settings");
        }

        /// <summary>Leaving the world: the client's own values again, and anything else changed meanwhile saved.</summary>
        private static void Left()
        {
            if (_own == null)
            {
                return;
            }
            _applying = true;
            try
            {
                foreach (var kv in _own)
                {
                    kv.Key.SetSerializedValue(kv.Value);
                }
            }
            finally
            {
                _applying = false;
            }
            _own = null;
            _config.SaveOnConfigSet = _savedBefore;
            if (_savedBefore)
            {
                _config.Save();
            }
        }
    }
}
