using BepInEx.Configuration;

namespace Eldenheim
{
    public enum DodgeTrigger { Release, Press }

    /// <summary>Everything in the F1 menu (BepInEx Configuration Manager).</summary>
    public static class Settings
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> ToggleKey;

        public static ConfigEntry<bool> SeparateSprint;
        public static ConfigEntry<DodgeTrigger> Dodge;

        public static readonly System.Collections.Generic.Dictionary<string, ConfigEntry<string>> Keyboard =
            new System.Collections.Generic.Dictionary<string, ConfigEntry<string>>();
        public static readonly System.Collections.Generic.Dictionary<string, ConfigEntry<string>> Gamepad =
            new System.Collections.Generic.Dictionary<string, ConfigEntry<string>>();

        public static ConfigEntry<bool> EmptyHandGuard;
        public static ConfigEntry<bool> ValheimEmptyHanded;
        public static ConfigEntry<bool> GuardTowardCamera;
        public static ConfigEntry<int> FreeAimVertical;
        public static ConfigEntry<bool> InstantJump;
        public static ConfigEntry<bool> LeftJoystickInventory;
        public static ConfigEntry<bool> DodgeInInventory;
        public static ConfigEntry<int> LockSwitchFlick;
        public static ConfigEntry<int> LockOnRange;
        public static ConfigEntry<bool> NoLockOnRecentre;
        public static ConfigEntry<bool> CameraAutoRotation;
        public static ConfigEntry<bool> AutoLockOn;
        public static ConfigEntry<bool> AutoTarget;
        public static ConfigEntry<bool> LockOnDot;
        public static ConfigEntry<float> DamageScale;
        public static ConfigEntry<float> PoiseScale;
        public static ConfigEntry<bool> SwingTrails;
        public static ConfigEntry<bool> HitStop;
        public static ConfigEntry<int> KnockdownPercent;
        public static ConfigEntry<bool> GearPenalties;
        public static ConfigEntry<int> GuardBlowPercent;

        public static ConfigEntry<bool> ValheimIframes;
        public static ConfigEntry<bool> RemoveBlockForce;
        public static ConfigEntry<float> WeaponKnockbackScale;
        public static ConfigEntry<bool> ParrySound;
        public static ConfigEntry<int> ParryVolume;
        public static ConfigEntry<bool> IframeDebug;
        public static ConfigEntry<bool> GuardCounterSound;
        public static ConfigEntry<int> GuardCounterVolume;
        public static ConfigEntry<int> GuardBreakVolume;

        public static ConfigEntry<bool> CombatLog;
        public static ConfigEntry<bool> EventLog;
        public static ConfigEntry<bool> HitboxDebug;
        /// <summary>The Elden Ring class each Valheim weapon type plays, by setting key (see <see cref="Movesets"/>).</summary>
        public static readonly System.Collections.Generic.Dictionary<string, ConfigEntry<string>> Moveset =
            new System.Collections.Generic.Dictionary<string, ConfigEntry<string>>();
        public static ConfigEntry<bool> ThrowSpears;
        public static ConfigEntry<bool> NegativeStamina;
        public static ConfigEntry<int> StaminaRegenRate;
        public static ConfigEntry<bool> SneakStamina;
        public static ConfigEntry<int> SprintLean;

        // Action, keyboard default, gamepad default, description.
        private static readonly string[][] Actions =
        {
            new[] { "Dodge", "Valheim:Run", "<Gamepad>/buttonEast", "Roll or backstep (and, with SeparateSprint off, hold to sprint)." },
            new[] { "Sprint", "<Keyboard>/leftShift", "<Gamepad>/leftStickPress", "Hold to sprint. Only used when SeparateSprint is on." },
            new[] { "Jump", "Valheim:Jump", "<Gamepad>/buttonSouth", "Jump." },
            new[] { "LightAttack", "Valheim:Attack", "<Gamepad>/rightShoulder", "Light attack." },
            new[] { "HeavyAttack", "Valheim:SecondaryAttack", "<Gamepad>/rightTrigger", "Heavy attack; hold to charge." },
            new[] { "Guard", "Valheim:Block", "<Gamepad>/leftShoulder", "Guard, or the left-hand attack." },
            new[] { "Crouch", "Valheim:Crouch", "<Gamepad>/leftStickPress", "Crouch (toggle)." },
            new[] { "Walk", "Valheim:ToggleWalk", "", "Walk (toggle)." },
            new[] { "LockOn", "<Keyboard>/capsLock", "<Gamepad>/rightStickPress", "Lock on; with nothing to lock onto, recentre the camera (unless NoLockOnRecentre)." },
            new[] { "TwoHandModifier", "Valheim:Use", "Valheim:JoyUse", "Hold, then attack (right) or guard (left) to two-hand; held while equipping, the item goes to the left hand. " +
                                                                       "On a gamepad it counts for equipping once held a moment, so the Use tap that equips in the inventory goes to the right hand." },
        };

        /// <summary>
        /// The weapon classes a Valheim weapon can play, by their menu names
        /// (see <see cref="Shown"/> for those the sandbox calls otherwise).
        /// </summary>
        public static readonly string[] ErClasses =
        {
            // In the order Elden Ring lists weapon types; a class's own styles after it.
            "Dagger", "Erdsteel Dagger",
            "Smithscript Dagger",
            "Longsword", "Warhawk's Talon", "Carian Knight's Sword",
            "Milady",
            "Claymore", "Knight's Greatsword",
            "Colossal Sword",
            "Rapier",
            "Heavy Thrusting Sword", "Great Épée",
            "Curved Sword", "Shamshir",
            "Curved Greatsword", "Zamor Curved Sword",
            "Uchigatana", "Serpentbone Blade", "Meteoric Ore Blade",
            "Great Katana", "Rakshasa's Great Katana",
            "Twinblade", "Godskin Peeler",
            "Battle Axe", "Hand Axe", "Warped Axe", "Smithscript Axe",
            "Greataxe", "Crescent Moon Axe",
            "Club", "Morning Star", "Stone Club",
            "Flail",
            "Great Hammer", "Smithscript Hammer",
            "Colossal Weapon",
            "Short Spear", "Smithscript Spear", "Cross-Naginata",
            "Great Spear",
            "Halberd", "Banished Knight's Halberd", "Guardian's Swordspear",
            "Reaper",
            "Whip",
            "Fist",
            "Dryleaf Arts", "Dane's Footwork",
            "Claw", "Raptor Talons",
            "Beast Claw",
            "Torch",
        };

        // Setting key, default class, what it covers and the notable choices. Listed in this order.
        private static readonly string[][] Movesets =
        {
            new[] { "BareHands", "Fist", "Nothing in the hands. Fist: Elden Ring's fists (the Caestus's moveset). Dryleaf Arts, Dane's Footwork: the hand-to-hand arts, " +
                                        "punching and kicking (held in both hands, as knuckles are; Dane's Footwork kicks more)." },
            new[] { "OneHandedSwords", "Longsword", "Valheim's one-handed swords. Longsword: Elden Ring's straight swords. Shamshir: its curved sword's style (sweeping slashes). " +
                                                    "Warhawk's Talon, Carian Knight's Sword: straight swords of their own styles." },
            new[] { "TwoHandedSwords", "Claymore", "Valheim's two-handed swords (the Krom...). Claymore: Elden Ring's greatsword (style 125). " +
                                                   "Knight's Greatsword: that family and the Inseparable Sword (style 128). Milady: a light greatsword. " +
                                                   "Zamor Curved Sword: a curved greatsword of its own style." },
            new[] { "Knives", "Dagger", "Valheim's knives, Skoll and Hati included (a pair plays the class's power stance). " +
                                        "Smithscript Dagger: every attack throws the knife, which reforms in the hand." },
            new[] { "OneHandedClubs", "Morning Star", "Valheim's one-handed clubs and maces. Morning Star: the hammer category's own heavies. " +
                                                       "Club: the earlier moveset (the Mace's style). Stone Club: its own style." },
            new[] { "TwoHandedClubs", "Great Hammer", "Valheim's sledges. Smithscript Hammer: its heavies spin and throw the sledge, which slams " +
                                                      "(its area blow) where it lands and reforms in the hand." },
            new[] { "OneHandedAxes", "Hand Axe", "Valheim's one-handed axes, and the dual axes (as a power stance). Hand Axe: its style (a170). " +
                                                  "Battle Axe: the earlier moveset (the axe category's own). Warped Axe: its own style. " +
                                                  "Smithscript Axe: its heavies throw the axe, which reforms in the hand." },
            new[] { "TwoHandedAxes", "Greataxe", "Valheim's battleaxes." },
            new[] { "Polearms", "Halberd", "Valheim's atgeirs. Halberd: the category's own moveset. Banished Knight's Halberd: its style (a210, swung like a great spear). Guardian's Swordspear: its own style." },
            new[] { "Spears", "Smithscript Spear", "Valheim's spears. Smithscript Spear: its heavy attacks throw the spear, which reforms in the hand " +
                                                   "(with ThrowSpears it flies off for good); any other class swings it. Cross-Naginata: a spear of its own style." },
            new[] { "Knuckles", "Claw", "Valheim's knuckles and claws, held two-handed. Claw: the claw category's moveset (the Hookclaws'). Raptor Talons: its style (a227). " +
                                        "Beast Claw: Shadow of the Erdtree's beast claws. Fist: the earlier moveset (the Caestus)." },
        };

        /// <summary>Menu names that differ from the sandbox class they play.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> Shown =
            new System.Collections.Generic.Dictionary<string, string> { { "Knight's Greatsword", "Inseparable Sword" }, { "Colossal Sword", "Greatsword" }, { "Great Épée", "Great Epee" } };

        /// <summary>Earlier saved values, and what they are called now.</summary>
        private static readonly System.Collections.Generic.Dictionary<string, string> Renamed = new System.Collections.Generic.Dictionary<string, string>
        {
            { "InseparableSword", "Knight's Greatsword" }, { "Inseparable Sword", "Knight's Greatsword" }, { "Greatsword", "Colossal Sword" },
        };

        /// <summary>The ER class chosen for a Valheim weapon type (a Movesets key).</summary>
        public static string ClassFor(string key) => Moveset.TryGetValue(key, out ConfigEntry<string> e) ? (Shown.TryGetValue(e.Value, out string data) ? data : e.Value) : null;

        /// <summary>Bumped whenever a moveset choice changes, so the held weapon is mapped again.</summary>
        public static int MovesetVersion;

        /// <summary>Test switch (the setting command): elbows and wrists kept out of the torso.</summary>
        public static bool ArmsOutOfTorso = true;

        // The F1 menu's sections, numbered so they list in this order. Those
        // marked (server) follow the server's values in multiplayer (BalanceSync).
        public const string GENERAL = "1 - General", CONTROLS = "2 - Controls", KEYBOARD = "3 - Keyboard & Mouse", GAMEPAD = "4 - Gamepad",
            MOVESETS = "5 - Movesets", COMBAT = "6 - Combat (server)", STAMINA = "7 - Stamina & Movement (server)", VISUALS = "8 - Visuals",
            SOUND = "9 - Sound", DEBUG = "Debug";

        private static System.Collections.Generic.Dictionary<ConfigDefinition, string> _orphans;

        /// <summary>c.Bind, keeping a value saved under the setting's earlier section.</summary>
        private static ConfigEntry<T> Bind<T>(ConfigFile c, string section, string key, T value, ConfigDescription description, params string[] was)
        {
            foreach (string old in was)
            {
                if (_orphans != null && _orphans.TryGetValue(new ConfigDefinition(old, key), out string saved))
                {
                    _orphans[new ConfigDefinition(section, key)] = saved;
                    _orphans.Remove(new ConfigDefinition(old, key));
                }
            }
            return c.Bind(section, key, value, description);
        }

        private static ConfigEntry<T> Bind<T>(ConfigFile c, string section, string key, T value, string description, params string[] was) =>
            Bind(c, section, key, value, new ConfigDescription(description), was);

        public static void Bind(ConfigFile c)
        {
            _orphans = HarmonyLib.Traverse.Create(c).Property("OrphanedEntries").GetValue<System.Collections.Generic.Dictionary<ConfigDefinition, string>>();
            // Where each setting was before the sections were numbered.
            const string behaviour = "Input - Behaviour", balance = "Balance", visuals = "Visuals", sound = "Sound", weapons = "Weapons";
            // Settings that are gone (renamed with no carry-over, or replaced): out of the file.
            foreach (ConfigDefinition gone in new[] { new ConfigDefinition(balance, "KnockdownShare"), new ConfigDefinition(balance, "GuardBlowAt"), new ConfigDefinition(CONTROLS, "Auto-Target") })
            {
                _orphans?.Remove(gone);
            }

            Enabled = Bind(c, GENERAL, "Enabled", true, "Master switch. Off, Valheim's own combat is untouched.", "General");
            ToggleKey = Bind(c, GENERAL, "ToggleKey", new BepInEx.Configuration.KeyboardShortcut(), "Optional key that flips Enabled. Unbound by default.", "General");

            // Listed in this order (F1 sorts by name otherwise).
            int order = 100;
            ConfigDescription Controls(string text, AcceptableValueBase range = null) =>
                new ConfigDescription(text, range, new ConfigurationManagerAttributes { Order = order-- });
            SeparateSprint = Bind(c, CONTROLS, "SeparateSprint", false, Controls(
                "Off: one button, tap to dodge and hold to sprint (Elden Ring). On: Dodge only dodges and Sprint sprints."), behaviour);
            Dodge = Bind(c, CONTROLS, "DodgeTrigger", DodgeTrigger.Release, Controls(
                "Release (Elden Ring): the dodge comes out when the button is let go. Press: as soon as it goes down. " +
                "With SeparateSprint off, Press leaves no way to sprint on that button."), behaviour);
            EmptyHandGuard = Bind(c, CONTROLS, "EmptyHandGuard", false, Controls(
                "Off (Elden Ring): with nothing in the left hand, the guard button throws a left punch. " +
                "On (Valheim-like): it guards with the right-hand weapon, held in both hands."), behaviour);
            ValheimEmptyHanded = Bind(c, CONTROLS, "ValheimEmptyHanded", false, Controls(
                "On: with nothing in either hand, Valheim's own movement, jumping, physics and animations take over (as with a tool or a bow). " +
                "Off: bare hands are Elden Ring's fists (the BareHands moveset)."));
            GuardTowardCamera = Bind(c, CONTROLS, "GuardTowardCamera", false, Controls(
                "Off (Elden Ring): guarding unlocked, the player turns the way it moves. On (Valheim-like): while guarding it faces the " +
                "crosshair (the camera's way) and moves sideways and back as when locked on. Also for EmptyHandGuard's guard."));
            FreeAimVertical = Bind(c, CONTROLS, "FreeAimVertical", 10, Controls(
                "Unlocked, attacks aim up or down the way the camera looks, as in Elden Ring, by up to this many degrees either way " +
                "(the swing's reach and the upper body tilt about the hips). 0 = off.", new AcceptableValueRange<int>(0, 30)));
            InstantJump = Bind(c, CONTROLS, "InstantJump", true, Controls(
                "Off (Elden Ring): a jump crouches before it leaves the ground. On (Valheim-like): it leaves the ground at once."));
            LockOnRange = Bind(c, CONTROLS, "LockOnRange", 30, Controls(
                "How far away (metres) an enemy can be locked on to; a lock breaks at half as far again. Elden Ring's sandbox used 15.",
                new AcceptableValueRange<int>(10, 60)));
            NoLockOnRecentre = Bind(c, CONTROLS, "NoLockOnRecentre", true, Controls(
                "On: the lock-on button does nothing when there is nothing to lock onto. Off (Elden Ring): it recentres the camera behind the player."));
            CameraAutoRotation = Bind(c, CONTROLS, "CameraAutoRotation", true, Controls(
                "On (Elden Ring): with a gamepad and no lock-on, the camera gently turns to follow the way the player moves " +
                "(not while the right stick turns it, nor for a moment after). Off: the camera turns only by the right stick."));
            LockSwitchFlick = Bind(c, CONTROLS, "LockOnSwitchFlick", 45,
                Controls("Locked on, flick the mouse this far (pixels, within about a fifth of a second) left or right to switch to the next " +
                         "target that way; the right stick switches when pushed over. Higher: a bigger flick.",
                    new AcceptableValueRange<int>(10, 400)), behaviour);
            AutoLockOn = Bind(c, CONTROLS, "ToggleAutoLock-On", true, Controls(
                "Set auto lock-on to new target when former target dies: the lock moves to the enemy nearest the crosshair, as Elden Ring's " +
                "Auto Lock-On. Off: the lock ends with the target."));
            AutoTarget = Bind(c, CONTROLS, "UnlockedAimAssist", false, Controls(
                "Automatically target an enemy when attacking with no lock, as Elden Ring's Auto Target: an attack turns toward the nearest " +
                "enemy within 6 m in the direction pushed (or faced), as far as the attack can turn, without locking on."));
            LeftJoystickInventory = Bind(c, CONTROLS, "LeftJoystickInventory", true, Controls(
                "Gamepad, inventory open. Off (Valheim): the left stick and the D-pad move the selection and the player stands still. " +
                "On: the left stick walks and only the D-pad moves the selection."));
            DodgeInInventory = Bind(c, CONTROLS, "DodgeInInventory", true, Controls(
                "Gamepad, inventory open. Off (Valheim): B closes the inventory. On: B dodges and sprints as usual (with LeftJoystickInventory, " +
                "in the stick's direction) and only Y closes the inventory. B still closes the dialogs opened from it (split, skills...)."));

            foreach (string[] a in Actions)
            {
                Keyboard[a[0]] = Bind(c, KEYBOARD, a[0], a[1], new ConfigDescription(
                    a[3] + " Set, then press the key or mouse button. 'Valheim:Name' follows Valheim's own binding for that button; " +
                    "otherwise an input path such as <Keyboard>/capsLock.", null, InputDrawer.For(gamepad: false)), "Input - Keyboard & Mouse");
                Gamepad[a[0]] = Bind(c, GAMEPAD, a[0], a[2], new ConfigDescription(
                    a[3] + " Set, then press the gamepad button; 'Valheim:Name' follows Valheim's own binding (JoyUse: its Use button); " +
                    "or an input path such as <Gamepad>/buttonEast, empty for none.", null, InputDrawer.For(gamepad: true)),
                    "Input - Gamepad");
            }
            // The modifier's old gamepad default (Y, which also opens the inventory): Valheim's Use button now.
            if (Gamepad["TwoHandModifier"].Value == "<Gamepad>/buttonNorth")
            {
                Gamepad["TwoHandModifier"].Value = "Valheim:JoyUse";
            }

            int movesetOrder = Movesets.Length;
            foreach (string[] m in Movesets)
            {
                // A saved choice under an earlier name keeps its moveset.
                foreach (string section in new[] { weapons, MOVESETS })
                {
                    var def = new ConfigDefinition(section, m[0]);
                    if (_orphans != null && _orphans.TryGetValue(def, out string old) && Renamed.TryGetValue(old, out string now))
                    {
                        _orphans[def] = now;
                    }
                }
                // In Elden Ring's order (renamed saved values are mapped above).
                ConfigEntry<string> entry = Bind(c, MOVESETS, m[0], m[1],
                    new ConfigDescription(m[2] + " Any Elden Ring class can be picked.", new AcceptableValueList<string>(ErClasses),
                        new ConfigurationManagerAttributes { Order = movesetOrder-- }), weapons);
                entry.SettingChanged += (_, __) => MovesetVersion++;
                Moveset[m[0]] = entry;
            }
            ThrowSpears = Bind(c, MOVESETS, "ThrowSpears", false,
                "Off (Elden Ring's Smithscript Spear): the spear heavy throws a spear and the one in your hand stays. " +
                "On (Valheim): the spear itself flies, leaves your inventory and lands where it hits, to be picked up.", weapons);

            // Listed in this order (as F1 sorted them by name), WeaponKnockbackScale under RemoveBlockForce.
            int combat = 100;
            ConfigDescription Combat(string text, AcceptableValueBase range = null) =>
                new ConfigDescription(text, range, new ConfigurationManagerAttributes { Order = combat-- });
            DamageScale = Bind(c, COMBAT, "DamageScale", 1f, Combat("Multiplies the damage of every Eldenheim swing (1 = the weapon's own damage on a first light attack)."), balance);
            GuardBlowPercent = Bind(c, COMBAT, "GuardBlowPercent", 85,
                Combat("A blocked blow that alone fills at least this percent of Valheim's block meter, without filling it, throws the " +
                       "player back in Elden Ring's back roll. A full meter is a guard break instead, so 100 = never.",
                    new AcceptableValueRange<int>(0, 100)), balance);
            HitStop = Bind(c, COMBAT, "HitStop", false,
                Combat("Elden Ring's hit-stop: the attacker's swing and the target freeze for a moment when a hit lands. Off: swings carry straight through."), visuals);
            KnockdownPercent = Bind(c, COMBAT, "KnockdownPercent", 50,
                Combat("A single hit taking at least this percent of maximum health (after armour) knocks the player down, as Elden Ring's " +
                       "knockdown; smaller hits don't stagger.", new AcceptableValueRange<int>(0, 100)), balance);
            RemoveBlockForce = Bind(c, COMBAT, "RemoveBlockForce", true,
                Combat("On (Elden Ring): blocking a melee blow doesn't push the attacker back. Off (Valheim): the guarding item's Block Force shoves it away."));
            WeaponKnockbackScale = Bind(c, COMBAT, "WeaponKnockbackScale", 1f,
                Combat("Multiplies how far Eldenheim's hits push what they strike (Valheim's weapon knockback, its Attack Force). 1 = Valheim's; " +
                       "lower it for weapons such as maces that shove enemies far away, 0 = no push.", new AcceptableValueRange<float>(0f, 2f)));
            // Kept to one decimal (the slider gives any value).
            WeaponKnockbackScale.SettingChanged += (_, __) =>
            {
                float tenth = (float)System.Math.Round(WeaponKnockbackScale.Value, 1);
                if (WeaponKnockbackScale.Value != tenth)
                {
                    WeaponKnockbackScale.Value = tenth;
                }
            };
            PoiseScale = Bind(c, COMBAT, "StaggerScale", 1f, Combat("Multiplies how much each swing staggers."), balance);
            ValheimIframes = Bind(c, COMBAT, "ValheimIframes", false,
                Combat("Off (Elden Ring): rolls are invincible for frames 0-13 at 30 per second (0.43 s; 0-12 when heavily loaded), the backstep for " +
                       "0-7 (0.23 s). On (Valheim): every roll and backstep is invincible from its start for as long as Valheim's own dodge is (up to its " +
                       "dodge clip's DodgeMortal event, read from the game; the log line \"Valheim's dodge\" gives it)."));

            NegativeStamina = Bind(c, STAMINA, "NegativeStamina", true,
                "On (Elden Ring): any attack can start while there is stamina left, whatever it costs. One that runs the bar dry leaves it " +
                "empty (flashing red) until that attack is over, so no chain goes on and the next press is a first attack; then it refills as usual. " +
                "Off (Valheim): an attack needs its whole cost.", balance);
            StaminaRegenRate = Bind(c, STAMINA, "StaminaRegenRate", 100,
                new ConfigDescription("Stamina regeneration, in percent: Valheim's own world setting (setkey staminaregenrate), applied while Eldenheim is enabled. 100 = vanilla.",
                    new AcceptableValueRange<int>(25, 400)), balance);
            SneakStamina = Bind(c, STAMINA, "SneakStamina", true,
                "On: sneaking (crouched and moving) drains stamina as in Valheim. Off (Elden Ring): crouching is free. Sneak skill is raised either way.", balance);
            GearPenalties = Bind(c, STAMINA, "GearPenalties", false,
                "Valheim's movement penalties from gear (heavy armour, tower shields, big weapons): on, walking and running pay the items' " +
                "movement modifier, sprinting 1.5 times it, and running and jumping cost more stamina, as in Valheim. Off: gear never slows the " +
                "player or adds to those costs; its bonuses (Fenris armour) still count.", balance);

            LockOnDot = Bind(c, VISUALS, "LockOnDot", true, "A dot on the locked-on target, as in Elden Ring.", visuals);
            SwingTrails = Bind(c, VISUALS, "SwingTrails", true, "The weapon's white swing trail over each hit's active frames.", visuals);
            // Was a share (0-1) named SprintLean: carried over as a percent.
            if (_orphans != null && _orphans.TryGetValue(new ConfigDefinition(VISUALS, "SprintLean"), out string lean)
                && float.TryParse(lean, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float share))
            {
                _orphans.Remove(new ConfigDefinition(VISUALS, "SprintLean"));
                _orphans[new ConfigDefinition(VISUALS, "SprintLeanPercent")] = ((int)System.Math.Round(share * 100f)).ToString();
            }
            SprintLean = Bind(c, VISUALS, "SprintLeanPercent", 65,
                new ConfigDescription("How much of Elden Ring's forward lean the sprint keeps, in percent (Valheim's character already stoops; 100 = all of it).",
                    new AcceptableValueRange<int>(0, 100)), visuals);

            GuardCounterSound = Bind(c, SOUND, "GuardCounterSound", true,
                "The guard counter's own Elden Ring sound, on top of Valheim's swing (needs the sandbox's baked sounds; see README).", sound);
            // The levels were shares (0-1) under names without "Percent": an old "1" would read as 1 %.
            foreach (string old in new[] { "GuardCounterVolume", "GuardBreakVolume", "ParryVolume" })
            {
                _orphans?.Remove(new ConfigDefinition(SOUND, old));
                _orphans?.Remove(new ConfigDefinition(sound, old));
            }
            GuardCounterVolume = Bind(c, SOUND, "GuardCounterVolumePercent", 30,
                new ConfigDescription("Its level, in percent of the game's own mix.", new AcceptableValueRange<int>(0, 100)));
            GuardBreakVolume = Bind(c, SOUND, "GuardBreakVolumePercent", 40,
                new ConfigDescription("Elden Ring's guard-break sound when the block meter fills, at this level (percent of the game's own mix), on top " +
                                      "of Valheim's block sound (Valheim has none of its own). Needs the sandbox's baked sounds (see README). 0 = off.",
                    new AcceptableValueRange<int>(0, 100)));
            // 0.6.2 had these two defaults the wrong way round: saved as they were, they take the right ones.
            if (GuardCounterVolume.Value == 40 && GuardBreakVolume.Value == 30)
            {
                GuardCounterVolume.Value = 30;
                GuardBreakVolume.Value = 40;
            }
            ParrySound = Bind(c, SOUND, "ParrySound", true,
                "On: a parry (Valheim's perfect block) rings with Elden Ring's parry sound instead of Valheim's; its flash stays. Off: Valheim's. " +
                "Needs the sandbox's baked sounds (see README).");
            ParryVolume = Bind(c, SOUND, "ParryVolumePercent", 40,
                new ConfigDescription("Its level, in percent of the game's own mix.", new AcceptableValueRange<int>(0, 100)));

            IframeDebug = Bind(c, DEBUG, "IframeDebug", false,
                "Shows invincibility: a pale blue capsule round the player while hits pass through, and at the top of the screen the current " +
                "action's frame and its i-frame window (Elden Ring's or, with ValheimIframes, Valheim's).");
            HitboxDebug = Bind(c, DEBUG, "HitboxDebug", false,
                "Draws the weapons' hit capsules: each held item's always (green, red while a hit window is open) and Elden Ring's own blade " +
                "path during a window (yellow). A hit lands where either touches.");
            CombatLog = Bind(c, DEBUG, "CombatLog", false,
                "On-screen list of recent stamina use and damage dealt, with totals. Switching it off clears it.");
            EventLog = Bind(c, DEBUG, "EventLog", false,
                "Writes every swing, hit, stamina use and reaction to the BepInEx log and results.log. Each line is a disk write, so leave it off when playing.");
        }
    }
}
