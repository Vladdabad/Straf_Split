using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// ClientInstance.OnStartClient's owner branch installs itself as THE local player
    /// (ClientInstance.Instance, LobbyController.LocalPlayerController). It runs for every
    /// added player too — same connection, so IsOwner is true. Save the singletons, let the
    /// method run (spawn-point setup, PlayerSpawner wiring — all per-instance and wanted),
    /// restore.
    ///
    /// Player 1 is exempt because for them the branch is doing the right thing; that is the
    /// only difference, and it is one index test rather than a separate code path.
    /// </summary>
    [HarmonyPatch(typeof(ClientInstance), nameof(ClientInstance.OnStartClient))]
    internal static class ClientInstance_OnStartClient_Patch
    {
        internal struct State
        {
            public int Index;
            public ClientInstance Instance;
            public ClientInstance LocalPlayerController;
        }

        private static void Prefix(ClientInstance __instance, out State __state)
        {
            __state = default;
            var marker = __instance.GetComponent<LocalPlayerMarker>();
            __state.Index = marker != null ? marker.Index : 0;
            if (__state.Index == 0)
                return;

            __state.Instance = ClientInstance.Instance;
            __state.LocalPlayerController =
                LobbyController.Instance != null ? LobbyController.Instance.LocalPlayerController : null;
        }

        private static void Postfix(ClientInstance __instance, State __state)
        {
            if (__state.Index == 0)
                return;

            ClientInstance.Instance = __state.Instance;
            if (LobbyController.Instance != null)
                LobbyController.Instance.LocalPlayerController = __state.LocalPlayerController;

            LocalPlayers.Register(__instance, __state.Index);
        }
    }

    /// <summary>
    /// The one move that gives each player their own input.
    ///
    /// Every component on the pawn prefab (FirstPersonController, PlayerPickup, FPArms) reads
    /// InputManager.inputActions exactly once, inside Awake during Instantiate — including the
    /// MouseX/MouseY lambda subscriptions that cannot be unhooked afterwards. Swapping the
    /// static for the duration of a pawn's SpawnPlayer wires that whole pawn to one player's
    /// action set.
    ///
    /// This runs for EVERY local player, player 1 included. Giving player 1 their own instance
    /// too is what lets their pawn be pinned to a gamepad without taking the keyboard away
    /// from the pause menu, which reads the shared static.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager), "SpawnPlayer",
        typeof(int), typeof(int), typeof(Vector3), typeof(Quaternion))]
    internal static class PlayerManager_SpawnPlayer_Patch
    {
        private static readonly AccessTools.FieldRef<PlayerManager, GameObject> SpawnedObjectRef =
            AccessTools.FieldRefAccess<PlayerManager, GameObject>("SpawnedObject");

        internal struct State
        {
            public LocalPlayer Player;
            public PlayerControls SavedControls;
            public PlayerControls InstalledControls;
            public FirstPersonController SavedInstance;
        }

        private static void Prefix(PlayerManager __instance, out State __state)
        {
            __state = default;
            if (!LocalPlayers.SplitActive)
                return;

            var player = LocalPlayers.OwningManager(__instance);
            if (player == null)
                return;

            if (player.Controls == null)
                player.Controls = LocalPlayers.NewControls();

            __state.Player = player;
            __state.SavedControls = InputManager.inputActions;
            __state.SavedInstance = FirstPersonController.instance;
            __state.InstalledControls = player.Controls;
            InputManager.inputActions = player.Controls;
        }

        private static void Postfix(PlayerManager __instance, State __state)
        {
            if (__state.Player == null)
                return;

            InputManager.inputActions = __state.SavedControls;

            // Every pawn's Awake clobbers this static; it belongs to player 1.
            var host = LocalPlayers.At(0);
            var hostController = host != null ? host.Controller : null;
            FirstPersonController.instance = hostController != null ? hostController : __state.SavedInstance;

            GameObject pawn = SpawnedObjectRef(__instance);
            if (pawn != null)
                LocalPlayers.Tag(pawn, __state.Player.Index);

            PlayerVisibility.Invalidate();
            InputDiagnostics.VerifySpawn(pawn, __state.InstalledControls, __state.Player.Label);
        }
    }

    /// <summary>
    /// PlayerSetup.OnStartClient's owner branch runs for every local pawn. Most of it is
    /// wanted (enable cameras, own round-start coroutine). Unwanted for anyone but player 1:
    /// the singleton writes (Settings.localPlayer, Crosshair.player), saved and restored
    /// around the call. Afterwards the viewport layout is re-applied across the whole roster,
    /// which also settles audio listeners and HUD ownership.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSetup), nameof(PlayerSetup.OnStartClient))]
    internal static class PlayerSetup_OnStartClient_Patch
    {
        internal struct State
        {
            public bool Restore;
            public FirstPersonController LocalPlayer;
            public FirstPersonController CrosshairPlayer;
        }

        private static void Prefix(PlayerSetup __instance, out State __state)
        {
            __state = default;
            if (!__instance.IsOwner || !LocalPlayers.SplitActive)
                return;

            var player = LocalPlayers.OwningPawn(__instance.gameObject);
            if (player == null || player.Index == 0)
                return;

            __state.Restore = true;
            __state.LocalPlayer = Settings.Instance != null ? Settings.Instance.localPlayer : null;
            __state.CrosshairPlayer = Crosshair.Instance != null ? Crosshair.Instance.player : null;
        }

        private static void Postfix(PlayerSetup __instance, State __state)
        {
            if (!__instance.IsOwner)
                return;

            if (__state.Restore)
            {
                if (Settings.Instance != null)
                    Settings.Instance.localPlayer = __state.LocalPlayer;
                if (Crosshair.Instance != null)
                    Crosshair.Instance.player = __state.CrosshairPlayer;
            }

            // Runs for every pawn: whichever spawns last still triggers a fresh layout pass
            // over the whole roster.
            PlayerVisibility.Apply();
        }
    }

    /// <summary>
    /// Each round transition the game respawns the pawn of ClientInstance.Instance — one pawn,
    /// singleton. Give every added player the same round respawn. RunLocally RPC, so this
    /// fires on the host exactly once per round.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), "RpcLogic___ObserversRoundSpawn_2166136261")]
    internal static class GameManager_ObserversRoundSpawn_Patch
    {
        private static void Postfix()
        {
            if (!LocalPlayers.SplitActive)
                return;

            foreach (var player in LocalPlayers.All)
            {
                if (player.Index == 0 || player.Client == null)
                    continue;
                var spawner = player.Client.PlayerSpawner;
                if (spawner != null)
                    spawner.RoundSpawn();
            }
        }
    }

    /// <summary>
    /// Diagnostics for the spawn path: which PlayerManager gets told to spawn, and when.
    /// A player failing to appear at match start is invisible without this.
    /// </summary>
    [HarmonyPatch(typeof(PlayerManager))]
    internal static class PlayerManager_SpawnPath_Patch
    {
        private static string Who(PlayerManager manager)
        {
            var player = LocalPlayers.OwningManager(manager);
            return player != null ? player.Label : "remote/untagged";
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerManager.OnLoadSceneEnd))]
        private static void AfterLoadSceneEnd(PlayerManager __instance)
        {
            if (!Plugin.VerboseLogging.Value)
                return;
            Plugin.Log.LogInfo(
                $"OnLoadSceneEnd for {Who(__instance)} (IsOwner={__instance.IsOwner}, " +
                $"scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}').");
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerManager.NewSceneSpawn))]
        private static void AfterNewSceneSpawn(PlayerManager __instance)
        {
            if (!Plugin.VerboseLogging.Value)
                return;
            Plugin.Log.LogInfo($"NewSceneSpawn ran for {Who(__instance)} (IsOwner={__instance.IsOwner}).");
        }

        /// <summary>
        /// State of everything NewSceneSpawn dereferences, named per player, before it runs.
        ///
        /// The method is four singleton lookups and a StartCoroutine, and one of them is null
        /// for an added player while being fine for P1 a moment earlier — the stack says
        /// StartCoroutine but Harmony's dynamic-method wrappers make that frame worth
        /// distrusting. Logging the lot is cheaper than guessing which.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerManager.NewSceneSpawn))]
        private static void BeforeNewSceneSpawn(PlayerManager __instance)
        {
            if (!Plugin.VerboseLogging.Value)
                return;

            // Its own try/catch: this is a diagnostic, and a diagnostic that throws would
            // abort the very scene load it exists to explain. A destroyed PlayerManager — one
            // whose player left, still subscribed to OnLoadEnd because PlayerManager subscribes
            // in OnEnable and never unsubscribes — throws on `.gameObject` alone.
            try
            {
                var go = __instance.gameObject;
                Plugin.Log.LogInfo(
                    $"[scenespawn] {Who(__instance)} entering NewSceneSpawn: " +
                    $"active={go.activeInHierarchy} enabled={__instance.enabled} " +
                    $"SceneMotor={(SceneMotor.Instance == null ? "NULL" : "ok")} " +
                    $"SaveLoad={(SaveLoadSystem.Instance == null ? "NULL" : "ok")} " +
                    $"Cosmetics={(CosmeticsManager.Instance == null ? "NULL" : "ok")} " +
                    $"Settings={(Settings.Instance == null ? "NULL" : "ok")}.");
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning(
                    $"[scenespawn] a PlayerManager entered NewSceneSpawn in an unreadable state " +
                    $"(most likely destroyed but still subscribed to OnLoadEnd): {ex.GetType().Name}.");
            }
        }

        /// <summary>
        /// Keep one player's failed spawn from taking the whole scene load with it.
        ///
        /// NewSceneSpawn runs from PlayerManager.OnLoadSceneEnd, which FishNet calls on every
        /// subscriber in turn from InvokeOnSceneLoadEnd. An exception escaping ours does not
        /// just cost that player their spawn — it aborts the iteration, so every subscriber
        /// after them is never called, and the map itself comes up half-built: no weapon
        /// spawns, pickups in a blank state.
        ///
        /// Swallowing it is the lesser evil, and only ever for a local player we added: the
        /// player who threw is recovered anyway by SplitscreenCoordinator.EnsurePawns, which
        /// exists precisely because these spawn paths do not always fire. P1 keeps vanilla
        /// behaviour — if theirs throws, that is the game's own bug and hiding it would be
        /// dishonest.
        /// </summary>
        [HarmonyFinalizer]
        [HarmonyPatch(nameof(PlayerManager.NewSceneSpawn))]
        private static System.Exception AfterNewSceneSpawnThrew(System.Exception __exception, PlayerManager __instance)
        {
            if (__exception == null)
                return null;

            if (!LocalPlayers.SplitActive)
                return __exception; // no split running: the game is on its own, as shipped

            // Deliberately NOT conditioned on recognising the thrower any more.
            //
            // The previous version returned the exception unchanged when the manager carried no
            // LocalPlayerMarker — and then logged nothing, which is how a throw reached the log
            // with no [scenespawn] line either side of it and no explanation. An unowned manager
            // is not a reason to let the scene load die: PlayerManager subscribes to OnLoadEnd in
            // OnEnable and never unsubscribes, so a player who left the lobby leaves a destroyed
            // manager subscribed, and its failure would abort every subscriber queued behind it.
            var player = LocalPlayers.OwningManager(__instance);
            string who = player != null ? player.Label : "an unowned/destroyed PlayerManager";

            // P1 is swallowed too, which reverses the first version of this patch.
            //
            // That version let P1's throw through on the grounds that hiding the game's own bug
            // would be dishonest. The logs then caught P1 throwing here with every singleton
            // present — and because the throw escapes into FishNet's InvokeOnSceneLoadEnd, the
            // iteration aborts and the players AFTER them never run at all. One player's failure
            // was costing everybody the map: no weapon spawns, spawn points left over from the
            // previous scene, players dropped into the void.
            //
            // Swallowing it does not hide anything — the whole exception is logged at Error, and
            // the pawn is recovered by the EnsurePawns poll. It only stops one player's failure
            // from becoming everyone's.
            Plugin.Log.LogError(
                $"[scenespawn] {who}'s NewSceneSpawn threw — swallowed so the rest of the " +
                $"scene load still runs (a live player's pawn is recovered by the EnsurePawns " +
                $"poll): {__exception}");
            return null;
        }
    }

    /// <summary>
    /// Tick source. A MonoBehaviour we add ourselves does get Awake, but in this game it never
    /// receives Start/Update — so any hotkey wired to it is dead. PauseManager is a plain
    /// MonoBehaviour that exists and ticks in the menu and in match alike.
    /// </summary>
    [HarmonyPatch(typeof(PauseManager), "Update")]
    internal static class PauseManager_Update_Patch
    {
        private static void Postfix()
        {
            SplitscreenCoordinator.Tick();
        }
    }

    /// <summary>Late-frame half of the tick — see PauseManager_Update_Patch.</summary>
    [HarmonyPatch(typeof(PauseManager), "LateUpdate")]
    internal static class PauseManager_LateUpdate_Patch
    {
        private static void Postfix()
        {
            SplitscreenCoordinator.LateTick();
        }
    }
}
