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
        private static int _startFrame;

        public static ConfigurationManagerAttributes For(bool gamepad) => new ConfigurationManagerAttributes
        {
            CustomDrawer = entry => Draw(entry, gamepad),
        };

        private static void Draw(ConfigEntryBase entry, bool gamepad)
        {
            var e = (ConfigEntry<string>)entry;
            GUILayout.BeginHorizontal();
            if (_listening == entry)
            {
                GUILayout.Label(gamepad ? "Press a gamepad button... (Esc: cancel)" : "Press a key or mouse button... (Esc: cancel)", GUILayout.ExpandWidth(true));
                if (Time.frameCount > _startFrame && Pressed(gamepad, out string path))
                {
                    if (path != null)
                    {
                        e.Value = path;
                    }
                    _listening = null;
                }
            }
            else
            {
                // The buttons first: a long binding's text field would push them out of the row.
                if (GUILayout.Button("Set", GUILayout.ExpandWidth(false)))
                {
                    _listening = entry;
                    // The click that started listening is not the new binding.
                    _startFrame = Time.frameCount + 1;
                }
                if (GUILayout.Button("Default", GUILayout.ExpandWidth(false)))
                {
                    e.Value = (string)e.DefaultValue;
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
