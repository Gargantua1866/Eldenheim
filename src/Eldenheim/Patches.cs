using HarmonyLib;
using UnityEngine;

namespace Eldenheim
{
    /// <summary>
    /// The hooks. Each one only acts for the local player while Eldenheim owns
    /// it, and each is applied on its own, so a game update that renames one
    /// method costs that one feature, not the mod.
    /// </summary>
    internal static class Patches
    {
        private static Controller Owning(Character c)
        {
            Controller ctl = Controller.Local;
            return ctl != null && ctl.Active && (object)c == Player.m_localPlayer && c != null ? ctl : null;
        }

        public static void Apply(Harmony h)
        {
            Try(h, typeof(Character), "UpdateMotion", prefix: nameof(UpdateMotion));
            Try(h, typeof(Player), nameof(Player.SetControls), prefix: nameof(SetControls));
            Try(h, typeof(CharacterAnimEvent), "OnAnimatorIK", prefix: nameof(AnimatorIK));
            Try(h, typeof(CharacterAnimEvent), "UpdateHeadRotation", prefix: nameof(HeadRotation));
            Try(h, typeof(Character), "RPC_Damage", prefix: nameof(RpcDamage), postfix: nameof(RpcDamaged));
            Try(h, typeof(Humanoid), "BlockAttack", prefix: nameof(BlockStart), postfix: nameof(BlockEnd));
            Try(h, typeof(EffectList), nameof(EffectList.Create), prefix: nameof(EffectCreate));
            Try(h, typeof(Character), nameof(Character.Damage), prefix: nameof(Push));
            Try(h, typeof(Character), "RPC_Stagger", prefix: nameof(Stagger));
            Try(h, typeof(InventoryGui), "Update", prefix: nameof(InventoryStart));
            Try(h, typeof(Humanoid), nameof(Humanoid.IsBlocking), postfix: nameof(IsBlocking));
            Try(h, typeof(Humanoid), "GetCurrentBlocker", postfix: nameof(CurrentBlocker));
            Try(h, typeof(Player), nameof(Player.IsDodgeInvincible), postfix: nameof(DodgeInvincible));
            Try(h, typeof(Player), "Awake", postfix: nameof(PlayerAwake));
            Try(h, typeof(Player), nameof(Player.InAttack), postfix: nameof(InAttack));
            Try(h, typeof(Player), nameof(Player.IsCrouching), postfix: nameof(IsCrouching));
            Try(h, typeof(Character), nameof(Character.IsSneaking), postfix: nameof(IsSneaking));
            // On the dodger's own client: other clients' attacks call it once per collider too.
            Try(h, typeof(Player), "RPC_HitWhileDodging", prefix: nameof(HitWhileDodging));
            Try(h, typeof(Character), nameof(Character.OnAutoJump), prefix: nameof(AutoJump));
            Try(h, typeof(Character), nameof(Character.SetTempParent), postfix: nameof(TempParent));
            Try(h, typeof(Projectile), "UpdateVisual", prefix: nameof(ProjectileVisual));
            Try(h, typeof(Projectile), "OnHit", postfix: nameof(ProjectileHit));
            Try(h, typeof(MonoUpdaters), "FixedUpdate", prefix: nameof(PhysicsStep));
            Try(h, typeof(Attack), "AddHitPoint", prefix: nameof(AttackHitPoint));
            Try(h, typeof(MeleeWeaponTrail), nameof(MeleeWeaponTrail.CustomFixedUpdate), prefix: nameof(TrailStep));
            try
            {
                h.Patch(AccessTools.Method(typeof(Player), "UpdateStats", new[] { typeof(float) }),
                    prefix: new HarmonyMethod(typeof(Patches), nameof(StatsStart)),
                    finalizer: new HarmonyMethod(typeof(Patches), nameof(StatsEnd)));
            }
            catch (System.Exception e)
            {
                Plugin.Report("patch: Player.UpdateStats failed: " + e.Message);
            }
        }

        // Inside Player.UpdateStats, whose regeneration stops for InAttack().
        private static bool _inStats;

        private static void StatsStart() => _inStats = true;

        private static System.Exception StatsEnd(System.Exception __exception)
        {
            _inStats = false;
            return __exception;
        }

        private static void Try(Harmony h, System.Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    Plugin.Report($"patch: {type.Name}.{method} not found");
                    return;
                }
                h.Patch(original,
                    prefix: prefix != null ? new HarmonyMethod(typeof(Patches), prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(typeof(Patches), postfix) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(typeof(Patches), finalizer) : null);
            }
            catch (System.Exception e)
            {
                Plugin.Report($"patch: {type.Name}.{method} failed: {e.Message}");
            }
        }

        /// <summary>
        /// Eldenheim's attacks count as Valheim's: it then refuses to equip,
        /// unequip or sheathe mid-swing, as it does in its own attacks. Not
        /// for stamina regeneration, which would then wait for the attack's
        /// last frame: Eldenheim holds it itself until the recovery frame.
        /// </summary>
        private static void InAttack(Player __instance, ref bool __result)
        {
            if (!__result && !_inStats && Owning(__instance) is Controller ctl && ctl.Attacking)
            {
                __result = true;
            }
        }

        /// <summary>Valheim's crouch is its animator state, which Eldenheim's pose replaces: the sim's crouch instead (stealth).</summary>
        private static void IsCrouching(Player __instance, ref bool __result)
        {
            if (Owning(__instance) is Controller ctl && ctl.Sim != null)
            {
                __result = ctl.Sim.Crouching;
            }
        }

        private static void IsSneaking(Character __instance, ref bool __result)
        {
            if (Owning(__instance) is Controller ctl)
            {
                __result = ctl.Sneaking;
            }
        }

        /// <summary>Valheim's ladder steps hop the character up (AutoJumpLedge): Eldenheim walks up ladders instead.</summary>
        private static bool AutoJump(Character __instance) => Owning(__instance) == null;

        /// <summary>Every client: a weapon thrown on a borrowed spear projectile shows its own model (Combat.ThrownModel).</summary>
        private static void ProjectileVisual(Projectile __instance) => Combat.ShowThrownModel(__instance);

        /// <summary>A thrown sledge (Smithscript Hammer) slams where it struck.</summary>
        private static void ProjectileHit(Projectile __instance, Collider collider, Vector3 hitPoint) => Combat.ThrownImpact(__instance, collider, hitPoint);

        /// <summary>A catapult holds the player (Catapult.CollectLaunchCharacters) and throws it: Valheim's physics until a move.</summary>
        private static void TempParent(Character __instance)
        {
            if ((object)__instance == Player.m_localPlayer)
            {
                Controller.Local?.Catapulted();
            }
        }

        private static bool HitWhileDodging(Player __instance)
        {
            return !(Owning(__instance) is Controller ctl) || ctl.Sim == null || ctl.FirstDodgeThisRoll();
        }

        private static void PlayerAwake(Player __instance)
        {
            Controller.For(__instance);
        }

        private static void PhysicsStep() => Controller.BeforePhysicsStep();

        /// <summary>A posed player's swing trails sample every rendered frame from the drawn pose (Controller.DriveTrails), not each physics step.</summary>
        private static bool TrailStep(MeleeWeaponTrail __instance) => !Controller.TrailDrivenHere(__instance);

        /// <summary>
        /// A melee blow passing under a jump's legs (Controller.Incoming's Jumped)
        /// is no hit at all where the attack runs: no sparks or hit sound, as a
        /// dodge's i-frames (Attack.DoMeleeAttack skips IsDodgeInvincible targets).
        /// </summary>
        private static bool AttackHitPoint(GameObject go, Vector3 point) =>
            !((object)go == (Player.m_localPlayer != null ? Player.m_localPlayer.gameObject : null) && Controller.Local is Controller ctl && ctl.PassesUnder(point));

        private static bool UpdateMotion(Character __instance, float dt)
        {
            Controller ctl = Owning(__instance);
            if (ctl == null)
            {
                return true;
            }
            ctl.FixedTick(dt);
            return false;
        }

        private static void SetControls(Player __instance, ref Vector3 movedir, ref bool attack, ref bool attackHold, ref bool secondaryAttack,
            ref bool secondaryAttackHold, ref bool block, ref bool blockHold, ref bool jump, ref bool crouch, ref bool run, ref bool autoRun, ref bool dodge)
        {
            if (Owning(__instance) == null)
            {
                return;
            }
            // Every combat and movement button is Eldenheim's while it owns the player.
            movedir = Vector3.zero;
            attack = attackHold = secondaryAttack = secondaryAttackHold = false;
            block = blockHold = jump = crouch = run = autoRun = dodge = false;
        }

        private static bool AnimatorIK(CharacterAnimEvent __instance)
        {
            Character c = Traverse.Create(__instance).Field("m_character").GetValue<Character>();
            return !Controller.Posing(c);
        }

        private static bool HeadRotation(CharacterAnimEvent __instance)
        {
            Character c = Traverse.Create(__instance).Field("m_character").GetValue<Character>();
            return !Controller.Posing(c);
        }

        private static bool RpcDamage(Character __instance, HitData hit, out float __state)
        {
            __state = __instance.GetHealth();
            Controller ctl = Owning(__instance);
            return ctl == null || ctl.Incoming(hit);
        }

        // After Valheim's own damage (armour, block, i-frames), so a knockdown's i-frames never cancel the blow that caused it.
        private static void RpcDamaged(Character __instance, float __state)
        {
            Owning(__instance)?.Damaged(__state - __instance.GetHealth());
        }

        // Valheim's stagger turns the body to the blow and plays under the ER pose; the sim's own
        // reactions (GuardBreak from the block meter, knockdowns) stand in for it.
        private static bool Stagger(Character __instance) => Owning(__instance) == null;

        private static void BlockStart(Humanoid __instance)
        {
            Owning(__instance)?.BlockStarting(__instance.GetStaggerPercentage());
            if ((object)__instance == Player.m_localPlayer)
            {
                Controller.Local?.QuietParry();
                _blocking = true;
            }
        }

        private static bool _blocking;

        /// <summary>
        /// The end of a successful block pushes the attacker back by the shield's
        /// Block Force (Humanoid.BlockAttack: attacker.Damage with no damage and
        /// that push). RemoveBlockForce drops that hit.
        /// </summary>
        private static bool Push(Character __instance, HitData hit) =>
            !(_blocking && Settings.Enabled.Value && Settings.RemoveBlockForce.Value && (object)__instance != Player.m_localPlayer
              && hit.GetTotalDamage() <= 0f && hit.m_pushForce > 0f);

        private static void BlockEnd(Humanoid __instance)
        {
            if ((object)__instance == Player.m_localPlayer)
            {
                Controller.Local?.LoudParry();
                _blocking = false;
            }
        }

        private static void EffectCreate(EffectList __instance) => Controller.Parried(__instance);

        private static readonly string[] StickDirections = { "JoyLStickLeft", "JoyLStickRight", "JoyLStickUp", "JoyLStickDown" };

        /// <summary>
        /// Before the inventory reads the gamepad: with LeftJoystickInventory the
        /// left stick's directions, and with DodgeInInventory B, read as not
        /// pressed this frame, so the stick walks instead of moving the selection
        /// (the D-pad does) and B dodges instead of closing it (Y does). B still
        /// closes the dialogs opened from it. (Patching ZInput.GetButtonDown did
        /// nothing: the one-line method is inlined into its callers.)
        /// </summary>
        private static void InventoryStart(InventoryGui __instance)
        {
            if (!Settings.Enabled.Value || !InventoryGui.IsVisible() || !ZInput.IsGamepadActive())
            {
                return;
            }
            if (Settings.LeftJoystickInventory.Value)
            {
                foreach (string name in StickDirections)
                {
                    ZInput.ResetButtonStatus(name);
                }
            }
            InventoryGui gui = __instance;
            if (Settings.DodgeInInventory.Value && !gui.m_trophiesPanel.activeSelf && !gui.m_achievementsPanel.gameObject.activeSelf
                && !gui.m_skillsDialog.gameObject.activeSelf && !gui.m_textsDialog.gameObject.activeSelf && !gui.m_splitDialog.IsActive
                && !gui.m_variantDialog.gameObject.activeSelf)
            {
                ZInput.ResetButtonStatus("JoyButtonB");
            }
        }

        private static void IsBlocking(Humanoid __instance, ref bool __result)
        {
            Controller ctl = Owning(__instance);
            if (ctl != null)
            {
                __result = ctl.Blocking;
            }
        }

        private static void CurrentBlocker(Humanoid __instance, ref ItemDrop.ItemData __result)
        {
            Controller ctl = Owning(__instance);
            if (ctl != null)
            {
                __result = ctl.Blocker ?? __result;
            }
        }

        private static void DodgeInvincible(Player __instance, ref bool __result)
        {
            Controller ctl = Owning(__instance);
            if (ctl != null && ctl.Sim != null)
            {
                __result = ctl.Sim.Invincible();
            }
        }
    }
}
