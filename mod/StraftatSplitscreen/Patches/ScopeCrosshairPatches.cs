using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Which crosshair the game's own code writes to while a weapon is being handled.
    ///
    /// <see cref="CrosshairSplit"/> gives every local player their own Crosshair rig, but the
    /// game decides what a reticle SHOWS somewhere else entirely, and always through the
    /// singleton. Every one of those writes is fixed the same way this mod fixes every
    /// singleton: swap the static for the duration of the call, so the game's own code writes
    /// to the right object without knowing anything changed. Same shape as the input-asset
    /// swap in <see cref="PlayerManager_SpawnPlayer_Patch"/> and the ClientInstance swap in
    /// <see cref="ClientInstance_OnStartClient_Patch"/>.
    ///
    /// Player 1 is not special-cased: <c>CrosshairSplit.For(0)</c> answers with the scene's own
    /// crosshair, which is what the static already held, so the swap is a no-op for them.
    /// </summary>
    internal static class CrosshairRouting
    {
        private static bool _warned;

        /// <summary>The crosshair belonging to this pawn's player, or null to leave the static alone.</summary>
        internal static Crosshair ForPawn(GameObject pawn)
        {
            if (!LocalPlayers.SplitActive)
                return null;

            var player = pawn != null ? LocalPlayers.OwningPawn(pawn) : null;
            if (player == null)
                return null; // dropped, or a remote player's — the game's own handling is right

            var cross = CrosshairSplit.For(player.Index);
            if (cross == null && !_warned)
            {
                _warned = true;
                Plugin.Log.LogWarning(
                    $"[crosshair] {player.Label} has no crosshair rig yet — their weapon's reticle " +
                    "is going to P1's crosshair until one exists.");
            }
            return cross;
        }

        /// <summary>
        /// Install a crosshair as the singleton, returning what to hand back afterwards.
        /// Null means "nothing to undo" — either there was no swap to make, or it was already
        /// the right one.
        /// </summary>
        internal static Crosshair Swap(Crosshair to)
        {
            if (to == null || ReferenceEquals(to, Crosshair.Instance))
                return null;

            var previous = Crosshair.Instance;
            Crosshair.Instance = to;
            return previous;
        }

        internal static void Restore(Crosshair previous)
        {
            if (previous != null)
                Crosshair.Instance = previous;
        }
    }

    /// <summary>
    /// Sends each weapon's reticle to the crosshair of the player actually holding it.
    ///
    /// <code>
    ///   // ItemBehaviour.OnGrab
    ///   Crosshair.Instance.standCrosshair = this.standCrosshair;
    ///   Crosshair.Instance.aimCrosshair   = this.aimCrosshair;
    ///   Crosshair.Instance.canScopeAim    = true;
    ///
    ///   // ItemBehaviour.Update, every frame while aiming
    ///   Crosshair.Instance.canScopeAim = this.playerController.isAiming &amp;&amp; ...;
    /// </code>
    ///
    /// `Crosshair.Instance` is player 1's. So a sniper picked up by player 2 writes its scope
    /// reticle onto player 1's crosshair, and player 2 — the one actually looking down the
    /// scope — keeps whatever reticle was there before. With both players carrying weapons the
    /// two fight over the same fields every frame, which is the reticle being wrong rather than
    /// merely misplaced.
    ///
    /// Finalizer rather than Postfix, so a throw inside the game's method cannot leave the
    /// singleton pointing at another player's crosshair for the rest of the round.
    ///
    /// The OnGrab half is redundant on the host, where
    /// <see cref="PlayerPickup_Crosshair_Patch"/> has already installed the same crosshair
    /// one level further out and the swap here no-ops. It is kept deliberately: it costs a
    /// reference comparison, it is the documented mechanism, and it is what still covers
    /// OnGrab if the call chain into it ever changes.
    /// </summary>
    [HarmonyPatch(typeof(ItemBehaviour))]
    internal static class ItemBehaviour_Crosshair_Patch
    {
        private static readonly AccessTools.FieldRef<ItemBehaviour, GameObject> RootObjectRef =
            AccessTools.FieldRefAccess<ItemBehaviour, GameObject>("rootObject");

        private static Crosshair HolderCrosshair(ItemBehaviour item)
        {
            return CrosshairRouting.ForPawn(RootObjectRef(item));
        }

        [HarmonyPrefix]
        [HarmonyPatch("OnGrab")]
        private static void BeforeOnGrab(ItemBehaviour __instance, out Crosshair __state)
        {
            __state = CrosshairRouting.Swap(HolderCrosshair(__instance));
        }

        [HarmonyFinalizer]
        [HarmonyPatch("OnGrab")]
        private static void AfterOnGrab(Crosshair __state)
        {
            CrosshairRouting.Restore(__state);
        }

        [HarmonyPrefix]
        [HarmonyPatch("Update")]
        private static void BeforeUpdate(ItemBehaviour __instance, out Crosshair __state)
        {
            __state = CrosshairRouting.Swap(HolderCrosshair(__instance));
        }

        [HarmonyFinalizer]
        [HarmonyPatch("Update")]
        private static void AfterUpdate(Crosshair __state)
        {
            CrosshairRouting.Restore(__state);
        }
    }

    /// <summary>
    /// The other singleton read in the pickup path — the one that decides how far a scope
    /// actually zooms.
    ///
    /// <code>
    ///   // PlayerPickup.RpcLogic___SetObjectInHandObserver, the last line of the method
    ///   playerController.zoomFOV = itemBehaviour.aimFOV
    ///       + (!Crosshair.Instance.canScopeAim ? playerController.defaultFOV - 68f : 0f);
    /// </code>
    ///
    /// `canScopeAim` is the game's "this weapon has a real scope" flag, and a scope is meant
    /// to zoom to a fixed FOV rather than one scaled off the player's FOV slider — hence the
    /// two branches. But the read goes through the singleton, so for player 2 it answers with
    /// PLAYER 1's crosshair: whether P2's sniper zooms correctly depends on what P1 happens to
    /// be holding. It runs the other way too — P2 picking up a pistol while P1 holds a sniper
    /// gets an over-zoomed ADS.
    ///
    /// The `68f` is a hardcoded baseline that does not match `Settings.normalFovValue`, which
    /// defaults to 75, so the error is +7 at stock settings and grows with the FOV slider —
    /// at FOV 120 a scope that should reach `aimFOV` lands 52 short of it. Far enough that on
    /// light-ADS weapons it can push `playerCamera.fieldOfView` back above the reticle
    /// threshold in `Crosshair.Update` and hide the scope reticle outright, which is how this
    /// turned up while fixing that.
    ///
    /// Wrapped one level out from <see cref="ItemBehaviour_Crosshair_Patch"/> because both
    /// singleton reads — OnGrab and this one — sit in the same method fifteen lines apart, so
    /// their caller catches both.
    ///
    /// `SetObjectInHandObserver` specifically, out of the four methods FishNet generates
    /// around that RPC:
    ///
    ///  - It is hand-written, so its name has no signature hash in it. Patching
    ///    `RpcLogic___SetObjectInHandObserver_46969756` by name would break silently the first
    ///    time that signature changed.
    ///  - It is the host path. `SetObjectInHandServer` runs locally on the host and reaches the
    ///    logic through here, so every local player's grab passes this way. Remote clients
    ///    arrive via `RpcReader___` instead, but they are not ours and the assignment is
    ///    behind `IsOwner` regardless.
    ///  - Its blast radius is exactly the two reads. Nothing else the method does — parenting,
    ///    animator state, weapon wiring, ammo display — touches `Crosshair.Instance`, so
    ///    holding the swap across all of it redirects nothing extra.
    /// </summary>
    [HarmonyPatch(typeof(PlayerPickup), "SetObjectInHandObserver")]
    internal static class PlayerPickup_Crosshair_Patch
    {
        [HarmonyPrefix]
        private static void Before(PlayerPickup __instance, out Crosshair __state)
        {
            __state = CrosshairRouting.Swap(CrosshairRouting.ForPawn(__instance.gameObject));
        }

        [HarmonyFinalizer]
        private static void After(Crosshair __state)
        {
            CrosshairRouting.Restore(__state);
        }
    }
}
