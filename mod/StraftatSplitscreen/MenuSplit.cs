using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;

namespace StraftatSplitscreen
{
    /// <summary>
    /// One pause/options menu per local player, in their own slice of the screen, driven by
    /// their own controller.
    ///
    /// The game has one of each menu on a ScreenSpaceOverlay canvas that ignores camera viewport
    /// rects, so Start opened a menu across the whole screen; and one EventSystem, so whichever
    /// stick moved drove it.
    ///
    /// ## The rig
    ///
    ///     Canvas (ScreenSpaceOverlay)
    ///       Splitscreen_Menu_P1          anchored to P1's viewport rect
    ///         PauseMenu                  the scene's own, reparented
    ///         OptionsMenu                the scene's own, reparented
    ///       Splitscreen_Menu_P2          anchored to P2's viewport rect
    ///         PauseMenu (clone)
    ///         OptionsMenu (clone)
    ///
    /// The root is the only thing anchored here. Both menus are authored stretched to fill the
    /// canvas, so a parent filling a quarter of the screen makes them fill that quarter with no
    /// per-object placement — which also avoids the trap <see cref="CrosshairSplit.Place"/>
    /// documents, where collapsing a stretched rect's anchors to a point leaves it 0x0.
    ///
    /// ## Why both menus are cloned together
    ///
    /// pauseMenu and optionsMenu are siblings, and each one's buttons reference the other through
    /// serialized UnityEvent targets. Instantiate only remaps references pointing *within* the
    /// copied subtree, and a UnityEvent target is not a field anybody can rewire afterwards — so
    /// cloning them separately would leave every clone's Options button opening player 1's
    /// options screen. Player 1's two menus are reparented into P1's root first, and that root is
    /// what gets cloned, so both travel inside one subtree and Unity remaps them for free.
    ///
    /// Player 1 keeps the scene's originals, which is what keeps <c>PauseManager.pauseMenu</c> and
    /// every other vanilla reference pointing at something real. They go back where they were on
    /// teardown.
    ///
    /// ## Three rules this file is built to obey
    ///
    /// An earlier attempt at this broke the input split and had to be reverted. The three things
    /// that went wrong are structural, and each is prevented here by construction rather than by
    /// care:
    ///
    ///  1. **Never write <c>InputManager.inputActions</c>.** That static belongs to the spawn
    ///     swap in <c>PlayerManager_SpawnPlayer_Patch</c> — "the one move that gives each player
    ///     their own input" — and <c>SplitscreenCoordinator.WatchSharedStatic</c> exists purely
    ///     to catch a second writer. Nothing in this file assigns it. The menus are pointed at
    ///     their asset directly instead.
    ///
    ///  2. **Bind to <see cref="LocalPlayer.PawnControls"/>, never <c>Controls</c>.** Its own
    ///     summary says why: <c>RestartEngine</c> fires during the spawn, so a pawn routinely
    ///     ends up on a third asset that neither static points at, and
    ///     <c>ApplyDeviceFilter</c> pins devices on what the pawn actually reads. Binding a menu
    ///     to <c>Controls</c> binds it to an unfiltered asset, which is every pad driving every
    ///     menu. That asset is also replaced on every spawn, so <see cref="Bind"/> re-points the
    ///     module whenever its identity changes.
    ///
    ///  3. **Clone with the subtree inactive.** <c>DisplayLoadValues.LoadValues</c> fills widgets
    ///     by assigning <c>Toggle.isOn</c> and <c>Slider.value</c> — the *notifying* setters — so
    ///     a clone waking up runs real <c>Settings.Change*</c> handlers and writes somebody's
    ///     settings to PlayerPrefs. Instantiating an inactive object runs no Awake, and
    ///     <see cref="Patches.DisplayLoadValues_Clone_Suppression"/> keeps it that way when the
    ///     menu is first opened. Clone widgets are painted by
    ///     <see cref="SettingsUi.Refresh"/> instead, which uses the non-notifying setters.
    ///
    /// ## Independent navigation
    ///
    /// <c>EventSystem.Update</c> begins <c>if (current != this) return;</c> and
    /// <c>EventSystem.current</c> is <c>m_EventSystems[0]</c>, so normally only one event system
    /// in the process ever ticks. <see cref="MultiplayerEventSystem"/> exists for this: its
    /// Update sets <c>current = this</c>, calls base, and restores in a finally. Four of them all
    /// tick, each current for its own Update — which is also what makes
    /// <see cref="EditingPlayer"/> able to name the owner of a UI callback exactly.
    ///
    /// The input module is built by hand rather than cloned off the scene's, because
    /// <c>actionsAsset</c>'s setter re-resolves references by the names of the ones it already
    /// holds, and because the scene's EventSystem is a DontDestroyOnLoad singleton that would
    /// destroy any copy of itself.
    /// </summary>
    internal static class MenuSplit
    {
        private sealed class Rig
        {
            internal int Index;
            internal RectTransform Root;
            internal GameObject PauseMenu;
            internal GameObject OptionsMenu;
            internal MultiplayerEventSystem Events;
            internal InputSystemUIInputModule Module;

            /// <summary>The asset the module is currently pointed at, to notice a respawn swapping it.</summary>
            internal InputActionAsset BoundAsset;

            /// <summary>Last frame's <see cref="Open"/>, so the opening moment can be caught.</summary>
            internal bool WasOpen;

            internal bool Alive => Root != null && PauseMenu != null;
            internal bool Open => PauseMenu != null && PauseMenu.activeInHierarchy;
        }

        private static readonly AccessTools.FieldRef<PauseManager, GameObject> PauseMenuRef =
            AccessTools.FieldRefAccess<PauseManager, GameObject>("pauseMenu");
        private static readonly AccessTools.FieldRef<PauseManager, GameObject> OptionsMenuRef =
            AccessTools.FieldRefAccess<PauseManager, GameObject>("optionsMenu");
        private static readonly AccessTools.FieldRef<PauseManager, GameObject> ResumeButtonRef =
            AccessTools.FieldRefAccess<PauseManager, GameObject>("resumeButton3D");

        private static readonly Dictionary<int, Rig> _rigs = new Dictionary<int, Rig>();
        private static readonly Dictionary<EventSystem, int> _owners = new Dictionary<EventSystem, int>();

        /// <summary>Where player 1's two menus came from, so they can be put back exactly.</summary>
        private static Transform _homeParent;
        private static int _homePauseSibling = -1;
        private static int _homeOptionsSibling = -1;

        private static PauseManager _builtAgainst;
        private static EventSystem _vanillaEvents;

        internal static bool Active => _rigs.Count > 0;

        // ------------------------------------------------------------------ lookup

        /// <summary>
        /// The player whose menu is dispatching the UI callback running right now, or null.
        ///
        /// Deliberately narrow. It answers only when that player's own menu is actually on
        /// screen, so it cannot attribute a stray write — a coroutine, a widget waking up — to
        /// somebody who merely happens to own the event system that ticked last. The previous
        /// attempt made this the general answer for "who owns a settings edit" and routed
        /// startup writes to arbitrary players, which saved a wrong FOV into their prefs.
        /// </summary>
        internal static LocalPlayer EditingPlayer()
        {
            if (_rigs.Count == 0)
                return null;

            int index;
            var events = EventSystem.current;
            if (events == null || !_owners.TryGetValue(events, out index))
                return null;

            Rig rig;
            if (!_rigs.TryGetValue(index, out rig) || !rig.Open)
                return null;

            return LocalPlayers.At(index);
        }

        /// <summary>
        /// The player whose menu this object lives in, or null for anything outside one.
        ///
        /// A widget's place in the hierarchy is a fact that holds whenever it is asked — during
        /// Awake, inside a coroutine, while repainting — where <c>EventSystem.current</c> only
        /// means anything inside an event dispatch.
        /// </summary>
        internal static LocalPlayer PlayerOwning(Transform child)
        {
            if (child == null || _rigs.Count == 0)
                return null;

            for (var t = child; t != null; t = t.parent)
            {
                foreach (var kv in _rigs)
                {
                    var rig = kv.Value;
                    if (rig.Root != null && ReferenceEquals(t, rig.Root))
                        return LocalPlayers.At(kv.Key);
                }
            }
            return null;
        }

        /// <summary>True if this object is inside a CLONED menu — i.e. anyone's but player 1's.</summary>
        internal static bool IsClonedWidget(Transform child)
        {
            var player = PlayerOwning(child);
            return player != null && player.Index != 0;
        }

        internal static GameObject PauseMenuFor(LocalPlayer player)
        {
            Rig rig;
            if (player != null && _rigs.TryGetValue(player.Index, out rig) && rig.Alive)
                return rig.PauseMenu;
            return null;
        }

        /// <summary>
        /// The root of this player's menu subtree — the scope for anything that used to sweep the
        /// whole scene for menu widgets.
        /// </summary>
        internal static Transform RootFor(LocalPlayer player)
        {
            Rig rig;
            if (player != null && _rigs.TryGetValue(player.Index, out rig) && rig.Alive)
                return rig.Root;
            return null;
        }

        internal static bool IsOpenFor(LocalPlayer player)
        {
            Rig rig;
            return player != null && _rigs.TryGetValue(player.Index, out rig) && rig.Open;
        }

        /// <summary>
        /// Is anybody's menu on screen? The question <c>PauseManager.Update</c> is really asking
        /// when it reads <c>pauseMenu.activeSelf</c> to decide whether to free the cursor.
        /// </summary>
        internal static bool AnyOpen()
        {
            foreach (var kv in _rigs)
            {
                if (kv.Value.Open)
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Build, refresh or tear down the rigs. Cheap, idempotent, called every frame.</summary>
        internal static void Tick()
        {
            // Not in the menus, and this is not a detail: optionsMenu is the SAME object the main
            // menu shows its settings on, and players join in the lobby — so building rigs there
            // would reparent the main menu's own options screen into a quarter of the screen
            // while somebody was looking at it. PauseManager.Menu draws the same line by the same
            // test, which is the argument for using it rather than inventing one.
            if (SceneManager.GetActiveScene().name == "MainMenu")
            {
                Teardown();
                return;
            }

            var pause = PauseManager.Instance;
            if (!Plugin.SplitMenus.Value || pause == null || !LocalPlayers.SplitActive)
            {
                Teardown();
                return;
            }

            // A map load brings a new PauseManager and a new canvas; the old rigs point at
            // destroyed objects.
            if (!ReferenceEquals(pause, _builtAgainst))
            {
                Teardown();
                _builtAgainst = pause;
            }

            var home = PauseMenuRef(pause);
            var options = OptionsMenuRef(pause);
            if (home == null || options == null)
                return;

            if (!EnsureHome(pause, home, options))
                return;

            int count = LocalPlayers.Count;
            foreach (var player in LocalPlayers.All)
            {
                var rig = EnsureRig(player);
                if (rig == null)
                    continue;
                Place(rig.Root, PlayerVisibility.Viewport(player.Index, count));
                Bind(rig, player);
                NoteOpenEdge(rig, player);
            }

            DropRigsFrom(count);
            SilenceVanilla();
        }

        /// <summary>Move player 1's menus into their own root, once. Everything else is defined against it.</summary>
        private static bool EnsureHome(PauseManager pause, GameObject home, GameObject options)
        {
            Rig existing;
            if (_rigs.TryGetValue(0, out existing) && existing.Alive)
                return true;

            var canvas = home.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                Plugin.Log.LogError(
                    "[menu] the pause menu is not under a Canvas — per-player menus cannot be built.");
                return false;
            }

            if (_homeParent == null)
            {
                _homeParent = home.transform.parent;
                _homePauseSibling = home.transform.GetSiblingIndex();
                _homeOptionsSibling = options.transform.GetSiblingIndex();
            }

            var root = NewRoot(canvas.transform, 0);

            // pauseMenu first, optionsMenu second, always: the clones find their pair by child
            // index, and that only works if the order is fixed here.
            home.transform.SetParent(root, false);
            options.transform.SetParent(root, false);

            _rigs[0] = new Rig { Index = 0, Root = root, PauseMenu = home, OptionsMenu = options };
            Plugin.Log.LogInfo("[menu] P1's pause and options menus moved into their own viewport root.");
            return true;
        }

        private static Rig EnsureRig(LocalPlayer player)
        {
            Rig rig;
            if (_rigs.TryGetValue(player.Index, out rig) && rig.Alive)
                return rig;

            if (player.Index == 0)
                return null; // EnsureHome owns slot 0 and has already run.

            Rig template;
            if (!_rigs.TryGetValue(0, out template) || !template.Alive)
                return null;

            // Cloned INACTIVE, which is the whole of rule 3. Instantiating an inactive object
            // runs no Awake and starts no coroutine, so DisplayLoadValues cannot fill the clone's
            // widgets through the notifying setters and write somebody's settings on the way up.
            var templateGo = template.Root.gameObject;
            bool wasActive = templateGo.activeSelf;
            templateGo.SetActive(false);
            GameObject clone;
            try
            {
                clone = Object.Instantiate(templateGo, template.Root.parent);
            }
            finally
            {
                templateGo.SetActive(wasActive);
            }

            clone.name = "Splitscreen_Menu_P" + (player.Index + 1);
            var root = clone.GetComponent<RectTransform>();

            // Child 0 and 1, in the order EnsureHome fixed — by index, because Instantiate
            // appends "(Clone)" to every name it copies.
            var pauseClone = root.childCount > 0 ? root.GetChild(0).gameObject : null;
            var optionsClone = root.childCount > 1 ? root.GetChild(1).gameObject : null;
            if (pauseClone == null || optionsClone == null)
            {
                Plugin.Log.LogError($"[menu] P{player.Index + 1}'s menu clone came out malformed — dropping it.");
                Object.Destroy(clone);
                return null;
            }

            pauseClone.name = "PauseMenu_P" + (player.Index + 1);
            optionsClone.name = "OptionsMenu_P" + (player.Index + 1);

            // Both closed before anything is allowed to wake up, whatever P1 had on screen at
            // the instant of the copy. Only then is the root itself activated.
            pauseClone.SetActive(false);
            optionsClone.SetActive(false);
            clone.SetActive(true);

            rig = new Rig
            {
                Index = player.Index,
                Root = root,
                PauseMenu = pauseClone,
                OptionsMenu = optionsClone,
            };
            _rigs[player.Index] = rig;

            Plugin.Log.LogInfo($"[menu] built a pause/options menu for {player.Label}.");
            return rig;
        }

        /// <summary>
        /// Repaint a menu with its owner's values the moment it opens.
        ///
        /// A clone is a copy of player 1's menu, and its own filling coroutine is suppressed, so
        /// its sliders show player 1's numbers until this says otherwise. <see cref="SettingsUi"/>
        /// writes them with the non-notifying setters, so no handler fires and nothing is saved.
        ///
        /// On the opening edge only — repainting every frame would fight the player's own drag.
        /// </summary>
        private static void NoteOpenEdge(Rig rig, LocalPlayer player)
        {
            bool open = rig.Open;
            if (open != rig.WasOpen)
            {
                if (open)
                {
                    SettingsUi.Refresh(player);
                    if (Plugin.VerboseLogging.Value)
                        Plugin.Log.LogInfo($"[menu] {player.Label} opened their menu — {player.Settings.Describe()}");
                }

                // The UI map is live only while this player's menu is up. It shares a stick with
                // the Player map, so leaving it enabled all match would have their navigation
                // running alongside their movement for no reason.
                var controls = player.PawnControls;
                if (controls != null)
                {
                    if (open)
                        controls.UI.Enable();
                    else
                        controls.UI.Disable();
                }
            }
            rig.WasOpen = open;
        }

        /// <summary>
        /// Stretch a rig's root across its player's slice of the screen. Normalized viewport
        /// coordinates are canvas anchors directly for a screen-filling canvas — the identity
        /// <see cref="CrosshairSplit"/> and <see cref="DeathOverlaySplit"/> both lean on.
        /// </summary>
        private static void Place(RectTransform root, Rect view)
        {
            if (root == null)
                return;
            root.anchorMin = new Vector2(view.xMin, view.yMin);
            root.anchorMax = new Vector2(view.xMax, view.yMax);
            root.offsetMin = Vector2.zero;
            root.offsetMax = Vector2.zero;
            root.localScale = Vector3.one;
        }

        private static RectTransform NewRoot(Transform parent, int index)
        {
            var go = new GameObject("Splitscreen_Menu_P" + (index + 1), typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        // ------------------------------------------------------------------ event systems

        /// <summary>
        /// Give a rig its own event system, scoped to its own subtree and fed by its own player's
        /// device-filtered asset.
        ///
        /// <see cref="LocalPlayer.PawnControls"/>, never <c>Controls</c> — rule 2. That asset is
        /// replaced on every spawn, so the binding is re-made whenever its identity changes
        /// rather than assumed to hold for the life of the rig.
        /// </summary>
        private static void Bind(Rig rig, LocalPlayer player)
        {
            var controls = player.PawnControls;
            var asset = controls != null ? controls.asset : null;
            if (asset == null)
                return; // Between pawns: try again next frame.

            if (rig.Events != null && ReferenceEquals(rig.BoundAsset, asset))
            {
                // Keep the owner map honest as the roster shifts.
                _owners[rig.Events] = player.Index;
                return;
            }

            if (rig.Events == null)
            {
                // Built inactive so OnEnable cannot run until every reference is in place —
                // an InputSystemUIInputModule with no actionsAsset assigns itself a default set
                // of actions on enable, which would listen to every device in the room.
                var go = new GameObject("Splitscreen_Events_P" + (player.Index + 1));
                go.SetActive(false);
                Object.DontDestroyOnLoad(go);

                var events = go.AddComponent<MultiplayerEventSystem>();
                var module = go.AddComponent<InputSystemUIInputModule>();

                events.playerRoot = rig.Root.gameObject;
                rig.Events = events;
                rig.Module = module;
                _owners[events] = player.Index;

                PointAt(module, asset);
                rig.BoundAsset = asset;
                go.SetActive(true);

                Plugin.Log.LogInfo(
                    $"[menu] {player.Label}'s menu is driven by their own event system ({player.DescribeDevices()}).");
                return;
            }

            // The pawn respawned onto a fresh asset; follow it.
            PointAt(rig.Module, asset);
            rig.BoundAsset = asset;
            _owners[rig.Events] = player.Index;
            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo($"[menu] {player.Label}'s menu re-pointed at their new action asset.");
        }

        /// <summary>
        /// Wire one module to one asset, by name, explicitly.
        ///
        /// Not <c>actionsAsset</c> alone: its setter re-resolves each reference by the name of
        /// the reference already there, and a module built from scratch has none — so it would
        /// resolve nothing and the menu would take no input at all. Assigning the asset first
        /// stops the module inventing a default action set on enable; the references are then set
        /// one by one from the UI map <c>PlayerControls</c> already carries.
        /// </summary>
        private static void PointAt(InputSystemUIInputModule module, InputActionAsset asset)
        {
            if (module == null || asset == null)
                return;

            module.actionsAsset = asset;
            module.point = Ref(asset, "UI/Point");
            module.move = Ref(asset, "UI/Navigate");
            module.submit = Ref(asset, "UI/Submit");
            module.cancel = Ref(asset, "UI/Cancel");
            module.leftClick = Ref(asset, "UI/Click");
            module.rightClick = Ref(asset, "UI/RightClick");
            module.middleClick = Ref(asset, "UI/MiddleClick");
            module.scrollWheel = Ref(asset, "UI/ScrollWheel");
            module.trackedDevicePosition = Ref(asset, "UI/TrackedDevicePosition");
            module.trackedDeviceOrientation = Ref(asset, "UI/TrackedDeviceOrientation");
        }

        private static InputActionReference Ref(InputActionAsset asset, string path)
        {
            var action = asset.FindAction(path, false);
            return action != null ? InputActionReference.Create(action) : null;
        }

        /// <summary>
        /// Stand the game's own EventSystem down while the rigs are up, and bring it back after.
        /// Two event systems both driving produce doubled submits; the vanilla one is still the
        /// driver outside a split, so it is disabled rather than destroyed.
        /// </summary>
        private static void SilenceVanilla()
        {
            if (_vanillaEvents != null || _rigs.Count == 0)
                return;

            foreach (var events in Object.FindObjectsOfType<EventSystem>())
            {
                if (events == null || events is MultiplayerEventSystem)
                    continue;
                _vanillaEvents = events;
                events.enabled = false;
                Plugin.Log.LogInfo("[menu] the game's shared event system is standing down for the split.");
                return;
            }
        }

        private static void RestoreVanilla()
        {
            if (_vanillaEvents == null)
                return;
            _vanillaEvents.enabled = true;
            _vanillaEvents = null;
            Plugin.Log.LogInfo("[menu] the game's shared event system is back in charge.");
        }

        // ------------------------------------------------------------------ teardown

        private static void DropRigsFrom(int count)
        {
            var stale = new List<int>();
            foreach (var kv in _rigs)
            {
                if (kv.Key >= count && kv.Key != 0)
                    stale.Add(kv.Key);
            }
            foreach (int index in stale)
                DropRig(index);
        }

        private static void DropRig(int index)
        {
            Rig rig;
            if (!_rigs.TryGetValue(index, out rig))
                return;

            if (rig.Events != null)
            {
                _owners.Remove(rig.Events);
                Object.Destroy(rig.Events.gameObject);
            }
            if (index != 0 && rig.Root != null)
                Object.Destroy(rig.Root.gameObject);

            _rigs.Remove(index);
            Plugin.Log.LogInfo($"[menu] removed P{index + 1}'s menu.");
        }

        /// <summary>
        /// Put everything back: clones destroyed, player 1's menus returned to the parent and
        /// sibling positions the scene gave them, the game's event system back in charge.
        /// </summary>
        internal static void Teardown()
        {
            if (_rigs.Count == 0 && _vanillaEvents == null)
                return;

            var keys = new List<int>(_rigs.Keys);
            foreach (int index in keys)
            {
                if (index != 0)
                    DropRig(index);
            }

            Rig home;
            if (_rigs.TryGetValue(0, out home))
            {
                if (home.Events != null)
                {
                    _owners.Remove(home.Events);
                    Object.Destroy(home.Events.gameObject);
                }

                if (_homeParent != null)
                {
                    if (home.PauseMenu != null)
                    {
                        home.PauseMenu.transform.SetParent(_homeParent, false);
                        home.PauseMenu.transform.SetSiblingIndex(_homePauseSibling);
                    }
                    if (home.OptionsMenu != null)
                    {
                        home.OptionsMenu.transform.SetParent(_homeParent, false);
                        home.OptionsMenu.transform.SetSiblingIndex(_homeOptionsSibling);
                    }
                }

                if (home.Root != null)
                    Object.Destroy(home.Root.gameObject);
                _rigs.Remove(0);
                Plugin.Log.LogInfo("[menu] P1's menus are back where the scene had them.");
            }

            _owners.Clear();
            _homeParent = null;
            _homePauseSibling = -1;
            _homeOptionsSibling = -1;
            _builtAgainst = null;
            RestoreVanilla();
        }

        // ------------------------------------------------------------------ borrow

        /// <summary>A <c>PauseManager</c> pointed at one player's menu for one call.</summary>
        internal struct Borrowed
        {
            internal PauseManager Manager;
            internal GameObject SavedPause;
            internal GameObject SavedOptions;
            internal GameObject SavedResume;

            internal bool Active => Manager != null;
        }

        /// <summary>
        /// Point the three menu references <c>PauseManager</c> reaches through at this player's
        /// rig, let the game's own method run, and put them back.
        ///
        /// The same borrow <see cref="SettingsRouting"/> plays with the settings singleton, and
        /// for the same reason: <c>Menu</c> is a dozen lines of exactly correct menu logic and
        /// the only thing wrong with it for four players is *which* objects it reaches. Note this
        /// borrows fields on a MonoBehaviour, not the input static — see rule 1.
        ///
        /// Buttons wired to a GameObject argument need none of this: Instantiate handed each
        /// clone's button its own object already.
        /// </summary>
        internal static Borrowed Borrow(LocalPlayer player)
        {
            var pause = PauseManager.Instance;
            if (pause == null || player == null || !Active)
                return default(Borrowed);

            Rig rig;
            if (!_rigs.TryGetValue(player.Index, out rig) || !rig.Alive)
                return default(Borrowed);

            var borrowed = new Borrowed
            {
                Manager = pause,
                SavedPause = PauseMenuRef(pause),
                SavedOptions = OptionsMenuRef(pause),
                SavedResume = ResumeButtonRef(pause),
            };

            PauseMenuRef(pause) = rig.PauseMenu;
            OptionsMenuRef(pause) = rig.OptionsMenu;

            var resume = Counterpart(borrowed.SavedResume, rig);
            if (resume != null)
                ResumeButtonRef(pause) = resume;

            return borrowed;
        }

        internal static void Return(Borrowed borrowed)
        {
            if (!borrowed.Active)
                return;
            PauseMenuRef(borrowed.Manager) = borrowed.SavedPause;
            OptionsMenuRef(borrowed.Manager) = borrowed.SavedOptions;
            ResumeButtonRef(borrowed.Manager) = borrowed.SavedResume;
        }

        /// <summary>
        /// The object in <paramref name="rig"/> sitting where <paramref name="original"/> sits in
        /// player 1's. Every rig is a clone of P1's root, so the hierarchy is identical and a
        /// path is a reliable address — used for the resume button <c>PauseManager</c> selects on
        /// open, which would otherwise send every clone's focus to player 1's copy.
        /// </summary>
        private static GameObject Counterpart(GameObject original, Rig rig)
        {
            Rig home;
            if (original == null || rig == null || rig.Index == 0)
                return original;
            if (!_rigs.TryGetValue(0, out home) || !home.Alive || home.Root == null)
                return null;

            var steps = new List<int>();
            var t = original.transform;
            while (t != null && !ReferenceEquals(t, home.Root))
            {
                steps.Add(t.GetSiblingIndex());
                t = t.parent;
            }
            if (t == null)
                return null; // Not inside the menu at all — leave it shared.

            Transform cursor = rig.Root;
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                int step = steps[i];
                if (cursor == null || step >= cursor.childCount)
                    return null;
                cursor = cursor.GetChild(step);
            }
            return cursor != null ? cursor.gameObject : null;
        }
    }
}
