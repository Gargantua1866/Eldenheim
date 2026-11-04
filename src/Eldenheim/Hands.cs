using HarmonyLib;
using UnityEngine;

namespace Eldenheim
{
    /// <summary>
    /// Elden Ring's hands on Valheim's equipment slots.
    ///
    /// Any melee weapon Eldenheim plays (one- or two-handed in Valheim) can go
    /// in either hand: equipping it while the two-hand modifier is held puts
    /// it in the left hand. A Valheim two-handed weapon in the right hand
    /// keeps a shield, torch or weapon in the left (Valheim would unequip
    /// them). Items that are already a pair (knuckles and claws, Skoll and
    /// Hati, the dual axes) take both hands and can't share them.
    ///
    /// Sheathing (R) and reloading the character bring the left weapon back
    /// to the left hand: it carries a mark in its custom data while there.
    /// </summary>
    internal static class Hands
    {
        public const string LeftMark = "eldenheim.left";

        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LeftItem = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_leftItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenLeft = AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_hiddenLeftItem");
        private static readonly System.Reflection.MethodInfo SetupEquipment = AccessTools.Method(typeof(Humanoid), "SetupEquipment");
        private static readonly System.Reflection.MethodInfo TriggerEquipEffect = AccessTools.Method(typeof(Humanoid), "TriggerEquipEffect");

        private static ItemDrop.ItemData _showingLeft;
        private static bool _loading;

        private static ItemDrop.ItemData.ItemType Type(ItemDrop.ItemData i) => i.m_shared.m_itemType;

        /// <summary>Knuckles/claws, Skoll and Hati, dual axes: one item for both hands.</summary>
        public static bool IsPair(ItemDrop.ItemData i) =>
            i != null && (i.m_shared.m_skillType == Skills.SkillType.Unarmed || (i.m_shared.m_attack?.m_attackAnimation ?? "").Contains("dual"));

        /// <summary>A melee weapon Eldenheim plays that can be held in one hand (either hand).</summary>
        public static bool IsHandWeapon(ItemDrop.ItemData i) =>
            i != null && (Type(i) == ItemDrop.ItemData.ItemType.OneHandedWeapon || Type(i) == ItemDrop.ItemData.ItemType.TwoHandedWeapon)
            && !IsPair(i) && Controller.Playable(i);

        public static bool IsLeftWeapon(ItemDrop.ItemData left) => IsHandWeapon(left);

        private static bool IsOffHand(ItemDrop.ItemData i) =>
            i != null && (Type(i) == ItemDrop.ItemData.ItemType.Shield || Type(i) == ItemDrop.ItemData.ItemType.Torch || IsHandWeapon(i));

        public static void Apply(Harmony h)
        {
            Patch(h, typeof(Humanoid), nameof(Humanoid.EquipItem), nameof(EquipPrefix), nameof(EquipPostfix));
            Patch(h, typeof(Humanoid), "ShowHandItems", nameof(ShowPrefix), nameof(ShowPostfix));
            Patch(h, typeof(Player), "EquipInventoryItems", nameof(LoadPrefix), nameof(LoadPostfix));
            Patch(h, typeof(Humanoid), nameof(Humanoid.Pickup), nameof(PickupPrefix), nameof(PickupPostfix));
        }

        private static void Patch(Harmony h, System.Type type, string method, string prefix, string postfix)
        {
            try
            {
                var original = AccessTools.Method(type, method);
                if (original == null)
                {
                    Plugin.Report($"patch: {type.Name}.{method} not found (no left-hand weapons)");
                    return;
                }
                h.Patch(original, prefix: new HarmonyMethod(typeof(Hands), prefix), postfix: new HarmonyMethod(typeof(Hands), postfix));
            }
            catch (System.Exception e)
            {
                Plugin.Report($"patch: {type.Name}.{method} failed: {e.Message}");
            }
        }

        private static bool ShowPrefix(Humanoid __instance)
        {
            // No drawing mid-attack (Valheim only guards sheathing).
            if (__instance == Player.m_localPlayer && Controller.Local != null && Controller.Local.Attacking)
            {
                return false;
            }
            _showingLeft = HiddenLeft(__instance);
            return true;
        }
        private static void ShowPostfix() => _showingLeft = null;
        private static void LoadPrefix() => _loading = true;
        // Picking up uses the Use key, which is also the two-hand modifier: a
        // weapon Valheim auto-equips on pickup goes to the right hand.
        private static void PickupPrefix() => _pickingUp = true;
        private static void PickupPostfix() => _pickingUp = false;
        private static bool _pickingUp;
        private static void LoadPostfix() => _loading = false;

        private static bool WantsLeft(ItemDrop.ItemData item)
        {
            if (!IsHandWeapon(item) || !Settings.Enabled.Value)
            {
                return false;
            }
            if (item == _showingLeft || _loading && item.m_customData.ContainsKey(LeftMark))
            {
                return true;
            }
            return !_loading && !_pickingUp && Bindings.ModifierHeld();
        }

        public struct Kept
        {
            public ItemDrop.ItemData Left, Right;
        }

        /// <summary>
        /// Into the left hand: equipped here and the original skipped. A torch
        /// beside a right weapon goes to the left hand here too. Otherwise the
        /// original runs, with whatever it would wrongly unequip hidden from
        /// it and put back afterwards.
        /// </summary>
        private static bool EquipPrefix(Humanoid __instance, ItemDrop.ItemData item, bool triggerEquipEffects, ref bool __result, out Kept __state)
        {
            __state = default;
            if (!(__instance is Player p) || p != Player.m_localPlayer || item == null || !Settings.Enabled.Value)
            {
                return true;
            }
            if (WantsLeft(item))
            {
                __result = EquipLeft(p, item, triggerEquipEffects);
                return false;
            }
            ItemDrop.ItemData left = LeftItem(p), right = RightItem(p);
            if (Type(item) == ItemDrop.ItemData.ItemType.Torch && IsHandWeapon(right))
            {
                __result = EquipLeft(p, item, triggerEquipEffects);
                return false;
            }
            if (IsHandWeapon(item) && left != null && left != item && IsOffHand(left))
            {
                // A right weapon (even a Valheim two-hander) keeps the off-hand.
                __state.Left = left;
                LeftItem(p) = null;
            }
            if (Type(item) == ItemDrop.ItemData.ItemType.Shield && IsHandWeapon(right))
            {
                // A shield beside a Valheim two-hander.
                __state.Right = right;
                RightItem(p) = null;
            }
            return true;
        }

        private static void EquipPostfix(Humanoid __instance, ItemDrop.ItemData item, bool __result, Kept __state)
        {
            bool changed = false;
            if (__state.Left != null)
            {
                if (LeftItem(__instance) == null && RightItem(__instance) != __state.Left)
                {
                    LeftItem(__instance) = __state.Left;
                    __state.Left.m_equipped = true;
                }
                else
                {
                    __state.Left.m_equipped = __instance.IsItemEquiped(__state.Left);
                    __state.Left.m_customData.Remove(LeftMark);
                }
                changed = true;
            }
            if (__state.Right != null)
            {
                if (RightItem(__instance) == null && LeftItem(__instance) != __state.Right)
                {
                    RightItem(__instance) = __state.Right;
                    __state.Right.m_equipped = true;
                }
                else
                {
                    __state.Right.m_equipped = __instance.IsItemEquiped(__state.Right);
                }
                changed = true;
            }
            if (changed)
            {
                SetupEquipment?.Invoke(__instance, null);
            }
            if (__result && item != null && RightItem(__instance) == item)
            {
                item.m_customData.Remove(LeftMark);
            }
        }

        private static bool EquipLeft(Player p, ItemDrop.ItemData item, bool effects)
        {
            if (p.IsItemEquiped(item) && LeftItem(p) == item)
            {
                return false;
            }
            if (!p.GetInventory().ContainsItem(item) || p.InAttack() || p.InDodge() || item.m_shared.m_useDurability && item.m_durability <= 0f)
            {
                return false;
            }
            if (RightItem(p) == item)
            {
                p.UnequipItem(item, false);
            }
            p.UnequipItem(LeftItem(p), effects);
            ItemDrop.ItemData right = RightItem(p);
            if (right != null && !IsHandWeapon(right) && Type(right) != ItemDrop.ItemData.ItemType.OneHandedWeapon)
            {
                // Tools, bows, a right-hand torch and paired items need the other hand.
                p.UnequipItem(right, effects);
            }
            LeftItem(p) = item;
            item.m_equipped = true;
            if (IsHandWeapon(item))
            {
                item.m_customData[LeftMark] = "1";
            }
            SetupEquipment?.Invoke(p, null);
            if (effects)
            {
                TriggerEquipEffect?.Invoke(p, new object[] { item });
            }
            return true;
        }

        /// <summary>
        /// Eldenheim switched off: put back what vanilla Valheim can't hold: a
        /// weapon in the left hand, or an off-hand beside a two-hander.
        /// </summary>
        public static void Release(Player p)
        {
            ItemDrop.ItemData left = LeftItem(p), right = RightItem(p);
            if (IsLeftWeapon(left))
            {
                p.UnequipItem(left, false);
                left.m_customData.Remove(LeftMark);
            }
            else if (left != null && right != null && Type(right) == ItemDrop.ItemData.ItemType.TwoHandedWeapon)
            {
                p.UnequipItem(left, false);
            }
        }
    }
}
