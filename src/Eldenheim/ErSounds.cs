using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Eldenheim
{
    /// <summary>
    /// The Elden Ring sounds Eldenheim plays: the guard counter's own, and
    /// the guard break's (the break clip's frame-0 event, baked with the rest).
    /// Everything else (swings, steps, rolls) is Valheim's. Reads the
    /// sandbox's baked sound table (player_sounds.bin, ERSD v2, from
    /// tools/bake_sounds.py) and its recordings (sounds/*.ogg), and plays only
    /// the recordings no other action family uses. Ported from the earlier
    /// Krom port's ErSounds.
    /// </summary>
    internal sealed class ErSounds : MonoBehaviour
    {
        private sealed class Recording
        {
            public int Media;
            public float Db, DbFrom, DbTo, Cents, CentsFrom, CentsTo;
        }

        private abstract class Tree
        {
            public abstract IEnumerable<Recording> Pick();
            public abstract IEnumerable<int> All();
        }

        private sealed class Leaf : Tree
        {
            public Recording Sound;
            public override IEnumerable<Recording> Pick() { yield return Sound; }
            public override IEnumerable<int> All() { yield return Sound.Media; }
        }

        private sealed class Choice : Tree
        {
            public Tree[] Children;
            public bool Random;

            public override IEnumerable<Recording> Pick()
            {
                if (Children.Length == 0)
                {
                    yield break;
                }
                IEnumerable<Tree> picked = Random ? new[] { Children[UnityEngine.Random.Range(0, Children.Length)] } : (IEnumerable<Tree>)Children;
                foreach (Tree child in picked)
                {
                    foreach (Recording sound in child.Pick())
                    {
                        yield return sound;
                    }
                }
            }

            public override IEnumerable<int> All()
            {
                foreach (Tree child in Children)
                {
                    foreach (int media in child.All())
                    {
                        yield return media;
                    }
                }
            }
        }

        private static Tree[] _trees;
        private static readonly Dictionary<string, List<(float At, int Tree)>> Clips = new Dictionary<string, List<(float, int)>>();
        private static readonly Dictionary<int, HashSet<string>> Families = new Dictionary<int, HashSet<string>>();
        private static readonly Dictionary<string, AudioClip> Loaded = new Dictionary<string, AudioClip>();
        private static readonly HashSet<string> Pending = new HashSet<string>();
        // a000_019500 (and the other shields' breaks) play Play_c999999985 on
        // frame 0: this one recording, at -2 dB. The other reactions don't.
        private const string GuardBreakFile = "599687853.ogg";
        private const float GuardBreakGain = 0.79f;
        // Being parried (a000_005800) plays Play_c999999991 on frame 0: the parry's ring, at 0 dB.
        private const string ParryFile = "842851583.ogg";
        private static bool _tried;

        private readonly List<(float At, int Tree)> _due = new List<(float, int)>();

        /// <summary>Plays the sounds only this clip's action family has (the guard counter's), as if the clip began <paramref name="late"/> seconds ago.</summary>
        public static void PlaySignature(Character who, string clip, float late = 0f)
        {
            if (!Settings.GuardCounterSound.Value || who == null || !Load() || !Clips.TryGetValue(clip, out var events))
            {
                return;
            }
            ErSounds s = who.GetComponent<ErSounds>() ?? who.gameObject.AddComponent<ErSounds>();
            int queued = 0;
            foreach ((float at, int tree) in events)
            {
                if (Own(tree, clip))
                {
                    s._due.Add((Time.time + at - late, tree));
                    queued++;
                    foreach (int media in _trees[tree].All())
                    {
                        s.Clip(media + ".ogg");
                    }
                }
            }
            if (Settings.EventLog.Value)
            {
                Plugin.Report($"sound: {clip}: {queued} of {events.Count} sounds are its own");
            }
        }

        /// <summary>
        /// Starts loading every recording a guard counter can play (family
        /// "700"), so the first one after loading the world isn't silent
        /// (Vorbis loads a moment after it is asked for).
        /// </summary>
        public static void Preload(Character who)
        {
            if (who == null)
            {
                return;
            }
            ErSounds s = who.GetComponent<ErSounds>() ?? who.gameObject.AddComponent<ErSounds>();
            s.Clip(GuardBreakFile);
            s.Clip(ParryFile);
            if (!Settings.GuardCounterSound.Value || !Load())
            {
                return;
            }
            foreach (var entry in Clips)
            {
                if (!entry.Key.EndsWith("700"))
                {
                    continue;
                }
                foreach ((float _, int tree) in entry.Value)
                {
                    if (Own(tree, entry.Key))
                    {
                        foreach (int media in _trees[tree].All())
                        {
                            s.Clip(media + ".ogg");
                        }
                    }
                }
            }
        }

        /// <summary>Elden Ring's guard-break sound, at GuardBreakVolumePercent.</summary>
        public static void PlayGuardBreak(Character who)
        {
            if (who == null || Settings.GuardBreakVolume.Value <= 0)
            {
                return;
            }
            ErSounds s = who.GetComponent<ErSounds>() ?? who.gameObject.AddComponent<ErSounds>();
            AudioClip clip = s.Clip(GuardBreakFile);
            if (clip != null)
            {
                s.Emit(clip, Settings.GuardBreakVolume.Value / 100f * GuardBreakGain, 1f);
            }
        }

        /// <summary>Elden Ring's parry sound, in place of Valheim's (ParrySound).</summary>
        public static void PlayParry(Character who)
        {
            if (who == null)
            {
                return;
            }
            ErSounds s = who.GetComponent<ErSounds>() ?? who.gameObject.AddComponent<ErSounds>();
            AudioClip clip = s.Clip(ParryFile);
            if (clip != null)
            {
                s.Emit(clip, Settings.ParryVolume.Value / 100f, 1f);
            }
        }

        private void Update()
        {
            for (int i = _due.Count - 1; i >= 0; i--)
            {
                if (_due[i].At > Time.time)
                {
                    continue;
                }
                int tree = _due[i].Tree;
                _due.RemoveAt(i);
                foreach (Recording sound in _trees[tree].Pick())
                {
                    Play(sound);
                }
            }
        }

        /// <summary>Whether every recording the sound can play belongs to this clip's family alone (the id's last three digits).</summary>
        private static bool Own(int tree, string clip)
        {
            if (tree < 0 || tree >= _trees.Length)
            {
                return false;
            }
            string family = clip.Substring(clip.Length - 3);
            foreach (int media in _trees[tree].All())
            {
                if (!Families.TryGetValue(media, out HashSet<string> used) || used.Count != 1 || !used.Contains(family))
                {
                    return false;
                }
            }
            return true;
        }

        private void Play(Recording sound)
        {
            AudioClip clip = Clip(sound.Media + ".ogg");
            if (clip != null)
            {
                Emit(clip, Mathf.Pow(10f, Level(sound.Db, sound.DbFrom, sound.DbTo) / 20f) * Settings.GuardCounterVolume.Value / 100f,
                    Mathf.Pow(2f, Level(sound.Cents, sound.CentsFrom, sound.CentsTo) / 1200f));
            }
        }

        private void Emit(AudioClip clip, float volume, float pitch)
        {
            var host = new GameObject("eldenheim-sound");
            host.transform.position = transform.position + Vector3.up * 1.2f;
            AudioSource source = host.AddComponent<AudioSource>();
            source.clip = clip;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 1f;
            source.maxDistance = 40f;
            // ER's recordings carry their own room; Valheim's reverb zones on top made them echo.
            source.bypassReverbZones = true;
            source.reverbZoneMix = 0f;
            source.outputAudioMixerGroup = SfxGroup();
            source.volume = Mathf.Clamp01(volume);
            source.pitch = pitch;
            source.Play();
            Destroy(host, clip.length / Mathf.Max(0.05f, source.pitch) + 0.2f);
        }

        private static UnityEngine.Audio.AudioMixerGroup _sfx;

        /// <summary>Valheim's sound-effects mixer group, so the game's SFX volume applies.</summary>
        private static UnityEngine.Audio.AudioMixerGroup SfxGroup()
        {
            if (_sfx == null && AudioMan.instance != null && AudioMan.instance.m_masterMixer != null)
            {
                var groups = AudioMan.instance.m_masterMixer.FindMatchingGroups("SFX");
                _sfx = groups.Length > 0 ? groups[0] : null;
            }
            return _sfx;
        }

        private static float Level(float value, float from, float to) => from == to ? value : value + Random.Range(from, to);

        /// <summary>The recording, or null while it loads (Unity decodes Vorbis only through a web request).</summary>
        private AudioClip Clip(string file)
        {
            if (Loaded.TryGetValue(file, out AudioClip c))
            {
                return c;
            }
            string path = Path.Combine(Plugin.Dir, "sounds", file);
            if (!File.Exists(path))
            {
                // Gale flattens a package's subfolders: the recordings then sit beside the DLL.
                path = Path.Combine(Plugin.Dir, file);
            }
            if (Pending.Add(file) && File.Exists(path))
            {
                StartCoroutine(Fetch(file, path));
            }
            return null;
        }

        private static IEnumerator Fetch(string file, string path)
        {
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new System.Uri(path).AbsoluteUri, AudioType.OGGVORBIS))
            {
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        Loaded[file] = clip;
                    }
                }
                else
                {
                    Plugin.Report($"sound: could not read {path}: {request.error}");
                }
            }
        }

        private static bool Load()
        {
            if (_trees != null)
            {
                return true;
            }
            if (_tried)
            {
                return false;
            }
            _tried = true;
            string path = Path.Combine(Plugin.Dir, "player_sounds.bin");
            if (!File.Exists(path))
            {
                Plugin.Report("sound: no player_sounds.bin (see README); the guard counter plays Valheim's swing only");
                return false;
            }
            try
            {
                using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                {
                    if (new string(r.ReadChars(4)) != "ERSD" || r.ReadUInt32() != 2)
                    {
                        throw new InvalidDataException("not an ERSD v2 file");
                    }
                    var trees = new Tree[r.ReadUInt32()];
                    for (int i = 0; i < trees.Length; i++)
                    {
                        trees[i] = ReadTree(r);
                    }
                    uint clips = r.ReadUInt32();
                    for (int i = 0; i < clips; i++)
                    {
                        string name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
                        int n = r.ReadUInt16();
                        var list = new List<(float, int)>(n);
                        for (int e = 0; e < n; e++)
                        {
                            list.Add((r.ReadSingle() / 30f, (int)r.ReadUInt32()));
                        }
                        Clips[name] = list;
                    }
                    _trees = trees;
                }
                foreach (var entry in Clips)
                {
                    string family = entry.Key.Substring(entry.Key.Length - 3);
                    foreach ((float _, int tree) in entry.Value)
                    {
                        if (tree < 0 || tree >= _trees.Length)
                        {
                            continue;
                        }
                        foreach (int media in _trees[tree].All())
                        {
                            if (!Families.TryGetValue(media, out HashSet<string> used))
                            {
                                Families[media] = used = new HashSet<string>();
                            }
                            used.Add(family);
                        }
                    }
                }
                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Report($"sound: could not read {path}: {e.Message}");
                return false;
            }
        }

        private static Tree ReadTree(BinaryReader r)
        {
            byte kind = r.ReadByte();
            if (kind == 0)
            {
                return new Leaf
                {
                    Sound = new Recording
                    {
                        Media = (int)r.ReadUInt32(),
                        Db = r.ReadSingle(), DbFrom = r.ReadSingle(), DbTo = r.ReadSingle(),
                        Cents = r.ReadSingle(), CentsFrom = r.ReadSingle(), CentsTo = r.ReadSingle(),
                    },
                };
            }
            var kids = new Tree[r.ReadUInt16()];
            for (int i = 0; i < kids.Length; i++)
            {
                kids[i] = ReadTree(r);
            }
            return new Choice { Children = kids, Random = kind == 1 };
        }
    }
}
