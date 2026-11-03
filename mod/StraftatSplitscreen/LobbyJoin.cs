using FishNet;
using UnityEngine.InputSystem;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Joining, readying and leaving from the controller itself, on the lobby screen. This is
    /// the only way a local player is added — there is no keyboard hotkey for it, because a
    /// player who has not pressed anything on a controller has nothing to play with.
    ///
    ///     X  (west)   an unclaimed controller claims a player — the first local player who has
    ///                 no controller yet, or a brand new one if everybody already has theirs.
    ///                 A player brought in this way is readied up as soon as their
    ///                 ClientInstance comes up (SplitscreenCoordinator.AdvanceJoins).
    ///                 On a controller that already has a player, X readies that player up
    ///     Y  (north)  the same as X on a claimed controller: ready up
    ///     B  (east)   ready -> unready; already unready -> that player leaves the lobby
    ///
    /// X rather than A, deliberately. A is the UI submit the game's own EventSystem listens for,
    /// so joining on A also pressed whatever the lobby had selected — player 1's customization
    /// screen, in practice. There is no clean way to read a button "before" the UI does: our
    /// press and its press are the same press. Moving to a face button the menu does not use is
    /// the fix, and it costs nothing, because these buttons are ours to name.
    ///
    /// X is "yes" at every stage of the screen and B is "back", which is what the trip home
    /// from a match needs: everyone is still on the roster holding the pad they claimed, so
    /// there is no unheld controller left for an X press to mean anything else, and the ready
    /// state the match cleared has to be reachable from the controller that lost it.
    ///
    /// The claim is what makes this work as one rule rather than two. Player 1 exists before
    /// anyone presses anything, but starts with no controller of their own — until a split is
    /// running, they play on everything, keyboard included. So the first X press binds *them*,
    /// and only once every player on the roster holds a controller does an X press mean "make
    /// room for someone new". One rule, no separate host case, and a host with a single
    /// controller cannot accidentally hand it to a second player and lock themselves out.
    ///
    /// Buttons are read straight off the <see cref="Gamepad"/> rather than through an action
    /// asset, for the same reason the mod's hotkeys read the keyboard directly: a controller
    /// nobody has claimed has no action asset to read, and the claimed ones are pinned to their
    /// own player's asset, which we must not disturb to ask a question.
    ///
    /// Readiness itself is the game's: `ClientInstance.ServerSetPlayerReadyState`, the same call
    /// behind the lobby's Ready button. `LobbyController.UpdatePlayerList` counts every
    /// ClientInstance, added ones included, and requires all of them ready before Start lights
    /// up — which is why joining readies a player outright. P1 is the exception, and only
    /// because they were never added: the host readies themselves with X on their own pad, or
    /// from the lobby's Ready button.
    /// </summary>
    internal static class LobbyJoin
    {
        /// <summary>
        /// True on the lobby screen of a game we are hosting: the menu scene, with a live local
        /// ClientInstance. Everything here is gated on it — joining mid-match has its own path,
        /// and B during a match would drop a player out of a round they are playing.
        /// </summary>
        internal static bool InLobby
        {
            get
            {
                var pause = PauseManager.Instance;
                if (pause == null || !pause.inMainMenu)
                    return false;
                var nm = InstanceFinder.NetworkManager;
                if (nm == null || !nm.IsHost)
                    return false;
                var lobby = LobbyController.Instance;
                return lobby != null && lobby.LocalPlayerController != null;
            }
        }

        private static bool _fullWarned;

        internal static void Tick()
        {
            if (!Plugin.ControllerJoin.Value || !InLobby)
            {
                _fullWarned = false;
                return;
            }

            // Cheap and idempotent, and this is the one path that runs before a split exists.
            if (!LocalPlayers.EnsureHostPlayer())
                return;

            foreach (var pad in Gamepad.all)
            {
                var owner = LocalPlayers.Holder(pad);

                if (owner == null)
                {
                    if (pad.buttonWest.wasPressedThisFrame)
                        Claim(pad);
                    continue;
                }

                // X on a pad that already has a player readies that player up. It is the same
                // button that brought them in, and the reason it has to work again is the trip
                // back to the menu after a match: everyone is still on the roster holding their
                // own pad, so no press is a claim any more, and the ready state the match reset
                // has to be given back some way. Y does the same, for anyone used to it.
                if (pad.buttonWest.wasPressedThisFrame || pad.buttonNorth.wasPressedThisFrame)
                    SetReady(owner, true);
                else if (pad.buttonEast.wasPressedThisFrame)
                    Back(owner);
            }
        }

        /// <summary>
        /// X pressed on a controller nobody holds: give it to whoever is waiting for one, or
        /// bring in a new player to hold it.
        /// </summary>
        private static void Claim(Gamepad pad)
        {
            var waiting = LocalPlayers.FirstUnbound();
            if (waiting != null)
            {
                waiting.Device = pad;
                waiting.ApplyDeviceFilter();
                _fullWarned = false;
                Plugin.Log.LogInfo(
                    $"[lobby] {waiting.Label} is on '{pad.displayName}'. " +
                    "X to ready up, B to unready.");
                // Deliberately not readied here: this branch also covers P1 and anyone who
                // lost their pad, neither of whom is joining. AdvanceJoins readies the ones
                // who are.
                return;
            }

            if (LocalPlayers.Count >= LocalPlayers.MaxPlayers)
            {
                if (!_fullWarned)
                {
                    _fullWarned = true;
                    Plugin.Log.LogWarning(
                        $"[lobby] '{pad.displayName}' pressed X but the roster is at the " +
                        $"{LocalPlayers.MaxPlayers}-player ceiling.");
                }
                return;
            }

            string error;
            if (!LocalPlayers.TryAdd(out error))
            {
                Plugin.Log.LogWarning($"[lobby] '{pad.displayName}' pressed X but a player could not be added: {error}");
                return;
            }

            // The spawn is asynchronous — the ClientInstance lands a tick or two later, and
            // readying up needs it. Arm the timeout so a join that never completes is reported.
            SplitscreenCoordinator.NoteJoinStarted();

            // Claim the controller for the player it just brought in, before AssignDevices can
            // hand them whichever pad happens to be spare — this pad is the one that asked.
            var joined = LocalPlayers.At(LocalPlayers.Count - 1);
            if (joined != null)
            {
                joined.Device = pad;
                Plugin.Log.LogInfo(
                    $"[lobby] {joined.Label} joined on '{pad.displayName}' — readying up. B unreadies, B again leaves.");
            }
        }

        /// <summary>B: step back one stage — ready to unready, unready out of the lobby.</summary>
        private static void Back(LocalPlayer player)
        {
            if (IsReady(player))
            {
                SetReady(player, false);
                return;
            }

            if (player.Index == 0)
            {
                // The host's own player is the lobby. Leaving it is the menu's job, not a
                // button we can honour without tearing the session down for everybody.
                Plugin.Log.LogInfo("[lobby] P1 is the host — leave the lobby from the menu.");
                return;
            }

            Plugin.Log.LogInfo($"[lobby] {player.Label} left the lobby.");
            LocalPlayers.Remove(player);
        }

        private static void SetReady(LocalPlayer player, bool ready)
        {
            var client = player.Client;
            if (client == null)
            {
                Plugin.Log.LogWarning($"[lobby] {player.Label} is still joining — cannot change their ready state yet.");
                return;
            }
            if (client.Ready == ready)
            {
                // Not silent: "I pressed X and nothing happened" is exactly the report this
                // path exists to answer, and the game's own state is the only honest answer.
                Plugin.Log.LogInfo($"[lobby] {player.Label} is already {(ready ? "ready" : "not ready")}.");
                return;
            }

            client.ServerSetPlayerReadyState(ready);
            player.Readied = ready;
            Plugin.Log.LogInfo($"[lobby] {player.Label} is {(ready ? "ready" : "not ready")}.");
        }

        private static bool IsReady(LocalPlayer player)
        {
            return player.Client != null && player.Client.Ready;
        }
    }
}
