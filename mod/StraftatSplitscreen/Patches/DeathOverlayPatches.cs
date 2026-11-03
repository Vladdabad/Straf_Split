using HarmonyLib;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Sends each death banner to the viewport of the player who died.
    ///
    /// `PlayerSetup.OnDisable` calls this once for the dying pawn, and it is handed that pawn's
    /// own `PlayerHealth` — so one patch attributes the write for every way a player can die,
    /// with no need to touch the paths that kill them. Player 1 keeps the vanilla coroutine
    /// (their texts have simply been re-anchored into their own slice); everyone else is served
    /// by their own clone, and the original is left untouched so nobody else's screen reports a
    /// death that was not theirs.
    ///
    /// A death the split cannot place — no roster entry, or the UI could not be resolved —
    /// falls through to vanilla, which is a banner in the wrong slice rather than no banner.
    /// </summary>
    [HarmonyPatch(typeof(PauseManager), nameof(PauseManager.ShowEnemyHealth))]
    internal static class PauseManager_ShowEnemyHealth_Patch
    {
        private static bool Prefix(float h, PlayerHealth ph)
        {
            if (!LocalPlayers.SplitActive || ph == null)
                return true;

            var player = LocalPlayers.OwningPawn(ph.gameObject);
            return !DeathOverlaySplit.Request(player, h, ph);
        }
    }
}
