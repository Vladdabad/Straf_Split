using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    // ============================ TEMPORARY DIAGNOSTIC ============================
    // Delete this file together with GrenadeDiagnostics.cs once the "picking up a grenade
    // reveals a normally invisible object" bug is fixed. See the removal checklist in the
    // doc comment on GrenadeDiagnostics.
    // ==============================================================================

    /// <summary>
    /// The moment an item lands in a hand. This is the game's own "you are now holding this"
    /// callback — it is what sets the crosshair, and it runs from the pickup RPC on the
    /// owning client — so it is the tightest event available and it fires before any of the
    /// visual consequences.
    ///
    /// A postfix, not a prefix: OnGrab itself destroys the item's Rigidbody and rewrites the
    /// crosshair, and the interesting state changes are the ones that follow it.
    /// </summary>
    [HarmonyPatch(typeof(ItemBehaviour), "OnGrab")]
    internal static class ItemBehaviour_OnGrab_Watch_Patch
    {
        private static void Postfix(ItemBehaviour __instance, bool owner, bool rightHand)
        {
            GrenadeDiagnostics.Note(
                GrenadeDiagnostics.HolderOf(__instance),
                $"grabbed (owner={owner}, {(rightHand ? "right" : "left")} hand)",
                __instance.gameObject);
        }
    }

    /// <summary>
    /// The other half. A grenade is normally dropped or thrown a second after it is picked
    /// up, so without this the window closes mid-interaction and the log stops exactly where
    /// it gets interesting.
    /// </summary>
    [HarmonyPatch(typeof(ItemBehaviour), "OnDrop")]
    internal static class ItemBehaviour_OnDrop_Watch_Patch
    {
        // No parameter injection here on purpose: Harmony matches injected arguments by the
        // real parameter name, and nothing in this postfix needs the camera.
        private static void Postfix(ItemBehaviour __instance)
        {
            GrenadeDiagnostics.Note(
                GrenadeDiagnostics.HolderOf(__instance),
                "dropped",
                __instance.gameObject);
        }
    }

    /// <summary>
    /// SetLayer moves an item and everything under it onto layer 8 when it is taken, and
    /// back to layer 0 when it is not — a whole-subtree layer rewrite, which is transition
    /// type 3 in the watcher and the single most likely way a held item drags something
    /// visible along with it. The watcher reports the layer change either way; this reports
    /// the call that caused it, which is the half a state sample cannot supply.
    /// </summary>
    [HarmonyPatch(typeof(ItemBehaviour), "SetLayer")]
    internal static class ItemBehaviour_SetLayer_Watch_Patch
    {
        private static void Postfix(ItemBehaviour __instance)
        {
            GrenadeDiagnostics.Note(
                GrenadeDiagnostics.HolderOf(__instance),
                "SetLayer",
                __instance.gameObject);
        }
    }
}
