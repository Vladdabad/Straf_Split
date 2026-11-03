using FishNet;
using FishNet.Object.Synchronizing;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Gives every local player their own vote on the end screen's REMATCH button.
    ///
    /// The button is a world-space interactable: each player walks up to it and presses their
    /// own Interact, and `PlayerPickup` hands the pawn's transform to
    /// `VictoryMenu.OnInteract(Transform player)`. That transform is the only thing in the
    /// whole path that knows WHICH local player pressed it — and vanilla throws it away:
    ///
    /// <code>
    ///   // VictoryMenu.OnInteract
    ///   if (this.restartGame) this.VoteRematchServer(null);   // the pawn is not passed on
    ///
    ///   // VictoryMenu.RpcLogic___VoteRematchServer, server side
    ///   foreach (var ci in ClientInstance.playerInstances.Values)
    ///       if (ci.Owner == sender) { clientInstance = ci; break; }   // FIRST one on the connection
    ///   if (VotedPlayers.Contains(playerId)) { VotedPlayers.Remove(playerId); return; }
    ///   VotedPlayers.Add(playerId);
    /// </code>
    ///
    /// The voter is resolved from the RPC's <em>connection</em>, and every local player shares
    /// the host's connection, so the loop always stops on the same ClientInstance — P1's. The
    /// vote is a toggle, so P2 pressing REMATCH does not cast their own vote, it CANCELS P1's;
    /// P3 casts it again, P4 cancels it again. With four players the count never rises above
    /// one and the rematch can never reach its threshold.
    ///
    /// This resolves the voter from the pawn instead, and toggles that player's own PlayerId.
    /// Only the rematch button is intercepted: RETURN TO MENU and QUIT are session-wide by
    /// nature — one player pressing them ends the match for everybody, which is what they are
    /// meant to do — and the playlist buttons carry no per-player state at all.
    ///
    /// Doing it inline rather than through the RPC is not a shortcut: the ServerRpc has nowhere
    /// to carry a player id (its only parameter is the connection FishNet fills in), and we are
    /// the server here anyway, so this writes to the same SyncList the RPC's own server-side
    /// logic writes to, and syncs to remote clients identically. When we are not the server, or
    /// the pawn does not belong to a local player, vanilla runs untouched.
    ///
    /// One rough edge left alone deliberately: `PlayerPickup.HandleInteractEnvironment` fires a
    /// second interact path from `Input.GetMouseButtonDown(0)`, which is legacy input and so is
    /// global — a mouse click makes every local pawn currently facing a button interact at
    /// once. With per-player votes that now toggles each of their own votes rather than
    /// fighting over one, which is merely odd instead of broken. Suppressing it for players 2+
    /// would mean skipping the whole method, and that same method is what assigns
    /// `currentEnvironmentInteractable` for their proper Interact-action path.
    /// </summary>
    [HarmonyPatch(typeof(VictoryMenu), nameof(VictoryMenu.OnInteract))]
    internal static class VictoryMenu_OnInteract_Patch
    {
        private static readonly AccessTools.FieldRef<VictoryMenu, bool> RestartGameRef =
            AccessTools.FieldRefAccess<VictoryMenu, bool>("restartGame");

        private static readonly System.Reflection.FieldInfo VotedPlayersField =
            AccessTools.Field(typeof(VictoryMenu), "VotedPlayers");

        private static bool _warned;

        private static bool Prefix(VictoryMenu __instance, Transform player)
        {
            if (!LocalPlayers.SplitActive)
                return true;

            // Every other button on this screen is session-wide; only the vote is per-player.
            if (!RestartGameRef(__instance))
                return true;

            var voter = LocalPlayers.OwningPawn(player != null ? player.gameObject : null);
            if (voter == null || voter.Client == null)
                return true;

            var nm = InstanceFinder.NetworkManager;
            if (nm == null || !nm.IsServer)
                return true;

            var voted = VotedPlayersField != null
                ? VotedPlayersField.GetValue(__instance) as SyncList<int>
                : null;
            if (voted == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Plugin.Log.LogWarning(
                        "[rematch] VictoryMenu.VotedPlayers could not be read — falling back to the " +
                        "game's own vote, which credits every local player's press to P1. The field " +
                        "has been renamed since this was written.");
                }
                return true;
            }

            int id = voter.Client.PlayerId;
            if (voted.Contains(id))
            {
                voted.Remove(id);
                Plugin.Log.LogInfo($"[rematch] {voter.Label} withdrew their rematch vote ({voted.Count} in favour).");
            }
            else
            {
                voted.Add(id);
                Plugin.Log.LogInfo($"[rematch] {voter.Label} voted for a rematch ({voted.Count} in favour).");
            }

            return false;
        }
    }
}
