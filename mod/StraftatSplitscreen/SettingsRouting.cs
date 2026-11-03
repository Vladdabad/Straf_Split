using UnityEngine;
using UnityEngine.InputSystem;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Who the options screen is editing, and how one global <see cref="Settings"/> singleton
    /// serves four people.
    ///
    /// The game keeps every option in one place and every consumer reads it from there:
    /// <c>Settings.Instance.mouseSensitivity</c>, <c>Settings.Instance.normalFovValue</c>, and so
    /// on, scattered across the pawn, the crosshair, the damage flash and the weapon. Making
    /// those per-player by finding and rewriting each read would mean patching a dozen methods
    /// with a dozen different fixes, and missing the next one the game adds.
    ///
    /// So the singleton is *borrowed* instead. There is one invariant:
    ///
    ///     At rest, Settings.Instance holds player 1's values.
    ///
    /// and one exception to it: for the duration of a single call that is running on some other
    /// player's behalf — their pawn's Update, their crosshair's Update, their damage flash, or
    /// an options handler they triggered — the singleton is loaded with *their* values and put
    /// back afterwards. The game's own code then reads the right numbers without knowing anyone
    /// is there, which is the same trick the mod already plays with <c>Settings.localPlayer</c>
    /// and <c>Crosshair.player</c> around <c>PlayerSetup.OnStartClient</c>.
    ///
    /// The borrow is always bounded by one method call and never spans a frame, so nothing can
    /// observe the singleton mid-borrow except the code that is inside it. <see cref="Tick"/>
    /// re-asserts the invariant every frame and complains loudly if it ever finds it broken.
    ///
    /// Player 1 is not special-cased: they have a <see cref="PlayerSettings"/> like everyone
    /// else. It just happens that theirs is the copy the singleton already holds, so borrowing
    /// for them is a no-op and their edits go down the game's own untouched path.
    /// </summary>
    internal static class SettingsRouting
    {
        /// <summary>
        /// A borrow in progress. Handed from a Harmony prefix to its finalizer as
        /// <c>__state</c>, so it is a struct and allocates nothing on the per-frame paths.
        /// </summary>
        internal struct Borrowed
        {
            internal LocalPlayer Player;
            internal bool SwappedLocalPlayer;
            internal FirstPersonController SavedLocalPlayer;

            internal bool Active => Player != null;
        }

        private static int _depth;

        /// <summary>
        /// The local player whose settings the options screen is currently editing — the one who
        /// opened it. Latched on open and held until it closes, deliberately: there is one
        /// EventSystem and one cursor, so only one person is driving the menu anyway, and
        /// re-deciding on every stick flick would let a bored player two seats over steal an
        /// edit halfway through.
        /// </summary>
        internal static LocalPlayer Owner { get; private set; }

        // ------------------------------------------------------------------ borrow

        /// <summary>
        /// Load <paramref name="player"/>'s values into the singleton for the duration of one
        /// call. Always paired with <see cref="Return"/> — from a Harmony *finalizer*, not a
        /// postfix, so that an exception thrown by the game's own code cannot leave someone
        /// else's numbers sitting in the singleton.
        /// </summary>
        /// <param name="swapLocalPlayer">
        /// Also point <c>Settings.localPlayer</c> at this player's pawn. Wanted for the options
        /// handlers, some of which reach through it to touch the pawn (minimalist UI, motion
        /// blur); not wanted on the per-frame read paths, where it would fight
        /// <c>SplitscreenCoordinator.GuardSingletons</c> for no gain.
        /// </param>
        internal static Borrowed Borrow(LocalPlayer player, bool swapLocalPlayer)
        {
            var settings = Settings.Instance;
            if (settings == null || player == null || player.Index == 0 || !Enabled)
                return default(Borrowed);

            var host = LocalPlayers.At(0);
            if (host == null)
                return default(Borrowed);

            // Touch the host's settings before overwriting the singleton. They are mirrored from
            // it on first access, and materialising them mid-borrow would capture the borrower's
            // values as player 1's — permanently.
            var hostSettings = host.Settings;
            if (hostSettings == null)
                return default(Borrowed);

            var borrowed = new Borrowed { Player = player };
            if (swapLocalPlayer)
            {
                borrowed.SwappedLocalPlayer = true;
                borrowed.SavedLocalPlayer = settings.localPlayer;
                settings.localPlayer = player.Controller;
            }

            player.Settings.Push(settings);
            _depth++;
            return borrowed;
        }

        /// <summary>Put the singleton back. Safe to call with an inactive borrow, and safe to nest.</summary>
        internal static void Return(Borrowed borrowed)
        {
            if (!borrowed.Active)
                return;

            var settings = Settings.Instance;
            if (settings == null)
            {
                _depth = 0;
                return;
            }

            _depth--;
            if (_depth <= 0)
            {
                _depth = 0;
                var host = LocalPlayers.At(0);
                if (host != null)
                    host.Settings.Push(settings);
            }

            if (borrowed.SwappedLocalPlayer)
                settings.localPlayer = borrowed.SavedLocalPlayer;
        }

        /// <summary>
        /// End an options-handler borrow: keep whatever the game's own handler just wrote, file
        /// it under the borrower's keys, and undo the collateral damage.
        ///
        /// The collateral damage is real and easy to miss. Every <c>Settings.Change*</c> handler
        /// ends with <c>PlayerPrefs.SetFloat("mouseSensitivity", …)</c> — the *vanilla* key —
        /// so running one on player 3's behalf writes player 3's number into player 1's saved
        /// settings. Re-saving player 1 immediately afterwards puts that right.
        /// </summary>
        internal static void Finish(Borrowed borrowed)
        {
            if (!borrowed.Active)
                return;

            var settings = Settings.Instance;
            var player = borrowed.Player;

            if (settings != null)
            {
                player.Settings.Pull(settings);
                player.Settings.Save();
            }

            Return(borrowed);

            var host = LocalPlayers.At(0);
            if (host != null)
                host.Settings.Save();
            PlayerPrefs.Save();

            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo($"[settings] {player.Label}: {player.Settings.Describe()}");
        }

        // ------------------------------------------------------------------ tick

        internal static bool Enabled => Plugin.PerPlayerSettings.Value && SettingTable.Ok;

        private static InputDevice _lastDevice;
        private static bool _menuWasOpen;
        private static bool _brokenReported;

        /// <summary>
        /// Re-assert the invariant, follow whoever is driving the menu, and hand the options
        /// screen to them when it opens.
        ///
        /// Runs before the coordinator's split check, deliberately: player 1's mirror has to
        /// stay current while there is only one of them, or the first player to join would
        /// inherit whatever the settings were the last time a split was running.
        /// </summary>
        internal static void Tick()
        {
            if (!Enabled)
                return;

            var settings = Settings.Instance;
            var host = LocalPlayers.At(0);
            if (settings == null || host == null)
                return;

            if (_depth != 0)
            {
                // A borrow escaped its finalizer. Say so once — silently re-syncing would copy
                // somebody else's values onto player 1 and look like the settings "drifting".
                if (!_brokenReported)
                {
                    _brokenReported = true;
                    Plugin.Log.LogError(
                        $"[settings] a settings borrow was still open at the frame boundary (depth {_depth}). " +
                        "Player 1's mirror is being restored rather than re-read; the values the singleton " +
                        "holds right now belong to somebody else.");
                }
                _depth = 0;
                host.Settings.Push(settings);
                return;
            }

            // The invariant: at rest the singleton IS player 1's settings, so this is how their
            // own edits — which run down the game's untouched path — get picked up.
            host.Settings.Pull(settings);

            if (!LocalPlayers.SplitActive)
            {
                Owner = host;
                _menuWasOpen = false;
                return;
            }

            NoteActuatedDevice();

            bool open = SettingsUi.IsOpen;
            if (open && !_menuWasOpen)
                Latch(WhoIsDriving() ?? host);
            _menuWasOpen = open;
        }

        /// <summary>
        /// Hand the options screen to a player: remember them, and repaint every widget with
        /// their values so the sliders read what they are about to edit rather than what the
        /// last person left behind.
        /// </summary>
        internal static void Latch(LocalPlayer player)
        {
            if (player == null)
                return;
            Owner = player;
            Plugin.Log.LogInfo($"[settings] the options screen belongs to {player.Label} — {player.Settings.Describe()}");
            SettingsUi.Refresh(player);
        }

        /// <summary>
        /// Called from the <c>PauseManager.Menu</c> prefix, where the device that pressed Start
        /// is right there in the callback context. This is the precise answer to "who opened
        /// the menu"; <see cref="WhoIsDriving"/> is the fallback for the main menu, which has no
        /// such moment.
        /// </summary>
        internal static void NoteMenuPress(InputDevice device)
        {
            if (!Enabled || !LocalPlayers.SplitActive)
                return;
            if (device != null)
                _lastDevice = device;
            var player = WhoIsDriving();
            if (player != null)
                Latch(player);

            // Suppress the open-edge latch that is about to follow. The press that brought us
            // here names its own device, which is a better answer than the last-device guess the
            // edge would make — and the guess can differ, because a gamepad pressed anywhere in
            // the same frame outranks the keyboard in that scan.
            _menuWasOpen = true;
        }

        private static bool _closedEditReported;

        /// <summary>
        /// An options handler fired while no settings screen appeared to be open.
        ///
        /// Expected once at startup — <c>DisplayLoadValues</c> fills the widgets from a coroutine
        /// and every assignment fires the handler — and that one is harmless, because there is no
        /// split yet and the values it writes are player 1's own. Repeatedly, during play, it
        /// means <see cref="SettingsUi.IsOpen"/> has stopped recognising the screen, and every
        /// edit is silently going to player 1. That is worth one line in the log rather than a
        /// puzzled evening.
        /// </summary>
        internal static void NoteEditWhileClosed()
        {
            if (_closedEditReported)
                return;
            _closedEditReported = true;
            Plugin.Log.LogInfo(
                "[settings] an option changed while no settings screen looked open — not routed to " +
                $"{(Owner != null ? Owner.Label : "anyone")}. Expected once as the menu fills itself " +
                "in; if settings stop following the player who opened them, this is why.");
        }

        /// <summary>
        /// The player behind the most recent button press. A controller belongs to whoever
        /// claimed it in the lobby; the keyboard and mouse belong to player 1, who is the host
        /// sitting at the machine.
        /// </summary>
        private static LocalPlayer WhoIsDriving()
        {
            if (_lastDevice == null)
                return null;
            var holder = LocalPlayers.Holder(_lastDevice);
            if (holder != null)
                return holder;
            if (_lastDevice is Keyboard || _lastDevice is Mouse)
                return LocalPlayers.At(0);
            return null;
        }

        /// <summary>
        /// Track the last device anyone actually used. Read straight off the devices rather than
        /// through an action asset, for the same reason the lobby join does: every player's
        /// asset is pinned to their own pad, and asking a question through one would disturb it.
        /// </summary>
        private static void NoteActuatedDevice()
        {
            foreach (var pad in Gamepad.all)
            {
                if (pad.wasUpdatedThisFrame && ActuatedThisFrame(pad))
                {
                    _lastDevice = pad;
                    return;
                }
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.anyKey.wasPressedThisFrame)
            {
                _lastDevice = keyboard;
                return;
            }

            var mouse = Mouse.current;
            if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                _lastDevice = mouse;
        }

        /// <summary>
        /// A button press — face buttons, shoulders, Start, and the d-pad, whose directions are
        /// buttons too.
        ///
        /// Presses only, deliberately: no stick reading. A stick is analogue and rests near but
        /// not at zero, so any threshold either misses a gentle push or lets a drifting
        /// controller claim the menu from across the room. Reaching the options screen at all
        /// takes a button press, so presses are enough to know whose hands are on it.
        /// </summary>
        private static bool ActuatedThisFrame(Gamepad pad)
        {
            var controls = pad.allControls;
            for (int i = 0; i < controls.Count; i++)
            {
                var button = controls[i] as UnityEngine.InputSystem.Controls.ButtonControl;
                if (button != null && button.wasPressedThisFrame)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Give every local player their look settings, every frame, the way the game gives them
        /// to player 1. Cheap enough to do unconditionally, and doing it unconditionally is what
        /// makes a mid-match join, a respawn and a round transition all pick the right values up
        /// with no event to hook.
        /// </summary>
        internal static void ApplyToPawns()
        {
            if (!Enabled || !LocalPlayers.SplitActive)
                return;
            foreach (var player in LocalPlayers.All)
                player.Settings.ApplyTo(player.Controller);
        }
    }
}
