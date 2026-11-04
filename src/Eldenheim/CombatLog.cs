using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Eldenheim
{
    /// <summary>
    /// F1 → Debug → CombatLog: an on-screen list of recent stamina spending and
    /// damage dealt, with totals (all since it was switched on, and over the
    /// last 10 seconds).
    /// </summary>
    internal static class CombatLog
    {
        private struct Entry
        {
            public float At;
            public string What;
            public float Amount;
        }

        private const int SHOWN = 12;
        private const float WINDOW = 10f;
        private static readonly List<Entry> Stamina = new List<Entry>();
        private static readonly List<Entry> Damage = new List<Entry>();
        private static float _staminaTotal, _damageTotal;
        private static bool _was;
        private static GUIStyle _style;

        public static bool On => Settings.CombatLog != null && Settings.CombatLog.Value;

        public static void SpentStamina(string what, float amount)
        {
            if (!On || amount <= 0f)
            {
                return;
            }
            // Sprinting drains every tick: one running entry per sprint.
            if (what == "sprint" && Stamina.Count > 0 && Stamina[Stamina.Count - 1].What == "sprint" && Time.time - Stamina[Stamina.Count - 1].At < 0.25f)
            {
                Entry e = Stamina[Stamina.Count - 1];
                e.Amount += amount;
                e.At = Time.time;
                Stamina[Stamina.Count - 1] = e;
            }
            else
            {
                Stamina.Add(new Entry { At = Time.time, What = Localize(what), Amount = amount });
            }
            _staminaTotal += amount;
            Trim(Stamina);
        }

        public static void Dealt(string what, float amount)
        {
            if (!On)
            {
                return;
            }
            Damage.Add(new Entry { At = Time.time, What = Localize(what), Amount = amount });
            _damageTotal += amount;
            Trim(Damage);
        }

        private static string Localize(string s) => Localization.instance != null ? Localization.instance.Localize(s) : s;

        private static void Trim(List<Entry> list)
        {
            while (list.Count > 200)
            {
                list.RemoveAt(0);
            }
        }

        public static void OnGUI()
        {
            if (!On)
            {
                if (_was)
                {
                    Stamina.Clear();
                    Damage.Clear();
                    _staminaTotal = _damageTotal = 0f;
                    _was = false;
                }
                return;
            }
            _was = true;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, richText = true };
            }
            float now = Time.time;
            string Column(string title, List<Entry> list, float total, string unit)
            {
                float recent = list.Where(e => now - e.At <= WINDOW).Sum(e => e.Amount);
                var lines = list.Skip(System.Math.Max(0, list.Count - SHOWN)).Reverse()
                    .Select(e => $"{now - e.At,5:0.0}s ago  {e.Amount,6:0.0}  {e.What}");
                return $"<b>{title}</b>  total {total:0.0}{unit}, last {WINDOW:0}s {recent:0.0}{unit}\n" + string.Join("\n", lines);
            }
            string text = Column("Stamina used", Stamina, _staminaTotal, "") + "\n\n" +
                          Column("Damage dealt (before the target's resistances)", Damage, _damageTotal, "");
            GUI.Box(new Rect(Screen.width - 490f, 260f, 470f, 26f + 17f * (2 * SHOWN + 4)), text, _style);
        }
    }
}
