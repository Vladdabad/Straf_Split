using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Identifies which local player an object belongs to. Lives on a local player's
    /// ClientInstance object and on their pawn. Local-only — never networked, so remote
    /// clients just see ordinary extra players.
    ///
    /// Player 0 (the host's own, created by the game) carries one too. That is the point:
    /// every lookup in the mod asks "which local player is this?" and gets an answer for all
    /// of them, so there is no separate code path for the original player to drift out of
    /// step with the others.
    /// </summary>
    public class LocalPlayerMarker : MonoBehaviour
    {
        public int Index;
    }

    /// <summary>
    /// One local player.
    ///
    /// Everything that used to be duplicated between "the local player" and "the second
    /// player" is a property of this object instead: which ClientInstance owns them, which
    /// action asset drives them, which gamepad feeds that asset, which pawn they currently
    /// have, and which slice of the screen they get. Adding a third or fourth player is
    /// constructing another one of these — no new branches anywhere.
    ///
    /// Player 0 is not special-cased. The game creates their ClientInstance rather than the
    /// mod, and they keep the HUD, audio listener and singletons, but those are consequences
    /// of <see cref="Index"/> being 0, tested in one place each, not a parallel implementation.
    /// </summary>
    internal sealed class LocalPlayer
    {
        private static readonly AccessTools.FieldRef<PlayerManager, GameObject> SpawnedObjectRef =
            AccessTools.FieldRefAccess<PlayerManager, GameObject>("SpawnedObject");
        private static readonly AccessTools.FieldRef<PlayerSetup, Camera[]> CamerasRef =
            AccessTools.FieldRefAccess<PlayerSetup, Camera[]>("cameras");
        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject> GraphicsRef =
            AccessTools.FieldRefAccess<PlayerSetup, GameObject>("graphics");
        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject> HudObjectRef =
            AccessTools.FieldRefAccess<PlayerSetup, GameObject>("hudObject");

        /// <summary>
        /// Position in the roster, and the identity every <see cref="LocalPlayerMarker"/> in the
        /// scene refers to. Settable only by <see cref="LocalPlayers"/>, and only in the same
        /// breath as re-stamping this player's markers — see <c>LocalPlayers.Remove</c>.
        /// </summary>
        internal int Index { get; set; }

        /// <summary>This player's connection-level object. The game made player 0's; the mod makes the rest.</summary>
        internal ClientInstance Client;

        /// <summary>
        /// The action set installed while this player's pawn spawns, so the pawn's components
        /// capture it in their Awake. Every player gets their own instance — including player
        /// 0, whose pawn would otherwise share the asset the pause menu reads, and could not
        /// then be pinned to a gamepad without taking the keyboard away from menus.
        /// </summary>
        internal PlayerControls Controls;

        /// <summary>The gamepad feeding this player, or null while they are waiting for one.</summary>
        internal InputDevice Device;

        /// <summary>
        /// Per-player join state. These live on the instance rather than as statics precisely
        /// so that a third and fourth player do not need a second and third copy of them.
        /// </summary>
        internal bool Readied;

        /// <summary>
        /// One-shot: this player's ClientInstance has come up and the join has been finished off.
        /// Separate from <see cref="Readied"/>, which now tracks the lobby's ready state and can
        /// go back and forth as they press Y and B.
        /// </summary>
        internal bool JoinAdvanced;

        internal int PawnAttempts;
        internal float NextPawnCheck;

        /// <summary>
        /// This player's own copy of the ammo bus — see <see cref="HudRouting"/>. Held here
        /// rather than in a side table keyed by index so that a player's HUD state lives and
        /// dies with the player, like everything else about them.
        /// </summary>
        internal string AmmoLeft = "---";

        internal string AmmoRight = "---";
        internal float AmmoLeftAt;
        internal float AmmoRightAt;

        /// <summary>
        /// This player's own sensitivity, invert, FOV and the rest — see <see cref="SettingsRouting"/>.
        ///
        /// Player 0's is a mirror of the game's <see cref="global::Settings"/> singleton, taken
        /// the first time anyone asks and refreshed every tick; everyone else's is read from
        /// their own PlayerPrefs keys, falling back to player 0's for anything they have never
        /// changed. So a player who has just joined starts out feeling like the host's game
        /// rather than like the prefab's defaults.
        ///
        /// Saved per roster slot, deliberately — the slots behave like controller ports. If the
        /// player in slot 3 leaves, slot 4 shifts down into it and keeps the settings they are
        /// playing with (they are in memory, and re-saved under the slot they moved into); it is
        /// the saved file that belongs to the seat, not to the person.
        /// </summary>
        internal PlayerSettings Settings
        {
            get
            {
                if (_settings == null)
                {
                    var fresh = new PlayerSettings(Index);
                    if (Index == 0)
                    {
                        // Not cached until there is something real to mirror. Caching an empty
                        // copy taken before the game's Settings singleton exists would make
                        // every value zero, and the first borrow would then push those zeros
                        // onto the singleton for the whole game to read.
                        var instance = global::Settings.Instance;
                        if (instance == null)
                            return fresh;
                        fresh.Pull(instance);
                    }
                    else
                    {
                        var host = LocalPlayers.At(0);
                        fresh.Load(host != null && !ReferenceEquals(host, this) ? host.Settings : null);
                    }
                    _settings = fresh;
                }
                else if (_settings.Slot != Index)
                {
                    _settings.Slot = Index;
                    _settings.Save();
                }
                return _settings;
            }
        }

        private PlayerSettings _settings;

        /// <summary>Cached hitbox colliders for the damage-layer swap, keyed to the pawn they came from.</summary>
        internal Collider[] BodyColliders;

        internal int BodyCollidersPawnId;

        internal string Label => "P" + (Index + 1);

        internal LocalPlayer(int index, ClientInstance client)
        {
            Index = index;
            Client = client;
        }

        internal bool Alive => Client != null;

        internal PlayerManager Manager
        {
            get
            {
                return Client != null ? Client.GetComponent<PlayerManager>() : null;
            }
        }

        internal GameObject Pawn
        {
            get
            {
                var manager = Manager;
                return manager != null ? SpawnedObjectRef(manager) : null;
            }
        }

        internal PlayerSetup Setup
        {
            get
            {
                var pawn = Pawn;
                return pawn != null ? pawn.GetComponent<PlayerSetup>() : null;
            }
        }

        internal FirstPersonController Controller
        {
            get
            {
                var pawn = Pawn;
                return pawn != null ? pawn.GetComponent<FirstPersonController>() : null;
            }
        }

        internal Camera[] Cameras
        {
            get
            {
                var setup = Setup;
                return setup != null ? CamerasRef(setup) : null;
            }
        }

        internal GameObject Graphics
        {
            get
            {
                var setup = Setup;
                return setup != null ? GraphicsRef(setup) : null;
            }
        }

        internal GameObject Hud
        {
            get
            {
                var setup = Setup;
                return setup != null ? HudObjectRef(setup) : null;
            }
        }

        /// <summary>
        /// The asset this player's pawn is really holding.
        ///
        /// Not the same thing as <see cref="Controls"/>: InputManager.RestartEngine fires
        /// during the spawn and overwrites the static the pawn is about to read, so a pawn
        /// routinely ends up on a third instance that neither static points at. Device pinning
        /// has to target what the pawn actually reads, or it pins an asset nobody uses.
        /// </summary>
        internal PlayerControls PawnControls
        {
            get
            {
                var controller = Controller;
                return controller != null ? controller.playerControls : null;
            }
        }

        /// <summary>
        /// Pin this player's input to their own gamepad, on the asset their pawn is holding.
        /// A player with no gamepad yet is pinned to nothing — deliberately deaf rather than
        /// falling back to shared keyboard input, which would drive every unbound player at
        /// once. They pick up the first spare pad the moment one is plugged in.
        /// </summary>
        internal void ApplyDeviceFilter()
        {
            var controls = PawnControls;
            if (controls == null || controls.asset == null)
                return;
            controls.asset.devices = Device != null
                ? new InputDevice[] { Device }
                : System.Array.Empty<InputDevice>();
        }

        internal string DescribeDevices()
        {
            var controls = PawnControls;
            if (controls == null)
                return "no pawn";
            var devices = controls.asset.devices;
            if (!devices.HasValue)
                return "ALL (unrestricted)";
            if (devices.Value.Count == 0)
                return "none (waiting for a controller)";
            var names = new System.Collections.Generic.List<string>();
            foreach (var d in devices.Value)
                names.Add(d.displayName);
            return string.Join(", ", names.ToArray());
        }
    }
}
