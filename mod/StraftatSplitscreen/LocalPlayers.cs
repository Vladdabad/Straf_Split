using System;
using System.Collections.Generic;
using System.Text;
using FishNet;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StraftatSplitscreen
{
    /// <summary>
    /// The roster of local players, and the single place that knows how many there are.
    ///
    /// Every other part of the mod asks this registry rather than testing "is this the second
    /// player". Visibility, viewports, input, HUD and audio all iterate the roster and key off
    /// <see cref="LocalPlayer.Index"/>, so behaviour added for one player is behaviour for all
    /// of them, and raising the ceiling is a number rather than a new branch.
    ///
    /// Player 0 is adopted from the game: <see cref="EnsureHostPlayer"/> wraps the
    /// ClientInstance the game already made and tags it with a marker, so it looks up exactly
    /// like the ones the mod spawns. Players 1..N-1 are extra ClientInstances spawned onto the
    /// host's own connection — every FishNet ownership guard checks the connection rather than
    /// "the one local player", so they pass all of it.
    /// </summary>
    internal static class LocalPlayers
    {
        /// <summary>Hard ceiling. Beyond four the screen slices stop being playable.</summary>
        internal const int MaxPlayers = 4;

        private static readonly List<LocalPlayer> _players = new List<LocalPlayer>();

        internal static IList<LocalPlayer> All => _players;

        internal static int Count => _players.Count;

        /// <summary>True once there is more than one local player — the point where the mod changes anything.</summary>
        internal static bool SplitActive => _players.Count > 1;

        // ------------------------------------------------------------------ lookup

        internal static LocalPlayer At(int index)
        {
            return index >= 0 && index < _players.Count ? _players[index] : null;
        }

        /// <summary>
        /// Which local player owns this object, by marker. Works for a ClientInstance object,
        /// a pawn, or anything under either — one lookup for every "whose is this?" question
        /// the mod used to answer with a bespoke IsSecond* helper.
        /// </summary>
        internal static LocalPlayer Owning(Component c)
        {
            if (c == null)
                return null;
            var marker = c.GetComponentInParent<LocalPlayerMarker>();
            if (marker == null)
                return null;
            return At(marker.Index);
        }

        internal static LocalPlayer OwningPawn(GameObject pawn)
        {
            if (pawn == null)
                return null;
            var owner = Owning(pawn.transform);
            if (owner != null)
                return owner;

            // Fallback for the window between a pawn spawning and being tagged: the pawn
            // points back at its ClientInstance, which is tagged.
            var values = pawn.GetComponent<PlayerValues>();
            var client = values != null ? values.sync___get_value_playerClient() : null;
            return client != null ? Owning(client) : null;
        }

        internal static LocalPlayer OwningManager(PlayerManager manager)
        {
            return manager == null ? null : Owning(manager);
        }

        internal static int IndexOf(Component c)
        {
            var player = Owning(c);
            return player != null ? player.Index : -1;
        }

        // ------------------------------------------------------------------ roster

        /// <summary>
        /// Adopt the game's own local player as index 0 if we have not already. Idempotent and
        /// cheap, so it can be called from the tick rather than hooked to a lifecycle event.
        /// </summary>
        internal static bool EnsureHostPlayer()
        {
            var lobby = LobbyController.Instance;
            var host = lobby != null ? lobby.LocalPlayerController : null;

            if (_players.Count == 0)
            {
                if (host == null)
                    return false;
                _players.Add(new LocalPlayer(0, host));
                Tag(host.gameObject, 0);
                Plugin.Log.LogInfo("Adopted the game's own local player as P1.");
                return true;
            }

            // Never insert or remove at position 0. List position IS the index every
            // LocalPlayerMarker in the scene refers to, so shifting the list to re-adopt a
            // rebuilt host ClientInstance would silently repoint every other player's marker
            // at the wrong slot. Re-point the existing entry instead.
            if (host != null && !ReferenceEquals(_players[0].Client, host))
            {
                _players[0].Client = host;
                Tag(host.gameObject, 0);
                Plugin.Log.LogInfo("Re-adopted a rebuilt host ClientInstance as P1.");
            }

            return _players[0].Client != null;
        }

        internal static void Tag(GameObject go, int index)
        {
            if (go == null)
                return;
            var marker = go.GetComponent<LocalPlayerMarker>();
            if (marker == null)
                marker = go.AddComponent<LocalPlayerMarker>();
            marker.Index = index;
        }

        /// <summary>
        /// Add one more local player: another ClientInstance on the host's own connection.
        /// </summary>
        internal static bool TryAdd(out string error)
        {
            error = null;

            if (!EnsureHostPlayer())
            {
                error = "the local player instance is not ready yet (host a lobby first)";
                return false;
            }
            if (_players.Count >= MaxPlayers)
            {
                error = $"already at the {MaxPlayers}-player ceiling";
                return false;
            }

            var nm = InstanceFinder.NetworkManager;
            if (nm == null || !nm.IsHost)
            {
                error = "not hosting a game (host a lobby first)";
                return false;
            }

            var hostNob = _players[0].Client.GetComponent<NetworkObject>();
            NetworkObject prefab = ResolvePlayerPrefab(nm, hostNob, out error);
            if (prefab == null)
                return false;

            int index = _players.Count;

            // The roster entry goes in BEFORE the spawn, not after. On a host,
            // ClientInstance.OnStartClient can run inside ServerManager.Spawn, and the postfix
            // that fills the entry in would then find no slot to fill.
            _players.Add(new LocalPlayer(index, null));

            GameObject go = UnityEngine.Object.Instantiate(prefab.gameObject);
            Tag(go, index);
            nm.ServerManager.Spawn(go, hostNob.Owner);

            Plugin.Log.LogInfo($"Spawning P{index + 1} ({_players.Count} local players).");
            return true;
        }

        /// <summary>Called from the ClientInstance.OnStartClient postfix once FishNet has initialised it.</summary>
        internal static void Register(ClientInstance client, int index)
        {
            var player = At(index);
            if (player == null)
            {
                Plugin.Log.LogWarning($"ClientInstance initialised for unknown local player index {index}.");
                return;
            }
            player.Client = client;
            Plugin.Log.LogInfo($"{player.Label} ClientInstance initialised (PlayerId={client.PlayerId}).");
        }

        internal static void RemoveLast()
        {
            if (_players.Count <= 1)
            {
                Plugin.Log.LogWarning("No added local players to remove.");
                return;
            }
            Remove(_players[_players.Count - 1]);
        }

        /// <summary>
        /// Drop one added player, wherever they sit in the roster.
        ///
        /// List position IS the index every <see cref="LocalPlayerMarker"/> refers to, so a
        /// removal from the middle has to re-stamp everyone above it in the same breath —
        /// otherwise every marker above the hole points one slot too high and players start
        /// answering for each other. Player 0 is never removed: they are the game's own local
        /// player, and the roster is anchored on them.
        /// </summary>
        internal static bool Remove(LocalPlayer player)
        {
            if (player == null)
                return false;
            int at = _players.IndexOf(player);
            if (at <= 0)
            {
                Plugin.Log.LogWarning(
                    player.Index == 0
                        ? "P1 is the game's own local player and cannot be removed."
                        : "Asked to remove a player who is not in the roster.");
                return false;
            }

            _players.RemoveAt(at);
            Despawn(player);

            for (int i = at; i < _players.Count; i++)
            {
                var moved = _players[i];
                if (moved.Index == i)
                    continue;
                string was = moved.Label;
                moved.Index = i;
                if (moved.Client != null)
                    Tag(moved.Client.gameObject, i);
                var pawn = moved.Pawn;
                if (pawn != null)
                    Tag(pawn, i);
                Plugin.Log.LogInfo($"{was} is now {moved.Label}.");
            }

            Plugin.Log.LogInfo($"Removed {player.Label} ({_players.Count} local players).");
            PlayerVisibility.Apply();
            return true;
        }

        internal static void RemoveAll()
        {
            while (_players.Count > 1)
                RemoveLast();
        }

        private static void Despawn(LocalPlayer player)
        {
            if (player.Client == null)
                return;
            try
            {
                // Unsubscribe BEFORE despawning, while the manager is still readable.
                //
                // PlayerManager.OnEnable subscribes to SceneManager.OnLoadStart/OnLoadEnd and
                // there is no OnDisable or OnDestroy anywhere in the class — nothing ever
                // unsubscribes. Destroying the object therefore leaves a dead delegate in
                // FishNet's invocation list, and the next map load calls into it: it throws,
                // and because FishNet invokes its subscribers in one loop, every subscriber
                // queued behind it is skipped — including the map's own setup. That is the
                // "invisible map with no weapon spawns", and it got one entry worse every time a
                // player was removed.
                //
                // The finalizer on NewSceneSpawn still catches whatever slips through (a
                // remote player leaving leaks the same way, and that one is not ours to
                // intercept), but not leaking in the first place is the actual fix.
                var manager = player.Manager;
                var sceneManager = InstanceFinder.SceneManager;
                if (manager != null && sceneManager != null)
                {
                    sceneManager.OnLoadStart -= manager.OnLoadSceneStart;
                    sceneManager.OnLoadEnd -= manager.OnLoadSceneEnd;
                    Plugin.Log.LogInfo($"{player.Label}'s PlayerManager unsubscribed from the scene-load events.");
                }

                var pawn = player.Pawn;
                if (pawn != null)
                {
                    var nob = pawn.GetComponent<NetworkObject>();
                    if (nob != null)
                        InstanceFinder.ServerManager.Despawn(nob, null);
                }
                var clientNob = player.Client.GetComponent<NetworkObject>();
                if (clientNob != null)
                    InstanceFinder.ServerManager.Despawn(clientNob, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Despawning {player.Label} threw: {ex}");
            }
            player.Client = null;
        }

        // ------------------------------------------------------------------ input

        private static string _lastDeviceSignature;

        /// <summary>
        /// Keep every player on the pad they claimed, and fill in anyone who has none.
        ///
        /// A claim is made in the lobby, by pressing A on the pad itself (<see cref="LobbyJoin"/>),
        /// and it is sticky: a player keeps that exact controller whatever else happens to the
        /// roster. This is why the assignment is no longer positional — with players joining and
        /// leaving on their own controllers, `pads[i]` for player `i` reshuffles everyone every
        /// time somebody drops out.
        ///
        /// Anyone left without a pad — never claimed one, or theirs was unplugged — takes the
        /// first controller nobody else holds. A player with none stays bound to nothing rather
        /// than falling back to the keyboard: one keyboard shared between unbound players would
        /// drive all of them at once, which is the bug this design exists to prevent.
        ///
        /// Re-run every tick, so plugging a controller in mid-match binds the next waiting
        /// player and unplugging one releases them, with no reconnect step.
        /// </summary>
        internal static void AssignDevices()
        {
            if (!SplitActive)
                return;

            var pads = Gamepad.all;

            // Release claims on pads that are gone, so the slot can be filled or re-claimed.
            foreach (var player in _players)
            {
                if (player.Device != null && !IsConnected(player.Device, pads))
                    player.Device = null;
            }

            foreach (var player in _players)
            {
                if (player.Device != null)
                    continue;
                foreach (var pad in pads)
                {
                    if (Holder(pad) == null)
                    {
                        player.Device = pad;
                        break;
                    }
                }
            }

            for (int i = 0; i < _players.Count; i++)
                _players[i].ApplyDeviceFilter();

            var sb = new StringBuilder();
            for (int i = 0; i < _players.Count; i++)
            {
                if (i > 0)
                    sb.Append(' ');
                sb.Append($"{_players[i].Label}=[{_players[i].DescribeDevices()}]");
            }
            string signature = sb.ToString();
            if (signature != _lastDeviceSignature)
            {
                _lastDeviceSignature = signature;
                Plugin.Log.LogInfo($"[input] {signature}");

                int unbound = 0;
                foreach (var p in _players)
                {
                    if (p.Device == null)
                        unbound++;
                }
                if (unbound > 0)
                    Plugin.Log.LogWarning(
                        $"[input] {unbound} local player(s) have no controller and cannot move. " +
                        $"{pads.Count} gamepad(s) connected for {_players.Count} player(s) — plug one in to bind the next.");

                WarnOnSharedAssets();
            }
        }

        /// <summary>The local player holding this controller, or null if nobody has claimed it.</summary>
        internal static LocalPlayer Holder(InputDevice device)
        {
            if (device == null)
                return null;
            foreach (var player in _players)
            {
                if (ReferenceEquals(player.Device, device))
                    return player;
            }
            return null;
        }

        /// <summary>The first player with no controller — who a fresh claim goes to before a new player is added.</summary>
        internal static LocalPlayer FirstUnbound()
        {
            foreach (var player in _players)
            {
                if (player.Device == null)
                    return player;
            }
            return null;
        }

        private static bool IsConnected(InputDevice device, System.Collections.Generic.IEnumerable<Gamepad> pads)
        {
            foreach (var pad in pads)
            {
                if (ReferenceEquals(pad, device))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Two players holding one asset cannot be separated by device, whatever we set. It
        /// means the control swap did not reach one of their pawns, so say so rather than
        /// letting it look like a device problem.
        /// </summary>
        private static void WarnOnSharedAssets()
        {
            for (int i = 0; i < _players.Count; i++)
            {
                var a = _players[i].PawnControls;
                if (a == null)
                    continue;
                for (int j = i + 1; j < _players.Count; j++)
                {
                    var b = _players[j].PawnControls;
                    if (b != null && ReferenceEquals(a.asset, b.asset))
                    {
                        Plugin.Log.LogError(
                            $"[input] {_players[i].Label} and {_players[j].Label} are holding the SAME " +
                            "action asset — their input cannot be separated. The control swap during " +
                            "one of their spawns did not take.");
                    }
                }
            }
        }

        /// <summary>
        /// A fresh action set for one player, carrying over any rebinds the user has made in
        /// the options menu (a new PlayerControls otherwise starts from stock bindings and
        /// silently discards them).
        /// </summary>
        internal static PlayerControls NewControls()
        {
            var controls = new PlayerControls();
            try
            {
                var shared = InputManager.inputActions;
                if (shared != null && shared.asset != null && controls.asset != null)
                {
                    string overrides = shared.asset.SaveBindingOverridesAsJson();
                    if (!string.IsNullOrEmpty(overrides))
                        controls.asset.LoadBindingOverridesFromJson(overrides);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Could not copy binding overrides onto the new action set: {ex.Message}");
            }
            return controls;
        }

        // ------------------------------------------------------------------ prefab

        /// <summary>
        /// Find the prefab to instantiate for an added player, and refuse anything dangerous.
        ///
        /// PrefabId is normally the right answer, but a scene object or an unregistered prefab
        /// reports an id that resolves to something else entirely — and instantiating the
        /// wrong prefab can duplicate FishNet's managers, which produces "multiple
        /// TimeManagers instantiated" and breaks the session.
        /// </summary>
        private static NetworkObject ResolvePlayerPrefab(FishNet.Managing.NetworkManager nm, NetworkObject hostNob, out string error)
        {
            error = null;
            var prefabs = nm.SpawnablePrefabs;
            if (prefabs == null)
            {
                error = "NetworkManager.SpawnablePrefabs is null";
                return null;
            }

            int count = prefabs.GetObjectCount();
            NetworkObject candidate = null;
            if (!hostNob.IsSceneObject && hostNob.PrefabId < count)
                candidate = prefabs.GetObject(true, hostNob.PrefabId);

            string why;
            if (IsUsablePlayerPrefab(candidate, out why))
                return candidate;

            Plugin.Log.LogWarning($"PrefabId {hostNob.PrefabId} unusable ({why}); scanning the registry.");
            for (int i = 0; i < count; i++)
            {
                var nob = prefabs.GetObject(true, i);
                if (IsUsablePlayerPrefab(nob, out _))
                {
                    Plugin.Log.LogInfo($"Found player prefab at id {i} '{nob.name}'.");
                    return nob;
                }
            }

            error = $"no prefab with ClientInstance+PlayerManager found among {count} registered prefabs";
            return null;
        }

        private static bool IsUsablePlayerPrefab(NetworkObject nob, out string why)
        {
            why = null;
            if (nob == null)
            {
                why = "null";
                return false;
            }
            if (nob.GetComponent<ClientInstance>() == null)
            {
                why = $"'{nob.name}' has no ClientInstance";
                return false;
            }
            if (nob.GetComponent<PlayerManager>() == null)
            {
                why = $"'{nob.name}' has no PlayerManager";
                return false;
            }
            if (nob.GetComponentInChildren<FishNet.Managing.NetworkManager>(true) != null ||
                nob.GetComponentInChildren<FishNet.Managing.Timing.TimeManager>(true) != null)
            {
                why = $"'{nob.name}' carries a NetworkManager/TimeManager — refusing";
                return false;
            }
            return true;
        }
    }
}
