using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StraftatSplitscreen
{
    /// <summary>
    /// The per-frame driver: hotkeys, roster upkeep, device assignment, and the guards that
    /// keep the game's singletons pointed at player 1.
    ///
    /// Deliberately static, with no MonoBehaviour of our own. STRAFTAT destroys BepInEx's
    /// manager GameObject shortly after the plugin loads: a component added there receives
    /// Awake and OnEnable, then OnDestroy before the first frame, so Start and Update never
    /// run and any hotkey wired to them is silently dead. Everything here is driven from
    /// Harmony patches on PauseManager.Update / LateUpdate instead, which are the game's own
    /// always-present ticks. The same constraint rules out coroutines.
    ///
    /// Every loop in here iterates the roster. There is no "the second player" step to
    /// duplicate when a third or fourth is added.
    /// </summary>
    internal static class SplitscreenCoordinator
    {
        private static bool _announced;
        private static bool _tickErrorLogged;

        internal static void Tick()
        {
            if (!_announced)
            {
                _announced = true;
                Plugin.Log.LogInfo("Tick confirmed — driven by PauseManager.Update.");
                Plugin.Log.LogInfo(
                    $"Players join with A on their own controller (ceiling = {LocalPlayers.MaxPlayers}); " +
                    $"{Plugin.RemovePlayerKey.Value} removes the last one. Gamepads connected: {Gamepad.all.Count}.");
            }

            try
            {
                TickBody();
            }
            catch (Exception ex)
            {
                if (!_tickErrorLogged)
                {
                    _tickErrorLogged = true;
                    Plugin.Log.LogError($"Coordinator tick threw (further occurrences suppressed): {ex}");
                }
            }
        }

        private static void TickBody()
        {
            if (Plugin.LogKeyPresses.Value)
                EchoKeys();

            if (KeyPressed(Plugin.RemovePlayerKey.Value))
                LocalPlayers.RemoveLast();

            if (KeyPressed(Plugin.DumpKey.Value))
            {
                PawnDump.Dump();
                RenderDiagnostics.Report();
                GrenadeDiagnostics.ReportSummary(); // TEMPORARY — see GrenadeDiagnostics
                ScopeDiagnostics.Report();          // TEMPORARY — see ScopeDiagnostics
            }

            if (KeyPressed(Plugin.InputReportKey.Value))
                InputDiagnostics.Report();

            if (KeyPressed(Plugin.TraceKey.Value))
                InputDiagnostics.ToggleTrace();

            WatchSharedStatic();
            InputDiagnostics.Trace();
            RenderDiagnostics.Watch("update");

            // TEMPORARY — see GrenadeDiagnostics. Deliberately above the SplitActive check:
            // the same interaction has to be watchable with one local player, which is the
            // only way to tell a splitscreen regression from vanilla behaviour.
            GrenadeDiagnostics.Sample(GrenadeDiagnostics.UpdatePhase);

            // TEMPORARY — see ScopeDiagnostics. Above the SplitActive check for the same
            // reason: the reticle has to be watchable with one local player, which is the
            // only way to tell a splitscreen regression from how the game already behaves.
            ScopeDiagnostics.Sample();

            // Before the split check, deliberately: this is how the split gets started in the
            // first place, from a controller nobody is holding yet.
            LobbyJoin.Tick();

            // Also before it, and for a related reason: player 1's settings mirror has to stay
            // current while they are alone, or the first player to join would inherit whatever
            // the settings happened to be the last time a split was running.
            SettingsRouting.Tick();

            // Above the split check, like the settings tick and for the same kind of reason: this
            // is the call that DISMANTLES the per-player menus and gives player 1's two back to
            // the scene when the last guest leaves. Below the check it would never run, and the
            // menus would stay parented into a quarter-screen root with nobody left to use them.
            //
            // It reads PawnControls and never writes InputManager.inputActions, so unlike the
            // reverted attempt it adds no second writer to the static the spawn swap owns.
            MenuSplit.Tick();

            if (!LocalPlayers.SplitActive)
                return;

            LocalPlayers.EnsureHostPlayer();
            AdvanceJoins();
            EnsurePawns();

            // Cheap, idempotent, and re-run every tick so that plugging a controller in
            // mid-match binds the next waiting player with no reconnect step.
            LocalPlayers.AssignDevices();

            PlayerVisibility.Tick();

            // The death banner of every player but P1 is ours to time — see DeathOverlaySplit.
            DeathOverlaySplit.Tick();
        }

        /// <summary>
        /// Arm the timeout that <see cref="AdvanceJoins"/> reports against. Called by whoever
        /// starts a join — <see cref="LobbyJoin"/>, the one path that adds a player — so that a
        /// ClientInstance which never comes up is called out rather than waited on forever.
        /// </summary>
        internal static void NoteJoinStarted()
        {
            _joinDeadline = Time.unscaledTime + 5f;
        }

        private static float _joinDeadline;

        /// <summary>
        /// Finish joining any player whose ClientInstance has come up. OnStartClient can land
        /// a tick or two after ServerManager.Spawn returns, so this is a poll rather than a
        /// callback — and it covers every pending player at once rather than one named slot.
        /// </summary>
        private static void AdvanceJoins()
        {
            bool waiting = false;

            foreach (var player in LocalPlayers.All)
            {
                if (player.Index == 0 || player.JoinAdvanced)
                    continue;

                if (player.Client == null)
                {
                    waiting = true;
                    continue;
                }

                player.JoinAdvanced = true;

                // Joining IS readying, in the lobby exactly as mid-match. A player only exists
                // because somebody pressed X on a controller to bring them in, so that press is
                // already the statement "I am in" — asking for a second one on Y only left the
                // Start button greyed out with no visible reason why. Pressing B unreadies them
                // again, so nobody is locked into a match they changed their mind about.
                player.Readied = true;
                player.Client.ServerSetPlayerReadyState(true);
                Plugin.Log.LogInfo(
                    LobbyJoin.InLobby
                        ? $"{player.Label} joined and is ready. B unreadies, B again leaves."
                        : $"{player.Label} ready (PlayerId assigned shortly after).");

                // Mid-match join: ask for a pawn now rather than waiting for the next round.
                if (player.Manager != null)
                    player.NextPawnCheck = 0f;
            }

            if (waiting && Time.unscaledTime > _joinDeadline)
            {
                Plugin.Log.LogError("A local player's ClientInstance never initialized (timeout).");
                _joinDeadline = float.MaxValue;
            }
        }

        /// <summary>
        /// Self-heal: added players are spawned by the same paths as player 1
        /// (PlayerManager.OnLoadSceneEnd on map load, ObserversRoundSpawn between rounds), but
        /// those do not always fire for them. Rather than depend on one spawn path, notice
        /// that player 1 has a pawn and someone else does not, and ask their own PlayerManager
        /// to respawn.
        /// </summary>
        private static void EnsurePawns()
        {
            var pause = PauseManager.Instance;
            if (pause == null || pause.inMainMenu)
                return;

            var host = LocalPlayers.At(0);
            if (host == null || host.Pawn == null)
                return; // the match has not really begun

            foreach (var player in LocalPlayers.All)
            {
                if (player.Index == 0 || player.Client == null)
                    continue;

                if (player.Pawn != null)
                {
                    player.PawnAttempts = 0; // in the world; re-arm for the next round
                    continue;
                }

                if (Time.unscaledTime < player.NextPawnCheck)
                    continue;
                player.NextPawnCheck = Time.unscaledTime + 2f;

                var spawner = player.Client.PlayerSpawner;
                if (spawner == null || player.PawnAttempts >= MaxPawnAttempts)
                    continue;

                player.PawnAttempts++;
                Plugin.Log.LogInfo(
                    $"{player.Label} (PlayerId={player.Client.PlayerId}) has no pawn while P1 does — " +
                    $"spawning (attempt {player.PawnAttempts}/{MaxPawnAttempts}).");

                // PopulateSpawnPoints first: the PlayerManager cached its SpawnPoint array in
                // the previous scene and those objects were destroyed by the map load. The
                // stale entries are what made TryRespawn throw.
                spawner.PopulateSpawnPoints();

                // RoundSpawn, not TryRespawn: TryRespawn routes through ReturnSpawnPoint,
                // which dereferences spawnPoint.gameObject on every entry and throws
                // MissingReference on a destroyed one, killing the ServerRpc before it spawns
                // anything. RoundSpawn reaches SpawnPlayer by index and avoids that entirely.
                spawner.RoundSpawn();

                if (player.PawnAttempts >= MaxPawnAttempts)
                    Plugin.Log.LogError(
                        $"{player.Label} still has no pawn after several attempts — giving up until " +
                        "the roster changes. Check the log above for a spawn exception.");
            }
        }

        private const int MaxPawnAttempts = 5;

        internal static void LateTick()
        {
            // TEMPORARY — see GrenadeDiagnostics. Before the SplitActive check for the same
            // reason as its Update half, and it carries its own try/catch.
            GrenadeDiagnostics.Sample(GrenadeDiagnostics.LatePhase);

            if (!LocalPlayers.SplitActive)
                return;

            try
            {
                // Safety net. If an exception ever escaped between a camera's pre-cull and its
                // post-render, some player would stay invisible until their next respawn.
                PlayerVisibility.RestoreAll();

                RenderDiagnostics.Watch("late");

                // Every frame, deliberately — see the method's own note. This is what stops
                // FirstPersonController.HandleAnimation hiding everyone's lower body.
                PlayerVisibility.ReassertBodyVisibility();

                GuardSingletons();

                // Late, deliberately: every living pawn's Update switches the kill cam off, so
                // its state has to be settled after all of them and before anything renders.
                KillCamSplit.Tick();
            }
            catch (Exception ex)
            {
                if (!_tickErrorLogged)
                {
                    _tickErrorLogged = true;
                    Plugin.Log.LogError($"Coordinator late tick threw (suppressed after this): {ex}");
                }
            }
        }

        /// <summary>
        /// PlayerSetup.Update re-captures Settings.localPlayer whenever it is null, which every
        /// round transition makes it. Any local pawn can win that race; the HUD, crosshair and
        /// kill cam follow it, and they belong to player 1.
        /// </summary>
        private static void GuardSingletons()
        {
            var host = LocalPlayers.At(0);
            var hostController = host != null ? host.Controller : null;
            if (hostController == null)
                return;

            var settings = Settings.Instance;
            if (settings != null && settings.localPlayer != null &&
                !ReferenceEquals(settings.localPlayer, hostController) &&
                LocalPlayers.OwningPawn(settings.localPlayer.gameObject) != null)
            {
                settings.localPlayer = hostController;
            }

            if (FirstPersonController.instance != null &&
                !ReferenceEquals(FirstPersonController.instance, hostController) &&
                LocalPlayers.OwningPawn(FirstPersonController.instance.gameObject) != null)
            {
                FirstPersonController.instance = hostController;
            }
        }

        /// <summary>
        /// Notice when the game swaps InputManager.inputActions out from under us.
        ///
        /// Every pawn caches the asset it saw in Awake, so a rebuild (RestartEngine, a rebind,
        /// a settings apply) silently detaches every live pawn from the static. Nothing else
        /// in the log changes when that happens, which is the kind of failure that costs an
        /// evening.
        /// </summary>
        private static void WatchSharedStatic()
        {
            var current = InputManager.inputActions;
            if (ReferenceEquals(current, _lastKnownStatic))
                return;

            string from = InputDiagnostics.AssetLabel(_lastKnownStatic != null ? _lastKnownStatic.asset : null);
            string to = InputDiagnostics.AssetLabel(current != null ? current.asset : null);
            _lastKnownStatic = current;

            if (!_staticSeen)
            {
                _staticSeen = true;
                Plugin.Log.LogInfo($"[static] InputManager.inputActions = {to}");
                return;
            }
            Plugin.Log.LogWarning(
                $"[static] InputManager.inputActions changed {from} -> {to} outside the spawn swap. " +
                "Every pawn alive right now still holds the old asset.");
        }

        private static PlayerControls _lastKnownStatic;
        private static bool _staticSeen;

        // ------------------------------------------------------------------ keys

        /// <summary>
        /// True if the key fired this frame, via legacy Input OR the new Input System. The game
        /// ships activeInputHandler=2 ("Both"); checking both costs nothing and removes a whole
        /// class of "the hotkey does nothing" report. Read straight off the keyboard device, so
        /// it keeps working while every player's action asset is pinned to a gamepad.
        /// </summary>
        private static bool KeyPressed(KeyCode wanted)
        {
            if (Input.GetKeyDown(wanted))
                return true;

            var kb = Keyboard.current;
            if (kb == null)
                return false;
            string want = wanted.ToString();
            foreach (var key in kb.allKeys)
            {
                if (key.wasPressedThisFrame && key.keyCode.ToString() == want)
                    return true;
            }
            return false;
        }

        private static void EchoKeys()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return;
            foreach (var key in kb.allKeys)
            {
                if (key.wasPressedThisFrame)
                    Plugin.Log.LogInfo($"key down: {key.keyCode}");
            }
        }
    }
}
