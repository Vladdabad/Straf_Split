using HarmonyLib;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Stops the one keyboard and mouse from driving every player at once.
    ///
    /// Splitting input at the Input System level works — each player has their own
    /// PlayerControls asset pinned to their own gamepad — but the
    /// game also reads the OLD UnityEngine.Input API in a few pawn-side places. Legacy input
    /// is global: it has no notion of which device belongs to which player, so every pawn
    /// polling it responds to the one keyboard and mouse. No amount of device filtering on
    /// the new API can prevent that.
    ///
    /// These features stay with player 1, who is the one sitting at the keyboard, and are
    /// skipped for everyone else. Each is a self-contained method whose only input source is
    /// the legacy API, so skipping it costs that player one feature rather than breaking
    /// anything shared.
    /// </summary>
    [HarmonyPatch(typeof(FirstPersonController), "HandleTaunt")]
    internal static class FirstPersonController_HandleTaunt_Patch
    {
        /// <summary>
        /// Taunts are bound to the number-row keys via Input.GetKeyDown, so pressing 1-0
        /// makes every character taunt at once. Players 2+ lose keyboard taunts entirely;
        /// giving them back means binding taunts to a gamepad action, a separate job.
        /// </summary>
        private static bool Prefix(FirstPersonController __instance)
        {
            if (!LocalPlayers.SplitActive)
                return true;
            var player = LocalPlayers.OwningPawn(__instance.gameObject);
            return player == null || player.Index == 0;
        }
    }

    /// <summary>
    /// Weapon sway is driven by Input.GetAxis("Mouse X"/"Mouse Y"), so every player's arms
    /// follow the one mouse. Skipping it leaves their weapon steady instead of visibly wrong;
    /// the sway is cosmetic. Driving it from each player's own look axis would need the sway
    /// maths reimplemented against their action asset.
    /// </summary>
    [HarmonyPatch(typeof(FPArms), "WeaponSway")]
    internal static class FPArms_WeaponSway_Patch
    {
        private static bool Prefix(FPArms __instance)
        {
            if (!LocalPlayers.SplitActive)
                return true;
            var player = LocalPlayers.OwningPawn(__instance.transform.root.gameObject);
            return player == null || player.Index == 0;
        }
    }
}
