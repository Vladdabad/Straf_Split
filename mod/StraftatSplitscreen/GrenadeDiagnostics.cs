using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// ============================ TEMPORARY DIAGNOSTIC ============================
    /// Delete this whole file once the "picking up a grenade reveals an object that is
    /// normally invisible" bug is fixed. <see cref="RemovalChecklist"/> lists every place
    /// that has to go with it — there are only five, and none of them is load-bearing.
    /// ==============================================================================
    ///
    /// The bug report is "some object that is usually not visible becomes visible when I
    /// pick up a grenade". Nothing in the mod names that object, so the job here is to
    /// name it, and there are only a handful of ways a thing can go from unseen to seen:
    ///
    ///   1. its GameObject is activated            -> activeSelf / activeInHierarchy
    ///   2. its Renderer (or Light) is enabled     -> component.enabled
    ///   3. it changes layer onto a visible one    -> gameObject.layer
    ///   4. it is reparented out of a hidden rig   -> transform.parent
    ///   5. it did not exist a frame ago           -> a fresh Instantiate
    ///
    /// So: keep a per-frame picture of all five properties for every Renderer and Light in
    /// the scene, and log only transitions, and only inside a short window around a grenade
    /// interaction. Outside that window the picture is still maintained (that is what makes
    /// a "before" state exist at all) but nothing is printed — the log stays clean until the
    /// user actually touches a grenade.
    ///
    /// Two properties of the sampling matter, both learned the hard way in
    /// <see cref="RenderDiagnostics"/>:
    ///
    ///  - It samples in BOTH Update and LateUpdate, keeping the two histories apart. A
    ///    single sample per frame reported the torso meshes as rock steady while the screen
    ///    was visibly flashing, because the game switched them off in Update and the mod
    ///    switched them back on in LateUpdate.
    ///  - <see cref="PlayerVisibility"/> toggles Renderer.enabled during culling and restores
    ///    it in onPostRender, so both sample points always observe the restored state and
    ///    that per-camera work never shows up here as a transition.
    ///
    /// Events come from two independent sources on purpose, so a mistake in either one still
    /// leaves a working tool: Harmony postfixes on ItemBehaviour.OnGrab/OnDrop
    /// (<see cref="Patches.ItemBehaviour_OnGrab_Watch_Patch"/>), and a poll of what each local
    /// player is actually holding, which also catches the pin being pulled.
    /// </summary>
    internal static class GrenadeDiagnostics
    {
        /// <summary>
        /// What to delete when the bug is fixed:
        ///   1. this file
        ///   2. Patches/GrenadeWatchPatches.cs
        ///   3. the three Plugin.cs config binds (WatchGrenadePickup, GrenadeWatchSeconds,
        ///      GrenadeWatchAllPickups) and the log line that mentions them
        ///   4. the three call sites in SplitscreenCoordinator (Sample("update"),
        ///      Sample("late"), ReportSummary() under the dump key)
        ///   5. the "Grenade pickup watch" section in docs/SPLITSCREEN.md
        /// Nothing else references it. Grep for "gwatch" to confirm.
        /// </summary>
        internal const string RemovalChecklist = "see the doc comment on GrenadeDiagnostics";

        internal const string UpdatePhase = "update";
        internal const string LatePhase = "late";

        private static bool Enabled
        {
            get { return Plugin.WatchGrenadePickup != null && Plugin.WatchGrenadePickup.Value; }
        }

        // ------------------------------------------------------------------ tracked state

        private struct VisState
        {
            public bool ActiveInHierarchy;
            public bool ActiveSelf;
            public bool ComponentEnabled;
            public int Layer;
            public int ParentId;
            public int MaterialId;
        }

        private sealed class Watched
        {
            public GameObject Go;
            public Renderer Renderer;
            public Light Light;
            public string Name;
            public string Path;
            public VisState Update;
            public VisState Late;
            public bool SeenUpdate;
            public bool SeenLate;
        }

        /// <summary>Keyed by the Renderer's / Light's instance id, so one GameObject with
        /// several renderers is tracked as several entries — which is what you want, since
        /// only one of them may be the thing that lit up.</summary>
        private static readonly Dictionary<int, Watched> _watched = new Dictionary<int, Watched>();
        private static readonly List<int> _dead = new List<int>();

        private static float _nextScan;

        // ------------------------------------------------------------------ window state

        private static float _armedUntil;
        private static bool _windowOpen;
        private static float _windowStart;
        private static int _windowLines;
        private static int _totalLines;
        private static int _events;
        private static bool _capWarned;

        private const int MaxLinesPerWindow = 150;
        private const int MaxLinesTotal = 3000;
        private const int MaxSummaryRows = 60;

        private sealed class Change
        {
            public int Count;
            public string Last;
            public string Path;
            public int FirstFrame;
        }

        /// <summary>Changes inside the window currently open.</summary>
        private static readonly Dictionary<string, Change> _windowChanges = new Dictionary<string, Change>();

        /// <summary>Every change ever logged, so one report at the end names the repeat offenders.</summary>
        private static readonly Dictionary<string, Change> _allChanges = new Dictionary<string, Change>();

        // ------------------------------------------------------------------ events

        /// <summary>
        /// Something happened to an item. Logs it, and opens (or extends) the window in which
        /// state transitions are printed. Safe to call for non-grenades: they are the control
        /// group, and <c>GrenadeWatchAllPickups</c> turns them off.
        /// </summary>
        internal static void Note(string who, string what, GameObject item)
        {
            if (!Enabled)
                return;

            try
            {
                bool grenade = IsGrenade(item);
                if (!grenade && !Plugin.GrenadeWatchAllPickups.Value)
                    return;

                _events++;
                if (!_windowOpen)
                {
                    _windowOpen = true;
                    _windowStart = Time.unscaledTime;
                    _windowLines = 0;
                    _windowChanges.Clear();
                }
                _armedUntil = Time.unscaledTime + Mathf.Max(0.5f, Plugin.GrenadeWatchSeconds.Value);

                Plugin.Log.LogInfo(
                    $"[gwatch/event] frame {Time.frameCount} {who} {what}: {Describe(item)}");
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>
        /// Which player is holding this item, by its own back-reference to the pawn. Falls
        /// back to lastPlayerHolder because rootObject is already null by the time a drop is
        /// reported, and to a plain label because with no local players in the roster (vanilla
        /// single player, which is the comparison case) there is nobody to name.
        /// </summary>
        internal static string HolderOf(ItemBehaviour behaviour)
        {
            if (behaviour == null)
                return "?";
            var pawn = behaviour.rootObject != null ? behaviour.rootObject : behaviour.lastPlayerHolder;
            var player = LocalPlayers.OwningPawn(pawn);
            return player != null ? player.Label : (pawn != null ? pawn.name : "?");
        }

        private static string Describe(GameObject item)
        {
            if (item == null)
                return "(nothing)";

            var behaviour = item.GetComponent<ItemBehaviour>();
            var weapon = item.GetComponent<Weapon>();
            return
                $"'{item.name}'{(IsGrenade(item) ? " GRENADE" : "")} " +
                $"[{(weapon != null ? weapon.GetType().Name : "no Weapon")}] " +
                $"weaponName={(behaviour != null ? behaviour.weaponName : "-")} " +
                $"layer={item.layer} tag={item.tag} " +
                $"renderers={item.GetComponentsInChildren<Renderer>(true).Length} " +
                $"parent={(item.transform.parent != null ? item.transform.parent.name : "SCENE ROOT")}";
        }

        private static readonly FieldInfo GrenadeFlag = AccessTools.Field(typeof(DualLauncher), "grenade");

        /// <summary>
        /// Three tests, because none of them alone is reliable: the thrown physics grenade
        /// carries the "Grenade" tag (Weapon.cs tests exactly that), the held launcher is a
        /// DualLauncher with its private <c>grenade</c> flag set, and the pickup prompt name
        /// is the last resort for anything the first two miss.
        /// </summary>
        internal static bool IsGrenade(GameObject item)
        {
            if (item == null)
                return false;

            try
            {
                if (item.CompareTag("Grenade"))
                    return true;
            }
            catch (Exception)
            {
                // An undefined tag throws rather than returning false; not fatal here.
            }

            var launcher = item.GetComponent<DualLauncher>();
            if (launcher != null && GrenadeFlag != null && (bool)GrenadeFlag.GetValue(launcher))
                return true;

            var behaviour = item.GetComponent<ItemBehaviour>();
            return behaviour != null && !string.IsNullOrEmpty(behaviour.weaponName) &&
                   behaviour.weaponName.ToLower().Contains("grenade");
        }

        // ------------------------------------------------------------------ the tick

        /// <summary>
        /// One sample. Called twice a frame — from the coordinator's Update tick and its
        /// LateUpdate tick — with the phase kept in the key, so a property that oscillates
        /// between the two phases reads as steady under each and the oscillation is visible
        /// as two different steady values rather than as silence.
        /// </summary>
        internal static void Sample(string phase)
        {
            if (!Enabled)
                return;

            try
            {
                bool armed = Time.unscaledTime < _armedUntil;

                if (phase == UpdatePhase)
                {
                    // Polling first: it is one of the two event sources, and a pickup noticed
                    // here arms the window before this very frame is diffed.
                    PollHeldItems();
                    armed = Time.unscaledTime < _armedUntil;

                    // Ten times a second while armed, so a VFX that lives a third of a second
                    // is still caught, and twice a second otherwise. Not every frame: this is
                    // the expensive half, and a scan-induced stutter during the very window
                    // the player is trying to look at the bug in would be self-defeating. The
                    // per-frame half is the diff below, which is the one that matters.
                    if (Time.unscaledTime >= _nextScan)
                    {
                        _nextScan = Time.unscaledTime + (armed ? 0.1f : 0.5f);
                        Scan(armed);
                    }
                }

                Diff(phase, armed);

                // Closed on the late phase so the frame that ends the window is fully diffed
                // in both phases before the summary prints.
                if (phase == LatePhase && _windowOpen && !armed)
                {
                    _windowOpen = false;
                    ReportWindow();
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        /// <summary>
        /// Find renderers and lights that are not tracked yet. Resources.FindObjectsOfTypeAll
        /// rather than FindObjectsOfType, deliberately: the object this hunt is looking for is
        /// by definition normally NOT visible, and quite possibly sitting on a deactivated
        /// GameObject, which the ordinary lookup skips entirely. The scene filter drops the
        /// prefab assets and editor-only objects that come back with it.
        /// </summary>
        private static void Scan(bool armed)
        {
            // The very first scan is the baseline: everything in the map is "new" then, and
            // announcing all of it would bury the window in thousands of CREATED lines.
            bool announce = armed && _baselineTaken;

            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (r == null || !r.gameObject.scene.IsValid() || r.hideFlags != HideFlags.None)
                    continue;
                Track(r.GetInstanceID(), r.gameObject, r, null, announce);
            }

            foreach (var l in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (l == null || !l.gameObject.scene.IsValid() || l.hideFlags != HideFlags.None)
                    continue;
                Track(l.GetInstanceID(), l.gameObject, null, l, announce);
            }

            _baselineTaken = true;
        }

        private static bool _baselineTaken;

        private static void Track(int key, GameObject go, Renderer renderer, Light light, bool armed)
        {
            if (_watched.ContainsKey(key))
                return;

            var w = new Watched
            {
                Go = go,
                Renderer = renderer,
                Light = light,
                Name = go.name,
                Path = PathOf(go.transform),
            };
            w.Update = Capture(w);
            w.Late = w.Update;
            w.SeenUpdate = true;
            w.SeenLate = true;
            _watched[key] = w;

            // Only interesting inside a window: at that point it means something was created
            // by the interaction itself, which is transition type 5.
            if (armed)
                Log(UpdatePhase, w, $"CREATED ({(renderer != null ? "renderer" : "light")}) {Snapshot(w.Update)}");
        }

        private static void Diff(string phase, bool armed)
        {
            bool late = phase == LatePhase;
            _dead.Clear();

            foreach (var pair in _watched)
            {
                var w = pair.Value;

                // Unity's fake null: the component or its object was destroyed.
                if (w.Go == null || (w.Renderer == null && w.Light == null))
                {
                    _dead.Add(pair.Key);
                    if (armed)
                        Log(phase, w, "DESTROYED");
                    continue;
                }

                var now = Capture(w);
                var was = late ? w.Late : w.Update;
                bool seen = late ? w.SeenLate : w.SeenUpdate;

                if (late)
                {
                    w.Late = now;
                    w.SeenLate = true;
                }
                else
                {
                    w.Update = now;
                    w.SeenUpdate = true;
                }

                if (!seen || !armed)
                    continue;

                string changes = Differences(was, now);
                if (changes != null)
                    Log(phase, w, changes);
            }

            for (int i = 0; i < _dead.Count; i++)
                _watched.Remove(_dead[i]);
        }

        private static VisState Capture(Watched w)
        {
            var go = w.Go;
            var parent = go.transform.parent;
            var mat = w.Renderer != null ? w.Renderer.sharedMaterial : null;

            return new VisState
            {
                ActiveInHierarchy = go.activeInHierarchy,
                ActiveSelf = go.activeSelf,
                ComponentEnabled = w.Renderer != null ? w.Renderer.enabled : (w.Light != null && w.Light.enabled),
                Layer = go.layer,
                ParentId = parent != null ? parent.GetInstanceID() : 0,
                MaterialId = mat != null ? mat.GetInstanceID() : 0,
            };
        }

        private static string Differences(VisState was, VisState now)
        {
            if (was.ActiveInHierarchy == now.ActiveInHierarchy &&
                was.ActiveSelf == now.ActiveSelf &&
                was.ComponentEnabled == now.ComponentEnabled &&
                was.Layer == now.Layer &&
                was.ParentId == now.ParentId &&
                was.MaterialId == now.MaterialId)
            {
                return null;
            }

            var sb = new StringBuilder();
            if (was.ActiveInHierarchy != now.ActiveInHierarchy)
                sb.Append($"active {Yn(was.ActiveInHierarchy)}->{Yn(now.ActiveInHierarchy)} ");
            if (was.ActiveSelf != now.ActiveSelf)
                sb.Append($"activeSelf {Yn(was.ActiveSelf)}->{Yn(now.ActiveSelf)} ");
            if (was.ComponentEnabled != now.ComponentEnabled)
                sb.Append($"enabled {Yn(was.ComponentEnabled)}->{Yn(now.ComponentEnabled)} ");
            if (was.Layer != now.Layer)
                sb.Append($"layer {was.Layer}->{now.Layer} ");
            if (was.ParentId != now.ParentId)
                sb.Append("reparented ");
            if (was.MaterialId != now.MaterialId)
                sb.Append("material changed ");

            // The one line that answers the actual question, spelled out rather than left to
            // be inferred from three columns.
            bool wasVisible = was.ActiveInHierarchy && was.ComponentEnabled;
            bool nowVisible = now.ActiveInHierarchy && now.ComponentEnabled;
            if (!wasVisible && nowVisible)
                sb.Append("<== BECAME DRAWABLE");
            else if (wasVisible && !nowVisible)
                sb.Append("(went dark)");

            return sb.ToString().TrimEnd();
        }

        private static string Snapshot(VisState s)
        {
            return $"active={Yn(s.ActiveInHierarchy)} enabled={Yn(s.ComponentEnabled)} layer={s.Layer}";
        }

        private static string Yn(bool b)
        {
            return b ? "on" : "off";
        }

        // ------------------------------------------------------------------ held-item poll

        /// <summary>What each hand held last time we looked; key = player index * 2 + hand.</summary>
        private static readonly Dictionary<int, GameObject> _hands = new Dictionary<int, GameObject>();

        /// <summary>Whether each grenade's pin has been pulled, keyed by item instance id.</summary>
        private static readonly Dictionary<int, bool> _pinPulled = new Dictionary<int, bool>();

        /// <summary>
        /// The second, patch-independent event source. It also covers the two things a
        /// pickup patch cannot see: a hand being emptied by the game rather than by the
        /// player (RightHandFix drops a weapon on its own), and the pin coming out, which is
        /// a SyncVar flip with no method of its own worth patching.
        /// </summary>
        private static void PollHeldItems()
        {
            if (LocalPlayers.Count > 0)
            {
                foreach (var player in LocalPlayers.All)
                    PollPawn(player.Index, player.Label, player.Pawn);
            }
            else
            {
                // No roster yet — vanilla single player, which is the case this whole hunt has
                // to be compared against. The game's own singleton is the one pawn there is.
                var controller = FirstPersonController.instance;
                PollPawn(0, "P1", controller != null ? controller.gameObject : null);
            }

            if (_pinPulled.Count > 64)
                _pinPulled.Clear();
        }

        private static void PollPawn(int index, string label, GameObject pawn)
        {
            var pickup = pawn != null ? pawn.GetComponent<PlayerPickup>() : null;
            if (pickup == null)
                return;

            // The generated getters, not the decompiled SyncAccessor_* properties: those
            // are not callable from a mod.
            CheckHand(index, label, pickup.sync___get_value_objInHand(), true);
            CheckHand(index, label, pickup.sync___get_value_objInLeftHand(), false);
        }

        private static void CheckHand(int index, string label, GameObject held, bool right)
        {
            int key = index * 2 + (right ? 0 : 1);
            string hand = right ? "right hand" : "left hand";

            GameObject was;
            if (!_hands.TryGetValue(key, out was))
            {
                // First look at this hand — no transition to report, only a baseline.
                _hands[key] = held;
            }
            else if (was == null && held == null)
            {
                // Unity's == null is true for a DESTROYED object as well as a missing one, so
                // this is also what stops a map change (which destroys whatever was in hand)
                // from reading as "the hand was emptied".
                _hands[key] = null;
            }
            else if (!ReferenceEquals(was, held))
            {
                _hands[key] = held;
                if (held == null)
                    Note(label, hand + " emptied", was);
                else
                    Note(label, "took in " + hand, held);
            }

            if (held == null)
                return;

            var launcher = held.GetComponent<DualLauncher>();
            if (launcher == null)
                return;

            // Public backing field of the [SyncVar]; reading it avoids the accessor entirely.
            bool open = launcher.grenadeOpen;
            int id = held.GetInstanceID();
            bool wasOpen;
            if (_pinPulled.TryGetValue(id, out wasOpen) && wasOpen != open)
                Note(label, open ? "pulled the pin" : "grenade reset", held);
            _pinPulled[id] = open;
        }

        // ------------------------------------------------------------------ output

        private static void Log(string phase, Watched w, string what)
        {
            if (_totalLines >= MaxLinesTotal || _windowLines >= MaxLinesPerWindow)
            {
                if (!_capWarned)
                {
                    _capWarned = true;
                    Plugin.Log.LogWarning(
                        "[gwatch] line cap reached — further transitions are counted in the summary " +
                        "but not printed.");
                }
                RecordOnly(w, what);
                return;
            }

            _windowLines++;
            _totalLines++;
            Plugin.Log.LogWarning(
                $"[gwatch/{phase}] frame {Time.frameCount} +{Time.unscaledTime - _windowStart:F2}s " +
                $"{w.Name}: {what}   ({w.Path})");
            RecordOnly(w, what);
        }

        private static void RecordOnly(Watched w, string what)
        {
            Record(_windowChanges, w, what);
            Record(_allChanges, w, what);
        }

        private static void Record(Dictionary<string, Change> into, Watched w, string what)
        {
            Change change;
            if (!into.TryGetValue(w.Path, out change))
            {
                change = new Change { Path = w.Path, FirstFrame = Time.frameCount };
                into[w.Path] = change;
            }
            change.Count++;
            change.Last = what;
        }

        /// <summary>Printed when a window closes: everything that moved while the player was
        /// touching the item, ranked, so the culprit is read off rather than scrolled for.</summary>
        private static void ReportWindow()
        {
            if (_windowChanges.Count == 0)
            {
                Plugin.Log.LogInfo("[gwatch] window closed — nothing changed state.");
                return;
            }

            Plugin.Log.LogInfo($"---------- gwatch: {_windowChanges.Count} object(s) changed during that interaction ----------");
            Dump(_windowChanges);
            Plugin.Log.LogInfo("---------- end gwatch window ----------");
        }

        /// <summary>Cumulative report, on the dump key, across every interaction so far.</summary>
        internal static void ReportSummary()
        {
            if (!Enabled)
            {
                Plugin.Log.LogInfo("[gwatch] grenade watch is off (Debug/WatchGrenadePickup).");
                return;
            }

            Plugin.Log.LogInfo("=============== GRENADE WATCH SUMMARY ===============");
            Plugin.Log.LogInfo(
                $"{_events} item interaction(s) seen, {_allChanges.Count} object(s) ever changed state, " +
                $"{_watched.Count} renderer/light(s) tracked.");
            Dump(_allChanges);
            Plugin.Log.LogInfo(
                "Temporary: set Debug/WatchGrenadePickup=false to silence, and delete " +
                "GrenadeDiagnostics.cs + Patches/GrenadeWatchPatches.cs once the bug is fixed.");
            Plugin.Log.LogInfo("============= END GRENADE WATCH SUMMARY =============");
        }

        private static void Dump(Dictionary<string, Change> changes)
        {
            var rows = new List<Change>(changes.Values);
            rows.Sort((a, b) => b.Count.CompareTo(a.Count));

            int printed = 0;
            foreach (var row in rows)
            {
                if (printed++ >= MaxSummaryRows)
                {
                    Plugin.Log.LogInfo($"  ... and {rows.Count - MaxSummaryRows} more.");
                    break;
                }
                Plugin.Log.LogInfo($"  {row.Count,4}x  {row.Path}   last: {row.Last}");
            }
        }

        private static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        private static void Fail(Exception ex)
        {
            Plugin.Log.LogError($"[gwatch] threw, disabling the grenade watch: {ex}");
            if (Plugin.WatchGrenadePickup != null)
                Plugin.WatchGrenadePickup.Value = false;
        }

        // No Reset(): a map change destroys its objects, the diff pass notices them as
        // DESTROYED and drops them, and the next scan picks up the new map. Nothing has to
        // remember to call anything.
    }
}
