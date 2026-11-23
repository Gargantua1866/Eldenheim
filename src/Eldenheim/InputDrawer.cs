using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Eldenheim
{
    /// <summary>
    /// Read by the Configuration Manager (F1) by its field names: a custom
    /// drawer for a setting.
    /// </summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public System.Action<ConfigEntryBase> CustomDrawer;
        /// <summary>Higher lists first within a section (default: by name).</summary>
        public int? Order;
    }

    /// <summary>
    /// The F1 menu's input settings: the binding as text, plus "Set", which
    /// waits for the next key, mouse button or gamepad button pressed and
    /// binds it (Escape cancels). "Default" puts the default back; the text
    /// can still be typed (empty: unbound).
    /// </summary>
    internal static class InputDrawer
    {
        private static ConfigEntryBase _listening;
        private static int _startFrame, _drawnFrame;
        private static bool _escapePatched;

        /// <summary>Waiting for a key, in a menu that's still drawn.</summary>
        private static bool Listening => _listening != null && Time.frameCount - _drawnFrame <= 2;

        /// <summary>
        /// The Configuration Manager closes on Escape unless it thinks the console has it
        /// (its ConsoleHandlesEscape): while a binding waits, it does, so Escape cancels only the
        /// binding. Patched here, the first time the menu draws, as the manager is surely loaded by then.
        /// </summary>
        private static void PatchEscape()
        {
            _escapePatched = true;
            try
            {
                var original = HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("ConfigurationManager.ConfigurationManager"), "ConsoleHandlesEscape");
                if (original == null)
                {
                    Plugin.Report("patch: ConfigurationManager.ConsoleHandlesEscape not found (Escape closes the menu while setting a key)");
                    return;
                }
                new HarmonyLib.Harmony(Plugin.Guid + ".escape").Patch(original, postfix: new HarmonyLib.HarmonyMethod(typeof(InputDrawer), nameof(KeepEscape)));
            }
            catch (System.Exception ex)
            {
                Plugin.Report($"patch: ConfigurationManager.ConsoleHandlesEscape failed: {ex.Message}");
            }
        }

        private static void KeepEscape(ref bool __result) => __result |= Listening;

        /// <summary>
        /// The Configuration Manager's dropdown lists draw rich text, so the moveset
        /// list's dividers can be coloured (Settings.MovesetChoices). Its style is made
        /// again whenever the menu's look changes, so it is set each time it's fetched.
        /// </summary>
        public static void PatchDropdowns(HarmonyLib.Harmony harmony)
        {
            var original = HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("ConfigurationManager.ConfigurationManagerStyles"), "GetComboBoxStyle");
            if (original == null)
            {
                Plugin.Report("patch: ConfigurationManagerStyles.GetComboBoxStyle not found (the moveset dividers show their colour tags)");
                return;
            }
            try
            {
                harmony.Patch(original, postfix: new HarmonyLib.HarmonyMethod(typeof(InputDrawer), nameof(RichDropdown)));
            }
            catch (System.Exception ex)
            {
                Plugin.Report($"patch: ConfigurationManagerStyles.GetComboBoxStyle failed: {ex.Message}");
            }
        }

        private static void RichDropdown(GUIStyle __result)
        {
            if (__result != null)
            {
                __result.richText = true;
            }
        }

        public static ConfigurationManagerAttributes For(bool gamepad) => new ConfigurationManagerAttributes
        {
            CustomDrawer = entry => Draw(entry, gamepad),
        };

        private static void Draw(ConfigEntryBase entry, bool gamepad)
        {
            var e = (ConfigEntry<string>)entry;
            if (!_escapePatched)
            {
                PatchEscape();
            }
            if (_listening != null && Time.frameCount - _drawnFrame > 2)
            {
                // The menu was closed while a binding waited (F1): it doesn't carry over to the next key.
                _listening = null;
            }
            _drawnFrame = Time.frameCount;
            // Only as a frame's GUI starts (its layout pass), before the row is laid out: switched
            // between the layout and the repaint, the row would be drawn with other controls than
            // were laid out, and IMGUI's error leaves it stuck without its buttons.
            if (_listening == entry && Event.current.type == EventType.Layout && Time.frameCount > _startFrame && Pressed(gamepad, out string path))
            {
                _listening = null;
                if (path != null)
                {
                    e.Value = path;
                }
            }
            GUILayout.BeginHorizontal();
            if (_listening == entry)
            {
                GUILayout.Label(gamepad ? "Press a gamepad button... (Esc: cancel)" : "Press a key or mouse button... (Esc: cancel)", GUILayout.ExpandWidth(true));
            }
            else
            {
                // The buttons first: a long binding's text field would push them out of the row.
                if (GUILayout.Button("Set", GUILayout.ExpandWidth(false)))
                {
                    _listening = entry;
                    // The click that started listening is not the new binding.
                    _startFrame = Time.frameCount + 1;
                    // A text field keeping the keyboard focus would hand back its old text when it's
                    // drawn again, undoing the new binding.
                    GUIUtility.keyboardControl = 0;
                }
                if (GUILayout.Button("Default", GUILayout.ExpandWidth(false)))
                {
                    e.Value = (string)e.DefaultValue;
                    GUIUtility.keyboardControl = 0;
                }
                string typed = GUILayout.TextField(e.Value ?? "", GUILayout.ExpandWidth(true));
                if (typed != e.Value)
                {
                    e.Value = typed;
                }
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>A button pressed this frame on the wanted kind of device, as an input path (null: Escape, cancel).</summary>
        private static bool Pressed(bool gamepad, out string path)
        {
            path = null;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                return true;
            }
            if (gamepad)
            {
                foreach (Gamepad pad in Gamepad.all)
                {
                    foreach (InputControl c in pad.allControls)
                    {
                        if (c is ButtonControl b && !b.synthetic && b.wasPressedThisFrame && !(c.parent is StickControl) && !(c.parent is DpadControl && c.parent != pad.dpad))
                        {
                            // The generic layout's name for it (<Gamepad>/buttonEast), whatever the pad.
                            path = "<Gamepad>/" + c.path.Substring(pad.path.Length + 1);
                            return true;
                        }
                    }
                }
                return false;
            }
            if (keyboard != null)
            {
                foreach (KeyControl k in keyboard.allKeys)
                {
                    if (k != null && k.wasPressedThisFrame)
                    {
                        path = "<Keyboard>/" + k.name;
                        return true;
                    }
                }
            }
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                foreach (ButtonControl b in new[] { mouse.leftButton, mouse.rightButton, mouse.middleButton, mouse.forwardButton, mouse.backButton })
                {
                    if (b != null && b.wasPressedThisFrame)
                    {
                        path = "<Mouse>/" + b.name;
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
