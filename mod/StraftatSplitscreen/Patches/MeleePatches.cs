using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Drives the overlap fallback off the tick the game already runs on every melee blade,
    /// and keeps the two detection paths from both landing the same hit.
    /// See <see cref="MeleeSplit"/> for why a second path exists at all.
    /// </summary>
    [HarmonyPatch(typeof(MeleeChildCollision), "Update")]
    internal static class MeleeChildCollision_Update_Patch
    {
        private static void Postfix(MeleeChildCollision __instance)
        {
            MeleeSplit.Tick(__instance);
        }
    }

    /// <summary>
    /// The contact path, if it fires at all, must not re-hit someone the overlap already
    /// struck this swing. Only that case is skipped — environment, glass and ragdoll handling
    /// in the rest of the method are left alone.
    /// </summary>
    [HarmonyPatch(typeof(MeleeChildCollision), "OnCollisionEnter")]
    internal static class MeleeChildCollision_OnCollisionEnter_Patch
    {
        private static bool Prefix(MeleeChildCollision __instance, Collision collision)
        {
            if (!LocalPlayers.SplitActive || collision == null)
                return true;

            var victim = collision.transform.GetComponentInParent<PlayerHealth>();
            if (victim == null)
                return true;

            if (MeleeSplit.AlreadyStruck(__instance, victim))
                return false;

            // Let vanilla run, and record what it hit so the overlap stands down.
            MeleeSplit.RecordStrike(__instance, victim, "contact");
            return true;
        }
    }
}
