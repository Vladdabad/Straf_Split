using HarmonyLib;

namespace StraftatSplitscreen.Patches
{
    // ============================ TEMPORARY DIAGNOSTIC ============================
    // Delete this file together with ScopeDiagnostics.cs once the "scope reticle does not
    // appear when aiming down sights" bug is fixed. See the removal checklist in the doc
    // comment on ScopeDiagnostics.
    // ==============================================================================

    /// <summary>
    /// The two places the game writes a weapon's reticle onto a crosshair, reported as they
    /// happen.
    ///
    /// Both are Postfixes, and that is the whole trick: Harmony runs
    /// Prefix -> original -> Postfix -> Finalizer, and
    /// <see cref="ItemBehaviour_Crosshair_Patch"/> swaps <c>Crosshair.Instance</c> in its
    /// prefix and puts it back in its finalizer. A postfix therefore observes the singleton
    /// exactly as the game's own code just saw it — so <c>Crosshair.Instance</c> read here IS
    /// the object that was written to, and comparing it against
    /// <c>CrosshairSplit.For(holder)</c> answers the only question a patch can answer that
    /// the per-frame poll cannot: did the swap land on the right player's crosshair?
    ///
    /// Reading it from a prefix instead would show the pre-swap value and prove nothing.
    /// </summary>
    [HarmonyPatch(typeof(ItemBehaviour), "OnGrab")]
    internal static class ItemBehaviour_Scope_Watch_Patch
    {
        private static void Postfix(ItemBehaviour __instance, bool owner, bool rightHand)
        {
            // OnGrab returns early for a non-owner or the off hand without touching any
            // crosshair, so those calls are not writes and saying so would be misleading.
            if (!owner || !rightHand)
                return;

            ScopeDiagnostics.NoteWrite("grabbed", __instance, Crosshair.Instance);
        }
    }

    /// <summary>
    /// The per-frame half. <c>ItemBehaviour.Update</c> rewrites <c>canScopeAim</c> every
    /// frame the weapon is held, which is the term most likely to be landing on the wrong
    /// crosshair — <c>OnGrab</c> fires once and is easy to get right, this one fires forever
    /// and fights with every other held weapon for the same field.
    ///
    /// Only flips are reported. Logging a write that changed nothing would be one line per
    /// weapon per frame, which is the log unusable for the sake of information the poll in
    /// <see cref="ScopeDiagnostics"/> already carries.
    /// </summary>
    [HarmonyPatch(typeof(ItemBehaviour), "Update")]
    internal static class ItemBehaviour_ScopeUpdate_Watch_Patch
    {
        /// <summary>
        /// The crosshair the prefix looked at, carried alongside the flag it read off it.
        ///
        /// The reference is half the state on purpose. Harmony gives no ordering guarantee
        /// between two patch classes' prefixes, so this one may run either side of the
        /// singleton swap in <see cref="ItemBehaviour_Crosshair_Patch"/> — and if it ran
        /// before, the postfix would be comparing one player's canScopeAim against another's
        /// and reporting a flip every single frame. Comparing the object too makes a
        /// mismatched pair simply not a reading.
        /// </summary>
        private struct Seen
        {
            internal Crosshair Cross;
            internal bool CanScopeAim;
        }

        private static void Prefix(out Seen __state)
        {
            var cross = Crosshair.Instance;
            __state = new Seen { Cross = cross, CanScopeAim = cross != null && cross.canScopeAim };
        }

        private static void Postfix(ItemBehaviour __instance, Seen __state)
        {
            var cross = Crosshair.Instance;
            if (cross == null || !ReferenceEquals(cross, __state.Cross) || cross.canScopeAim == __state.CanScopeAim)
                return;

            ScopeDiagnostics.NoteWrite(
                $"canScopeAim {(__state.CanScopeAim ? "Y" : "N")}->{(cross.canScopeAim ? "Y" : "N")}",
                __instance, cross);
        }
    }
}
