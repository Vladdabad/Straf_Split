using System;
using DG.Tweening;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Routes the ammo counter to the player whose HUD it is. See <see cref="HudRouting"/> for
    /// why only ammo needs this and health does not.
    /// </summary>
    internal static class HudPatches
    {
        // HUD's flags and its TMP reference are [SerializeField] private. Reflected by name
        // rather than via cached FieldRefs so a rename degrades to "leave it to vanilla"
        // instead of throwing at class-load time.
        internal static bool Flag(Component c, string name)
        {
            var f = AccessTools.Field(c.GetType(), name);
            return f != null && f.GetValue(c) is bool b && b;
        }
    }

    /// <summary>
    /// Marks which player's weapon is talking. `Weapon.WeaponUpdate` pushes that weapon's ammo
    /// into the shared bus every frame while it is held; wrapping it is what lets the write be
    /// attributed to a player without patching each of the seven call sites that reach
    /// `ChangeAmmoText`.
    /// </summary>
    [HarmonyPatch(typeof(Weapon), nameof(Weapon.WeaponUpdate))]
    internal static class Weapon_WeaponUpdate_Patch
    {
        private static void Prefix(Weapon __instance)
        {
            if (!LocalPlayers.SplitActive)
                return;
            HudRouting.Current = LocalPlayers.OwningPawn(__instance.transform.root.gameObject);
        }

        private static void Postfix()
        {
            HudRouting.Current = null;
        }
    }

    /// <summary>
    /// Copy every ammo write into the calling player's own slot. The original still runs, so
    /// the shared bus — which the scene's minimalist UI also reads — keeps behaving exactly as
    /// it does in vanilla.
    /// </summary>
    [HarmonyPatch(typeof(PauseManager), nameof(PauseManager.ChangeAmmoText))]
    internal static class PauseManager_ChangeAmmoText_Patch
    {
        private static void Prefix(string text, string reloadText, bool right)
        {
            if (!LocalPlayers.SplitActive)
                return;
            // Vanilla concatenates the same way before assigning; match it so the HUD path
            // below is a drop-in for what the counter would otherwise have copied.
            HudRouting.Record(reloadText + text, right);
        }
    }

    /// <summary>
    /// Each bullet counter reads its own player instead of the shared bus.
    ///
    /// `HUD.Update` copies `PauseManager.Instance.{left,right}GunAmmo.text` onto its TMP every
    /// frame, which is why every pawn's counter showed the same number. Only the two ammo
    /// flags are redirected: the ping and FPS variants of this component read genuinely global
    /// values and are left to vanilla.
    ///
    /// The scale punch is reproduced rather than skipped — it is the feedback that makes a
    /// shot read as a shot, and dropping it would be a visible regression for player 1.
    /// </summary>
    [HarmonyPatch(typeof(HUD), "Update")]
    internal static class HUD_Update_Patch
    {
        private static bool Prefix(HUD __instance)
        {
            if (!LocalPlayers.SplitActive)
                return true;

            bool right = HudPatches.Flag(__instance, "ammoRight");
            bool left = HudPatches.Flag(__instance, "ammoLeft");
            if (!right && !left)
                return true; // ping / FPS / reload variants: global, leave vanilla alone

            var player = LocalPlayers.OwningPawn(__instance.transform.root.gameObject);
            if (player == null)
                return true;

            var field = AccessTools.Field(typeof(HUD), "text");
            var tmp = field != null ? field.GetValue(__instance) as TMP_Text : null;
            if (tmp == null)
                return true;

            string value = HudRouting.Value(player, right);

            try
            {
                var parent = __instance.transform.parent;
                if (parent != null)
                {
                    if (tmp.text != value)
                    {
                        __instance.transform.DOKill(false);
                        parent.localScale = Vector3.one;
                        parent.DOPunchScale(new Vector3(1.15f, 1.15f, 1.15f), 0.1f, 10, 1f);
                    }
                    else
                    {
                        parent.DOScale(Vector3.one, 0.3f);
                    }
                }
                tmp.text = value;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[hud] counter update threw, falling back to vanilla: {ex}");
                return true;
            }

            return false;
        }
    }
}
