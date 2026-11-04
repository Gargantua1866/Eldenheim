using System.Collections.Generic;
using Eldenheim.Sim;
using UnityEngine;
using Input = Eldenheim.Sim.Input;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace Eldenheim
{
    /// <summary>
    /// The one place keys are read. Builds the sim's Input from the bindings
    /// in the config, gathering presses between ticks so none is lost.
    /// </summary>
    public class Bindings
    {
        /// <summary>Scripted input for automated tests: overrides the devices while set.</summary>
        public static readonly Dictionary<string, bool> Forced = new Dictionary<string, bool>();
        public static Vector2? ForcedStick;

        private readonly Dictionary<string, InputControl> _controls = new Dictionary<string, InputControl>();
        private readonly Dictionary<string, bool> _was = new Dictionary<string, bool>();
        private Input _pending;
        private bool _walk;
        // Gamepad buttons still down from a menu (the one that closed it, unpaused,
        // picked an item): ignored until let go, so they don't act once the game
        // has them. Keys are left alone: a held sprint carries on past the inventory.
        private readonly HashSet<string> _blocked = new HashSet<string>();
        // Keys held as Eldenheim takes over (a roll out of a cart: Valheim's block + jump): ignored until let go.
        private readonly HashSet<string> _heldOver = new HashSet<string>();
        private bool _hadInput, _hadMenu, _hadPadInventory;
        // Gamepad with the inventory open: only these actions (DodgeInInventory).
        private bool _padInventory;
        private static readonly string[] Actions =
            { "Dodge", "Sprint", "Jump", "LightAttack", "HeavyAttack", "Guard", "Crouch", "Walk", "LockOn", "TwoHandModifier" };
        private bool _sprintHeld;

        private static readonly Bindings Probe = new Bindings();

        private static float _padModifierSince = -1f;

        /// <summary>
        /// Whether the two-hand modifier is held right now (equipping into the
        /// left hand). A gamepad's counts once held 0.15 s: in the inventory its
        /// Use button is also the tap that equips, which goes to the right hand.
        /// </summary>
        public static bool ModifierHeld()
        {
            if (Forced.TryGetValue("TwoHandModifier", out bool forced))
            {
                return forced;
            }
            if (Probe.Read(Settings.Keyboard["TwoHandModifier"].Value))
            {
                return true;
            }
            return _padModifierSince >= 0f && Time.unscaledTime - _padModifierSince >= 0.15f;
        }

        /// <summary>Every frame (Plugin.Update): since when the gamepad's modifier has been held.</summary>
        public static void TrackModifier()
        {
            bool held = Probe.Read(Settings.Gamepad["TwoHandModifier"].Value);
            _padModifierSince = !held ? -1f : _padModifierSince < 0f ? Time.unscaledTime : _padModifierSince;
        }

        private bool Down(string action)
        {
            if (Forced.TryGetValue(action, out bool forced))
            {
                return forced;
            }
            if (_padInventory && !(Settings.DodgeInInventory.Value && (action == "Dodge" || action == "Sprint")))
            {
                // The inventory has the gamepad's buttons (A picks, X splits, Y closes).
                return false;
            }
            if (!Read(Settings.Keyboard[action].Value))
            {
                _heldOver.Remove(action);
            }
            else if (!_heldOver.Contains(action))
            {
                return true;
            }
            bool pad = Read(Settings.Gamepad[action].Value);
            if (!pad)
            {
                _blocked.Remove(action);
            }
            return pad && !_blocked.Contains(action);
        }

        private bool Read(string binding)
        {
            if (string.IsNullOrEmpty(binding))
            {
                return false;
            }
            if (binding.StartsWith("Valheim:"))
            {
                return ZInput.GetButton(binding.Substring(8));
            }
            if (!_controls.TryGetValue(binding, out InputControl control) || control == null || !control.device.added)
            {
                control = InputSystem.FindControl(binding);
                _controls[binding] = control;
            }
            return control is ButtonControl b && b.isPressed;
        }

        /// <summary>Eldenheim takes the player over: what is held now was meant for Valheim.</summary>
        public void IgnoreHeld()
        {
            foreach (string action in Actions)
            {
                if (Read(Settings.Keyboard[action].Value))
                {
                    _heldOver.Add(action);
                }
                if (Read(Settings.Gamepad[action].Value))
                {
                    _blocked.Add(action);
                }
            }
        }

        private static void Feed(ref Button b, bool down, bool pressed, bool released)
        {
            b.Held = down;
            b.Pressed |= pressed;
            b.Released |= released;
        }

        private bool Edge(string action, out bool pressed, out bool released)
        {
            bool down = Down(action);
            _was.TryGetValue(action, out bool was);
            _was[action] = down;
            pressed = down && !was;
            released = !down && was;
            return down;
        }

        /// <summary>
        /// Call every frame; presses are kept until the next tick takes them.
        /// With a menu open (inventory, map...) the player can still move,
        /// dodge, sprint and jump, as in Valheim, but not attack or guard.
        /// </summary>
        public void Gather(bool takeInput, bool menuOpen, float camYawUnityDeg, bool padInventory = false)
        {
            // Whenever control comes back from a menu, or the inventory's gamepad
            // mode starts or ends, what is held now belongs to the menu.
            if (takeInput && (!_hadInput || _hadMenu && !menuOpen || padInventory != _hadPadInventory))
            {
                _padInventory = false;
                foreach (string action in Actions)
                {
                    if (Read(Settings.Gamepad[action].Value))
                    {
                        _blocked.Add(action);
                    }
                }
            }
            _hadInput = takeInput;
            _hadMenu = menuOpen;
            _hadPadInventory = padInventory;
            _padInventory = padInventory;
            if (!takeInput)
            {
                foreach (string k in new List<string>(_was.Keys))
                {
                    _was[k] = false;
                }
                _pending.Mv = default;
                _pending.Dodge.Held = _pending.Jump.Held = _pending.Light.Held = _pending.Heavy.Held = _pending.Guard.Held = false;
                return;
            }
            Vector2 stick = ForcedStick ?? (padInventory && !Settings.LeftJoystickInventory.Value ? Vector2.zero : ReadStick());
            _pending.Mv = new Vec2(stick.x, stick.y);
            _pending.CamYaw = Space.YawToSim(camYawUnityDeg);

            bool modifier = Edge("TwoHandModifier", out _, out _);
            bool dodgeDown = Edge("Dodge", out bool dodgePressed, out bool dodgeReleased);
            bool separate = Settings.SeparateSprint.Value;
            bool press = Settings.Dodge.Value == DodgeTrigger.Press;
            if (separate || press)
            {
                // The sim decides roll or sprint on the dodge button alone. A
                // separate sprint is fed to it as a long hold; a dodge on press
                // as an instant tap (down and up in the same tick).
                bool sprint = separate && Edge("Sprint", out _, out _);
                if (press ? dodgePressed : dodgeReleased)
                {
                    _pending.Dodge.Pressed = true;
                    _pending.Dodge.Released = true;
                }
                if (sprint != _sprintHeld)
                {
                    if (sprint)
                    {
                        _pending.Dodge.Pressed = true;
                    }
                    else
                    {
                        _pending.Dodge.Released = true;
                    }
                    _sprintHeld = sprint;
                }
                _pending.Dodge.Held = _sprintHeld;
            }
            else
            {
                _pending.Dodge.Held = dodgeDown;
                _pending.Dodge.Pressed |= dodgePressed;
                _pending.Dodge.Released |= dodgeReleased;
            }

            bool jump = Edge("Jump", out bool jp, out bool jr);
            Feed(ref _pending.Jump, jump, jp, jr);
            bool light = Edge("LightAttack", out bool lp, out bool lr);
            bool heavy = Edge("HeavyAttack", out bool hp, out bool hr);
            bool guard = Edge("Guard", out bool gp, out bool gr);
            if (menuOpen && Forced.Count == 0)
            {
                // A click in the inventory is not a swing.
                light = heavy = guard = lp = hp = gp = false;
                modifier = false;
            }
            if (modifier)
            {
                // Holding the modifier turns the attack buttons into two-handing.
                _pending.TwoHandRight |= lp;
                _pending.TwoHandLeft |= gp;
                lp = gp = false;
                light = guard = false;
            }
            Feed(ref _pending.Light, light, lp, lr);
            Feed(ref _pending.Heavy, heavy, hp, hr);
            Feed(ref _pending.Guard, guard, gp, gr);
            Edge("Crouch", out bool cp, out _);
            _pending.Crouch |= cp && !menuOpen;
            Edge("Walk", out bool wp, out _);
            if (wp)
            {
                _walk = !_walk;
            }
            _pending.Walk = _walk;
            Edge("LockOn", out bool lockPressed, out _);
            _pending.Lock |= lockPressed;
        }

        private static Vector2 ReadStick()
        {
            float x = 0f, y = 0f;
            if (ZInput.GetButton("Forward")) y += 1f;
            if (ZInput.GetButton("Backward")) y -= 1f;
            if (ZInput.GetButton("Right")) x += 1f;
            if (ZInput.GetButton("Left")) x -= 1f;
            var v = new Vector2(x, y);
            if (v.sqrMagnitude > 1f)
            {
                v.Normalize();
            }
            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                Vector2 s = pad.leftStick.ReadValue();
                if (s.sqrMagnitude > v.sqrMagnitude)
                {
                    v = s;
                }
            }
            return v;
        }

        /// <summary>The input for one tick; one-shot flags are cleared once taken.</summary>
        public Input Take()
        {
            Input i = _pending;
            _pending.Dodge.Pressed = _pending.Dodge.Released = false;
            _pending.Jump.Pressed = _pending.Jump.Released = false;
            _pending.Light.Pressed = _pending.Light.Released = false;
            _pending.Heavy.Pressed = _pending.Heavy.Released = false;
            _pending.Guard.Pressed = _pending.Guard.Released = false;
            _pending.Crouch = _pending.Lock = _pending.TwoHandRight = _pending.TwoHandLeft = false;
            _pending.NextWeapon = _pending.NextLeft = false;
            return i;
        }
    }
}
