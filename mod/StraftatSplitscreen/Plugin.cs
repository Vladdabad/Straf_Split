using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "straftat.splitscreen";
        public const string PluginName = "STRAFTAT Splitscreen";
        public const string PluginVersion = "0.30.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> VerboseLogging;
        internal static ConfigEntry<KeyCode> RemovePlayerKey;
        internal static ConfigEntry<bool> VerticalSplit;
        internal static ConfigEntry<bool> LogKeyPresses;
        internal static ConfigEntry<KeyCode> DumpKey;
        internal static ConfigEntry<KeyCode> InputReportKey;
        internal static ConfigEntry<KeyCode> TraceKey;
        internal static ConfigEntry<float> TraceSeconds;
        internal static ConfigEntry<float> TraceInterval;
        internal static ConfigEntry<bool> TraceInputCapture;
        internal static ConfigEntry<bool> TraceBoxcast;
        internal static ConfigEntry<bool> WatchBodyVisibility;
        // TEMPORARY, with GrenadeDiagnostics.cs — delete these three with it.
        internal static ConfigEntry<bool> WatchGrenadePickup;
        internal static ConfigEntry<float> GrenadeWatchSeconds;
        internal static ConfigEntry<bool> GrenadeWatchAllPickups;
        // TEMPORARY, with ScopeDiagnostics.cs — delete these two with it.
        internal static ConfigEntry<bool> WatchScopeReticle;
        internal static ConfigEntry<float> ScopeWatchInterval;
        internal static ConfigEntry<bool> MeleeOverlapFallback;
        internal static ConfigEntry<bool> SplitKillCam;
        internal static ConfigEntry<bool> ControllerJoin;
        internal static ConfigEntry<int> ListenerPlayer;
        internal static ConfigEntry<bool> HearOwnSounds;
        internal static ConfigEntry<bool> PerPlayerSettings;
        internal static ConfigEntry<bool> SplitMenus;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            RemovePlayerKey = Config.Bind(
                "Splitscreen", "RemovePlayerKey", KeyCode.F8,
                "Emergency exit: remove the most recently added local player from the keyboard. Players normally leave on their own controller with B.");
            VerticalSplit = Config.Bind(
                "Splitscreen", "VerticalSplit", false,
                "Two-player layout only: false = stacked (P1 top / P2 bottom), true = side by side. Three and four players always use quadrants.");

            MeleeOverlapFallback = Config.Bind(
                "Splitscreen", "MeleeOverlapFallback", true,
                "Detect melee hits on other local players with an overlap test as well as the game's physics contact. The contact path may not fire at all between local pawns (the blade is a kinematic Rigidbody and pawns have no Rigidbody), so this is what makes melee land. Both paths share a per-swing victim list, so a hit is never applied twice. Turn off if melee ever double-damages.");

            ControllerJoin = Config.Bind(
                "Splitscreen", "ControllerJoin", true,
                "Join, ready up and leave from the controller itself, on the lobby screen. This is the only way to add a local player. X on a controller nobody is holding claims a player - the first player without a controller, or a new one if everybody already has theirs - and a player brought in that way is readied up the moment their instance comes up. X on a controller that already has a player readies that player up, which is how everyone readies again after a match (the game unreadies all of them on the way back to the menu); Y does the same. B unreadies, and B again while unready drops that player out of the lobby. X rather than A because A is the menu's own confirm button, and joining on it also pressed whatever the lobby had selected. Turning this off leaves no way to add local players at all.");

            SplitKillCam = Config.Bind(
                "Splitscreen", "SplitKillCam", true,
                "Draw the death view (the kill cam) only in the dead player's own slice of the screen, and hold it on while they are dead - the game otherwise gives it the whole screen and every living local player switches it back off. Additional dead players get a mirror of it in their own slice. Turn off to leave the kill cam exactly as the game handles it.");

            ListenerPlayer = Config.Bind(
                "Audio", "ListenerPlayer", 1,
                "Which player's ears the world is heard from. Unity mixes for exactly one AudioListener, so world audio - doors, props, remote players - is positioned relative to this player only. That player keeps fully directional audio; the others get their own sounds in 2D so they can hear themselves.");
            HearOwnSounds = Config.Bind(
                "Audio", "HearOwnSounds", true,
                "Play each non-listener player's own pawn sounds (footsteps, landings, near-miss whip-cracks) in 2D so they hear themselves at full volume. This is the rule the game already applies to your own weapon. Turn off to leave every pawn fully 3D, in which case players other than ListenerPlayer will barely hear themselves.");

            PerPlayerSettings = Config.Bind(
                "Splitscreen", "PerPlayerSettings", true,
                "Give each local player their own sensitivity, invert, hold/toggle binds, FOV, brightness, damage flash and crosshair. Whoever opens the options screen is the one editing it - press Start on your own controller in a match, or open Options from the menu on it - and their settings are saved separately, so four people can share one screen without sharing one aim. Everything that belongs to the machine rather than to a person (resolution, fullscreen, vsync, quality, frame cap, volumes, voice chat) stays global. Player 1's settings are still the game's own, in the game's own prefs keys, so turning this off - or uninstalling - loses nothing.");

            SplitMenus = Config.Bind(
                "Splitscreen", "SplitMenus", true,
                "Give each local player their own pause and options menu, inside their own slice of the screen and navigated on their own controller. Press Start and the menu opens where you are sitting, showing your settings - four people can be in their menus at once without fighting over one cursor. Turn off to go back to the game's single full-screen menu, which whoever presses Start takes over; per-player settings still follow whoever opened it.");

            VerboseLogging = Config.Bind(
                "Debug", "VerboseLogging", true,
                "Log player/camera setup details as each player spawns.");
            DumpKey = Config.Bind(
                "Debug", "DumpKey", KeyCode.F10,
                "Dump every local pawn's renderers, colliders and camera state to the log. Take one mid-match when something looks wrong.");
            // Default off: the hotkey path is confirmed working, and the spam drowns the traces.
            LogKeyPresses = Config.Bind(
                "Debug", "LogKeyPresses", false,
                "Log every key as it is pressed. Diagnostic for 'the hotkey does nothing' - noisy during play, and it drowns the input trace.");
            InputReportKey = Config.Bind(
                "Debug", "InputReportKey", KeyCode.F11,
                "One-shot input forensics: which action asset every pawn component is actually holding, which physical controls each action resolved to, and who is subscribed to the look events. Take one while all players are alive.");
            TraceKey = Config.Bind(
                "Debug", "InputTraceKey", KeyCode.F12,
                "Toggle the live input trace: per-frame input value next to how far that pawn really moved. Move one player at a time while it runs - it tells apart 'several pawns hear one device' from 'only one hears it but several move'.");
            TraceSeconds = Config.Bind(
                "Debug", "InputTraceSeconds", 15f,
                "The trace stops itself after this long, so a forgotten toggle cannot fill the log.");
            TraceInterval = Config.Bind(
                "Debug", "InputTraceInterval", 0.1f,
                "Minimum seconds between trace lines. 0 logs every frame - readable only in very short bursts.");
            TraceInputCapture = Config.Bind(
                "Debug", "TraceInputCapture", true,
                "Log which action asset each pawn component captures in its Awake, and what the shared static held at that instant. This is what proves whether a player's control swap covers every component.");
            TraceBoxcast = Config.Bind(
                "Debug", "TraceBoxcast", true,
                "Log why a boxcast weapon's shot did or did not find another local player - the spray/flamethrower path (Phoenix, BlankState, Bayshore). Prints the shooter, every collider the overlap box sees, and which layer each player's body is on at that instant: 11 means the hit-layer swap reached them, 16 means it did not. Rate-limited to two lines a second.");
            WatchBodyVisibility = Config.Bind(
                "Debug", "WatchBodyVisibility", true,
                "Watch the third-person body meshes every frame and log the moment any of them changes activation, renderer state, layer or material. Silent until something actually changes.");

            // ---- TEMPORARY: the grenade-pickup visibility hunt. Delete these three binds,
            // the log line below, GrenadeDiagnostics.cs and Patches/GrenadeWatchPatches.cs
            // once the bug is fixed.
            WatchGrenadePickup = Config.Bind(
                "Debug", "WatchGrenadePickup", true,
                "Hunt for the object that becomes visible when a grenade is picked up. Keeps a per-frame picture of every renderer and light in the scene (activation, enabled, layer, parent, material) and prints the ones that change during a short window around each item interaction - pickup, drop, pin pull. Silent the rest of the time. Costs a scene-wide scan per frame while that window is open, so turn it off once the bug is found.");
            GrenadeWatchSeconds = Config.Bind(
                "Debug", "GrenadeWatchSeconds", 4f,
                "How long the grenade watch keeps printing after an interaction. Each new interaction restarts the clock, so holding a grenade and throwing it is one continuous window.");
            GrenadeWatchAllPickups = Config.Bind(
                "Debug", "GrenadeWatchAllPickups", true,
                "Watch every item pickup, not only grenades. On by default because the control group is the point: whatever changes for a grenade AND for a pistol is not the bug.");

            // ---- TEMPORARY: the missing scope-reticle hunt. Delete these two binds, the log
            // line below, ScopeDiagnostics.cs and Patches/ScopeWatchPatches.cs once the bug
            // is fixed.
            WatchScopeReticle = Config.Bind(
                "Debug", "WatchScopeReticle", true,
                "Explain, for every player holding a scoped weapon (M2000, HKG-11, ...), why the scope reticle is or is not on screen. Crosshair.Update gates that sprite behind six terms; this names the first one that is false, and separately measures whether the scope image has any area left to draw into after the viewport re-anchoring. Silent for unscoped weapons, and it prints on change rather than per frame.");
            ScopeWatchInterval = Config.Bind(
                "Debug", "ScopeWatchInterval", 1f,
                "Seconds between repeat lines while a player's scope state has not changed. The heartbeat is the point: a reticle that is stuck hidden otherwise prints once and then looks the same as nothing happening. Raise it if the log is too busy.");

            // Before PatchAll: the settings patches no-op when the table does not check out,
            // and that verdict has to exist before the first frame can reach them.
            SettingTable.Verify();

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);

            // No MonoBehaviour of our own: STRAFTAT destroys BepInEx's manager GameObject
            // shortly after load, so anything we attach there gets Awake/OnEnable and then
            // OnDestroy before the first frame - Start and Update never run. The coordinator
            // is static and ticks off PauseManager.Update/LateUpdate instead.
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded - {_harmony.GetPatchedMethods().Count()} method(s) patched.");
            Log.LogInfo(
                $"Local players (max {LocalPlayers.MaxPlayers}) join from their own controller; connect one per player. " +
                $"{RemovePlayerKey.Value} removes the last one from the keyboard if a controller cannot.");
            if (ControllerJoin.Value)
                Log.LogInfo(
                    "In the lobby: X on a spare controller joins and readies up (the first press claims P1's own " +
                    "controller), X again readies that controller's player - which is what you want after a match, " +
                    "since the game unreadies everyone on the way back to the menu. B unreadies, B again leaves. " +
                    "X, not A: A is the menu's own confirm and would open whatever the lobby had selected.");
            else
                Log.LogWarning(
                    "ControllerJoin is off - there is no other way to add a local player, so the mod will do nothing.");
            if (PerPlayerSettings.Value)
                Log.LogInfo(
                    "Settings belong to whoever opens them: press Start on your own controller (or open " +
                    "Options from the menu on it) and the sensitivity, invert, FOV and crosshair sliders " +
                    "edit YOUR player. Display, audio and voice settings stay shared.");
            Log.LogInfo($"Diagnostics: {DumpKey.Value} = render dump, {InputReportKey.Value} = input report, {TraceKey.Value} = live input trace.");
            if (WatchGrenadePickup.Value)
                Log.LogInfo(
                    $"[gwatch] Grenade-pickup visibility watch is ON. Pick a grenade up and look for " +
                    $"'BECAME DRAWABLE' in the [gwatch/...] lines; {DumpKey.Value} prints the running summary. " +
                    "Temporary - remove GrenadeDiagnostics.cs when the bug is fixed.");
            if (WatchScopeReticle.Value)
                Log.LogInfo(
                    $"[swatch] Scope-reticle watch is ON. Aim a scoped rifle and read the [swatch] lines: " +
                    $"'HIDDEN: <term>' names the condition that failed, 'DRAWN BUT INVISIBLE' means the sprite is " +
                    $"right but the image has no area. {DumpKey.Value} prints the running summary. " +
                    "Temporary - remove ScopeDiagnostics.cs when the bug is fixed.");
        }

        // NO OnDestroy/UnpatchSelf here, deliberately.
        //
        // STRAFTAT destroys BepInEx's manager GameObject shortly after load, which destroys
        // this plugin component with it. An OnDestroy that called _harmony.UnpatchSelf()
        // therefore ripped every patch back out a fraction of a second after applying it -
        // the mod logged "6 method(s) patched" and then silently did nothing at all, because
        // by the first frame there were no patches left. Harmony patches are global and
        // outlive the object that installed them, so leaving them in place is both correct
        // and necessary. The game unloads them when the process exits.
    }
}
