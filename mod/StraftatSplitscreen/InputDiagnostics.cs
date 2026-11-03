using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Input forensics for "the wrong device is driving this player".
    ///
    /// Ordinary logging reports what the mod *intended* rather than what a pawn ended up
    /// holding, so every failure mode looks identical. This module reports observed state
    /// only, and is built to separate three causes:
    ///
    ///   A. ALIASING  — two players' pawns captured the same PlayerControls (the
    ///                  InputManager.inputActions swap in PlayerManager.SpawnPlayer did not
    ///                  take, or was undone). Proof: their cached InputAction objects are the
    ///                  same instance, or one asset's MouseX.performed lists both pawns.
    ///   B. DEVICES   — the assets are distinct but one is still bound to controls on the
    ///                  wrong device. Proof: an action resolves to a keyboard/mouse control,
    ///                  or its activeControl sits on another player's pad.
    ///   C. NOT-INPUT — a pawn's actions read zero and are bound to nothing, yet it still
    ///                  moves. Then input is not the channel at all and no amount of device
    ///                  filtering will help.
    ///
    /// <see cref="Report"/> answers A and B on demand; <see cref="Trace"/> answers C by
    /// logging each pawn's input value next to how far that pawn actually moved.
    /// </summary>
    internal static class InputDiagnostics
    {
        // ------------------------------------------------------------------ identity

        /// <summary>
        /// Reference identity, stable for the object's lifetime and independent of any
        /// Equals/GetHashCode override — including Unity's, which reports a destroyed object
        /// as null. Two things printing the same Id ARE the same object.
        /// </summary>
        internal static string Id(object o)
        {
            return o == null ? "null" : RuntimeHelpers.GetHashCode(o).ToString("X6");
        }

        private static readonly List<InputActionAsset> _assets = new List<InputActionAsset>();

        /// <summary>Short stable name per asset seen this session, so a third unexpected one is obvious.</summary>
        internal static string AssetLabel(InputActionAsset asset)
        {
            if (asset == null)
                return "none";
            for (int i = 0; i < _assets.Count; i++)
            {
                if (ReferenceEquals(_assets[i], asset))
                    return "A" + i;
            }
            _assets.Add(asset);
            return "A" + (_assets.Count - 1);
        }

        private static InputActionAsset AssetOf(PlayerControls controls)
        {
            return controls == null ? null : controls.asset;
        }

        /// <summary>
        /// Whose action set is this? The label of the player it was created for, "shared" for
        /// the asset the menus read, or ORPHAN for one nobody is filtering — which is the
        /// state that lets a stray device drive a pawn.
        /// </summary>
        private static string Role(PlayerControls controls)
        {
            if (controls == null)
                return "NONE";
            var asset = AssetOf(controls);

            foreach (var player in LocalPlayers.All)
            {
                if (player.Controls != null && ReferenceEquals(asset, player.Controls.asset))
                    return player.Label;
            }

            var shared = InputManager.inputActions;
            if (shared != null && ReferenceEquals(asset, shared.asset))
                return "shared";

            return "ORPHAN";
        }

        internal static string DescribeDevices(PlayerControls controls)
        {
            if (controls == null)
                return "null controls";
            var devices = controls.asset.devices;
            if (!devices.HasValue)
                return "ALL (unrestricted)";
            var names = new List<string>();
            foreach (var d in devices.Value)
                names.Add(d.displayName);
            return names.Count == 0 ? "none" : string.Join(", ", names.ToArray());
        }

        // ------------------------------------------------------------------ reflection

        private static readonly Dictionary<string, FieldInfo> _fieldCache = new Dictionary<string, FieldInfo>();

        private static object GetField(object obj, string name)
        {
            if (obj == null)
                return null;
            Type t = obj.GetType();
            string key = t.FullName + "." + name;
            FieldInfo f;
            if (!_fieldCache.TryGetValue(key, out f))
            {
                f = AccessTools.Field(t, name);
                _fieldCache[key] = f;
            }
            return f == null ? null : f.GetValue(obj);
        }

        /// <summary>
        /// The PlayerControls a component captured in Awake, whatever its access level.
        /// Reflected by name rather than through a cached FieldRef so a renamed field in a
        /// future game patch degrades to "null" in one log line instead of throwing a type
        /// initializer exception that takes the whole plugin down at load.
        /// </summary>
        internal static PlayerControls ControlsOf(Component c)
        {
            return GetField(c, "playerControls") as PlayerControls;
        }

        // ------------------------------------------------------------------ pawn model

        private struct Pawn
        {
            public GameObject Root;
            public FirstPersonController Fpc;
            public LocalPlayer Player;   // null for a remote player's pawn
            public string Who;
        }

        /// <summary>
        /// Every pawn in the scene, labelled by roster position. A remote player's pawn is in
        /// here too and must not be mistaken for a local one — it holds no local action asset,
        /// so counting it would produce a confident and completely wrong verdict.
        /// </summary>
        private static List<Pawn> Pawns()
        {
            var list = new List<Pawn>();
            foreach (var fpc in UnityEngine.Object.FindObjectsOfType<FirstPersonController>())
            {
                var player = LocalPlayers.OwningPawn(fpc.gameObject);
                list.Add(new Pawn
                {
                    Root = fpc.gameObject,
                    Fpc = fpc,
                    Player = player,
                    Who = player != null ? player.Label : "REMOTE",
                });
            }
            return list;
        }

        private static readonly string[] TrackedActions =
        {
            "move", "lookX", "lookY", "jump", "run", "crouch", "fire1", "fire2", "reload",
        };

        private static InputAction ActionOf(FirstPersonController fpc, string field)
        {
            return GetField(fpc, field) as InputAction;
        }

        // ------------------------------------------------------------------ report

        internal static void Report()
        {
            try
            {
                Plugin.Log.LogInfo("=============== INPUT DIAGNOSTIC ===============");
                ReportDevices();
                ReportAssets();
                ReportPawns();
                ReportAliasing();
                ReportVerdict();
                Plugin.Log.LogInfo("============= END INPUT DIAGNOSTIC =============");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Input diagnostic threw: {ex}");
            }
        }

        private static void ReportDevices()
        {
            var sb = new StringBuilder();
            foreach (var d in InputSystem.devices)
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append($"{d.displayName}({d.GetType().Name},id={d.deviceId},enabled={d.enabled})");
            }
            Plugin.Log.LogInfo($"[devices] system=[{sb}]");

            // Assignment is positional: pad 0 drives P1, pad 1 drives P2, and so on.
            var pads = Gamepad.all;
            for (int i = 0; i < pads.Count; i++)
                Plugin.Log.LogInfo($"[devices] gamepad[{i}] '{pads[i].displayName}' -> P{i + 1}");
            if (pads.Count < LocalPlayers.Count)
                Plugin.Log.LogWarning(
                    $"[devices] {pads.Count} gamepad(s) for {LocalPlayers.Count} local player(s) — " +
                    $"P{pads.Count + 1} and up are waiting for a controller.");
        }

        private static void ReportAssets()
        {
            var shared = InputManager.inputActions;
            DescribeAsset("shared (InputManager.inputActions, menus)", shared);

            foreach (var player in LocalPlayers.All)
                DescribeAsset($"{player.Label} installed", player.Controls);

            foreach (var pawn in Pawns())
            {
                var controls = ControlsOf(pawn.Fpc);
                if (controls != null && Role(controls) == "ORPHAN")
                    DescribeAsset($"ORPHAN held by {pawn.Who}", controls);
            }
        }

        private static void DescribeAsset(string label, PlayerControls controls)
        {
            if (controls == null)
            {
                Plugin.Log.LogInfo($"[asset] {label}: NULL");
                return;
            }
            var asset = controls.asset;
            Plugin.Log.LogInfo(
                $"[asset] {label}: {AssetLabel(asset)} wrapper#{Id(controls)} asset#{Id(asset)} " +
                $"enabled={(asset != null && asset.enabled)} devices=[{DescribeDevices(controls)}]");
        }

        private static void ReportPawns()
        {
            var pawns = Pawns();
            Plugin.Log.LogInfo($"[pawns] {pawns.Count} FirstPersonController(s), {LocalPlayers.Count} local player(s)");

            foreach (var pawn in pawns)
            {
                Plugin.Log.LogInfo(
                    $"[pawn] {pawn.Who} '{pawn.Root.name}'#{Id(pawn.Root)} " +
                    $"enabled={pawn.Fpc.enabled} activeInHierarchy={pawn.Root.activeInHierarchy}" +
                    $"{(pawn.Player != null ? " device=" + (pawn.Player.Device != null ? pawn.Player.Device.displayName : "NONE") : "")}");

                // Every component that captures its own copy of the action set. If these
                // disagree, only part of the pawn was rewired — which looks like a partial bug
                // (moves with the pad but shoots with the mouse).
                ReportComponentBinding(pawn.Fpc);
                ReportComponentBinding(pawn.Root.GetComponent<PlayerShoot>());
                ReportComponentBinding(pawn.Root.GetComponent<PlayerPickup>());
                foreach (var arms in pawn.Root.GetComponentsInChildren<FPArms>(true))
                    ReportComponentBinding(arms);

                foreach (string field in TrackedActions)
                    ReportAction(field, ActionOf(pawn.Fpc, field));

                var mouseInput = GetField(pawn.Fpc, "mouseInput");
                Plugin.Log.LogInfo($"    mouseInput={mouseInput} (set only by the MouseX/MouseY performed lambdas)");
            }
        }

        private static void ReportComponentBinding(Component c)
        {
            if (c == null)
                return;
            var controls = ControlsOf(c);
            Plugin.Log.LogInfo(
                $"    {c.GetType().Name,-22} controls={AssetLabel(AssetOf(controls))} " +
                $"role={Role(controls)} wrapper#{Id(controls)} devices=[{DescribeDevices(controls)}]");
        }

        private static void ReportAction(string field, InputAction action)
        {
            if (action == null)
            {
                Plugin.Log.LogInfo($"    action {field,-7} NULL (OnEnable has not run on this pawn)");
                return;
            }

            // The asset that actually owns this action can differ from the pawn's
            // playerControls field if OnEnable re-read the static after Awake.
            InputActionAsset owner = action.actionMap != null ? action.actionMap.asset : null;

            string value;
            try { value = Convert.ToString(action.ReadValueAsObject()); }
            catch (Exception ex) { value = "<" + ex.GetType().Name + ">"; }

            var active = action.activeControl;
            string activeStr = active == null ? "none" : $"{active.path} on {active.device.displayName}";

            Plugin.Log.LogInfo(
                $"    action {field,-7} #{Id(action)} owner={AssetLabel(owner)} enabled={action.enabled} " +
                $"value={value} active={activeStr} bound=[{DescribeControls(action)}]");
        }

        /// <summary>
        /// The controls an action resolved to AFTER device filtering — the direct answer to
        /// "is the keyboard still wired to this player". A restriction that failed to apply
        /// shows up here as Keyboard/Mouse entries, or as another player's pad.
        /// </summary>
        private static string DescribeControls(InputAction action)
        {
            var sb = new StringBuilder();
            int shown = 0;
            foreach (var control in action.controls)
            {
                if (shown++ == 6)
                {
                    sb.Append(", ...");
                    break;
                }
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(control.device.GetType().Name).Append('/').Append(control.name);
            }
            return sb.Length == 0 ? "NOTHING" : sb.ToString();
        }

        // ------------------------------------------------------------------ aliasing

        /// <summary>
        /// The decisive comparison. If two pawns hold the same InputAction object then they
        /// are literally reading one action and no device split can separate them, whatever
        /// the asset's device list says. Checked across every pair, so it scales with the
        /// roster instead of testing one hard-coded pairing.
        /// </summary>
        private static void ReportAliasing()
        {
            var local = new List<Pawn>();
            foreach (var pawn in Pawns())
            {
                if (pawn.Player != null)
                    local.Add(pawn);
            }

            for (int i = 0; i < local.Count; i++)
            {
                for (int j = i + 1; j < local.Count; j++)
                {
                    var a = ControlsOf(local[i].Fpc);
                    var b = ControlsOf(local[j].Fpc);
                    bool sameAsset = a != null && b != null && ReferenceEquals(AssetOf(a), AssetOf(b));
                    Plugin.Log.LogInfo(
                        $"[alias] {local[i].Who}={AssetLabel(AssetOf(a))} {local[j].Who}={AssetLabel(AssetOf(b))} " +
                        $"=> {(sameAsset ? "SAME ASSET — cause A confirmed" : "distinct")}");

                    foreach (string field in TrackedActions)
                    {
                        var x = ActionOf(local[i].Fpc, field);
                        var y = ActionOf(local[j].Fpc, field);
                        if (x != null && y != null && ReferenceEquals(x, y))
                            Plugin.Log.LogWarning(
                                $"[alias] action '{field}' ALIASED between {local[i].Who} and {local[j].Who} " +
                                $"— both read InputAction#{Id(x)}");
                    }
                }
            }

            // Look subscriptions can never be unhooked, so they accumulate on whichever asset
            // each pawn captured. Listing the subscribers names the guilty pawn directly.
            foreach (var asset in new List<InputActionAsset>(_assets))
            {
                if (asset == null)
                    continue;
                ReportListeners(asset, "Player/MouseX");
                ReportListeners(asset, "Player/MouseY");
            }
        }

        private static void ReportListeners(InputActionAsset asset, string actionPath)
        {
            InputAction action;
            try { action = asset.FindAction(actionPath, false); }
            catch { return; }
            if (action == null)
                return;

            var targets = PerformedListeners(action);
            if (targets == null)
            {
                Plugin.Log.LogInfo($"[listen] {AssetLabel(asset)} {actionPath}: <could not read m_OnPerformed>");
                return;
            }

            var sb = new StringBuilder();
            foreach (var t in targets)
            {
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(t);
            }
            Plugin.Log.LogInfo($"[listen] {AssetLabel(asset)} {actionPath}: {targets.Count} performed subscriber(s) — {sb}");
        }

        /// <summary>
        /// Describe every 'performed' subscriber by the object that owns the closure. The
        /// MouseX/MouseY lambdas in FirstPersonController.Awake and FPArms.Awake capture only
        /// 'this', so the compiler emits them as instance methods and the delegate target IS
        /// the component — identifiable right down to its pawn.
        /// </summary>
        private static List<string> PerformedListeners(InputAction action)
        {
            try
            {
                object callbackArray = GetField(action, "m_OnPerformed");
                if (callbackArray == null)
                    return null;
                object inlined = GetField(callbackArray, "m_Callbacks");
                if (inlined == null)
                    return null;

                object lengthObj = GetField(inlined, "length");
                if (lengthObj == null)
                    return null;
                int length = Convert.ToInt32(lengthObj);

                var first = GetField(inlined, "firstValue") as Delegate;
                var extra = GetField(inlined, "additionalValues") as Delegate[];

                var result = new List<string>(length);
                for (int i = 0; i < length; i++)
                {
                    Delegate d = i == 0 ? first : (extra != null && i - 1 < extra.Length ? extra[i - 1] : null);
                    result.Add(DescribeDelegate(d));
                }
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"listener read failed ({ex.GetType().Name}) — Input System internals differ from expected");
                return null;
            }
        }

        private static string DescribeDelegate(Delegate d)
        {
            if (d == null)
                return "<null>";
            object target = d.Target;
            if (target == null)
                return $"static {d.Method.DeclaringType?.Name}.{d.Method.Name}";

            var comp = target as Component;
            if (comp == null)
                return $"{target.GetType().Name}.{d.Method.Name}";

            // Unity's == null is true for a destroyed object whose managed shell the delegate
            // is still keeping alive: a leaked subscription from an old pawn.
            if (comp == null || comp.gameObject == null)
                return $"{target.GetType().Name}#{Id(target)} DESTROYED (leaked subscription)";

            GameObject root = comp.transform.root.gameObject;
            var player = LocalPlayers.OwningPawn(root);
            return $"{comp.GetType().Name} on '{root.name}'#{Id(root)} [{(player != null ? player.Label : "REMOTE")}]";
        }

        // ------------------------------------------------------------------ verdict

        /// <summary>
        /// State the conclusion the numbers support, per player, so the log answers the
        /// question rather than merely containing the answer.
        /// </summary>
        private static void ReportVerdict()
        {
            foreach (var pawn in Pawns())
            {
                if (pawn.Player == null)
                    continue;

                var controls = ControlsOf(pawn.Fpc);
                string role = Role(controls);

                if (role != pawn.Player.Label)
                {
                    Plugin.Log.LogError(
                        $"[verdict] {pawn.Who} CAUSE A: its pawn holds {role} rather than its own action " +
                        "asset. The InputManager.inputActions swap around PlayerManager.SpawnPlayer did " +
                        "not reach this pawn — device filtering cannot separate it from whoever else " +
                        "holds that asset.");
                    continue;
                }

                var expected = pawn.Player.Device;
                int wrongDevice = 0;
                foreach (string field in TrackedActions)
                {
                    var action = ActionOf(pawn.Fpc, field);
                    if (action == null)
                        continue;
                    foreach (var control in action.controls)
                    {
                        if (expected == null || !ReferenceEquals(control.device, expected))
                            wrongDevice++;
                    }
                }

                if (expected == null)
                {
                    Plugin.Log.LogInfo(
                        $"[verdict] {pawn.Who} has no controller assigned — deliberately unbound until one " +
                        $"is plugged in. {(wrongDevice > 0 ? wrongDevice + " control(s) are still resolved and SHOULD NOT BE." : "Bound to nothing, as intended.")}");
                }
                else if (wrongDevice > 0)
                {
                    Plugin.Log.LogError(
                        $"[verdict] {pawn.Who} CAUSE B: holds its own asset but {wrongDevice} control(s) " +
                        $"resolve to a device other than '{expected.displayName}' — the restriction is not " +
                        "reaching the asset this pawn actually reads.");
                }
                else
                {
                    Plugin.Log.LogInfo(
                        $"[verdict] {pawn.Who} correctly isolated on '{expected.displayName}'. If it still " +
                        $"mirrors another player, the channel is NOT the Input System — run the live trace " +
                        $"({Plugin.TraceKey.Value}) and compare input values against actual movement.");
                }
            }
        }

        // ------------------------------------------------------------- live trace

        internal static bool TraceEnabled { get; private set; }
        private static float _traceStopAt;
        private static float _nextTraceLine;
        private static readonly Dictionary<int, Vector3> _lastPos = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, float> _lastYaw = new Dictionary<int, float>();

        internal static void ToggleTrace()
        {
            TraceEnabled = !TraceEnabled;
            if (TraceEnabled)
            {
                _traceStopAt = Time.unscaledTime + Plugin.TraceSeconds.Value;
                _lastPos.Clear();
                _lastYaw.Clear();
                Plugin.Log.LogInfo(
                    $"[trace] ON for {Plugin.TraceSeconds.Value}s. Move ONE player at a time and watch which " +
                    "rows react. A row whose value stays 0 while its posD is non-zero is being moved by " +
                    "something other than its own input.");
            }
            else
            {
                Plugin.Log.LogInfo("[trace] OFF");
            }
        }

        /// <summary>
        /// One line per pawn per frame in which anything happened, pairing what the pawn's own
        /// actions read with how far the pawn actually moved. That pairing separates "several
        /// pawns receive one device" from "only one receives input but several move" — the two
        /// produce identical symptoms on screen and opposite fixes.
        /// </summary>
        internal static void Trace()
        {
            if (!TraceEnabled)
                return;
            if (Time.unscaledTime > _traceStopAt)
            {
                TraceEnabled = false;
                Plugin.Log.LogInfo("[trace] window elapsed — OFF");
                return;
            }
            if (Time.unscaledTime < _nextTraceLine)
                return;

            try
            {
                foreach (var pawn in Pawns())
                {
                    int key = pawn.Root.GetInstanceID();
                    Vector3 pos = pawn.Root.transform.position;
                    float yaw = pawn.Root.transform.eulerAngles.y;

                    Vector3 lastPos;
                    float lastYaw = yaw;
                    bool known = _lastPos.TryGetValue(key, out lastPos) & _lastYaw.TryGetValue(key, out lastYaw);
                    _lastPos[key] = pos;
                    _lastYaw[key] = yaw;
                    if (!known)
                        continue;

                    float posDelta = (pos - lastPos).magnitude;
                    float yawDelta = Mathf.DeltaAngle(lastYaw, yaw);

                    var move = ActionOf(pawn.Fpc, "move");
                    Vector2 moveValue = Vector2.zero;
                    string moveSource = "none";
                    if (move != null)
                    {
                        try { moveValue = move.ReadValue<Vector2>(); } catch { }
                        var active = move.activeControl;
                        if (active != null)
                            moveSource = active.device.displayName + "/" + active.name;
                    }

                    object mouseInput = GetField(pawn.Fpc, "mouseInput");

                    // Silence is the default: only log frames where this pawn did or received
                    // something, otherwise the interesting rows drown.
                    bool interesting = moveValue.sqrMagnitude > 0.0001f ||
                                       posDelta > 0.002f ||
                                       Mathf.Abs(yawDelta) > 0.05f;
                    if (!interesting)
                        continue;

                    Plugin.Log.LogInfo(
                        $"[trace] {pawn.Who} move={moveValue.x:F2},{moveValue.y:F2} src={moveSource} " +
                        $"mouseIn={mouseInput} posD={posDelta:F3} yawD={yawDelta:F2}");
                }

                _nextTraceLine = Time.unscaledTime + Plugin.TraceInterval.Value;
            }
            catch (Exception ex)
            {
                TraceEnabled = false;
                Plugin.Log.LogError($"[trace] threw, disabling: {ex}");
            }
        }

        // ------------------------------------------------------------ spawn check

        /// <summary>
        /// Verify at the moment of spawn that the pawn kept the asset the swap installed,
        /// instead of trusting that it did. Runs on the SpawnPlayer postfix, so a failure is
        /// reported before the player can notice the symptom, and before any later code has
        /// muddied the state.
        /// </summary>
        internal static void VerifySpawn(GameObject pawn, PlayerControls expected, string label)
        {
            try
            {
                if (pawn == null)
                {
                    Plugin.Log.LogError($"[spawncheck] {label}: SpawnPlayer produced no SpawnedObject.");
                    return;
                }

                var expectedAsset = AssetOf(expected);
                int ok = 0, bad = 0;

                foreach (var c in pawn.GetComponentsInChildren<Component>(true))
                {
                    if (c == null)
                        continue;
                    if (!(c is FirstPersonController || c is PlayerShoot || c is PlayerPickup || c is FPArms))
                        continue;

                    var got = ControlsOf(c);
                    bool match = got != null && ReferenceEquals(AssetOf(got), expectedAsset);
                    if (match)
                        ok++;
                    else
                        bad++;

                    Plugin.Log.LogInfo(
                        $"[spawncheck] {label} {c.GetType().Name,-22} got={AssetLabel(AssetOf(got))} " +
                        $"role={Role(got)} {(match ? "OK" : "MISMATCH")}");
                }

                if (bad > 0)
                    Plugin.Log.LogError(
                        $"[spawncheck] {label}: {bad} of {ok + bad} input component(s) did NOT capture this " +
                        "player's action asset. That is the root cause of shared control — the swap window " +
                        "does not cover their Awake.");
                else
                    Plugin.Log.LogInfo(
                        $"[spawncheck] {label}: all {ok} input component(s) captured " +
                        $"{AssetLabel(expectedAsset)} — the swap took.");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[spawncheck] threw: {ex}");
            }
        }
    }
}
