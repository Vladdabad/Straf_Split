using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// ============================ TEMPORARY DIAGNOSTIC ============================
    /// Delete this whole file once the "scope reticle does not appear when aiming down
    /// sights" bug is fixed. <see cref="RemovalChecklist"/> lists everything that goes
    /// with it.
    /// ==============================================================================
    ///
    /// The report is that scoped rifles — M2000, HKG-11 and the like — show no reticle
    /// when you aim. That reticle is not drawn by the weapon; it is one sprite swap at the
    /// end of <c>Crosshair.Update</c>, gated behind a six-term conjunction:
    ///
    /// <code>
    ///   scopeImage.sprite =
    ///       (player.playerPickupScript.SyncAccessor_hasObjectInHand
    ///        &amp;&amp; player.gameObject.activeSelf
    ///        &amp;&amp; canScopeAim                       // written by ItemBehaviour
    ///        &amp;&amp; aimCrosshair != null               // written by ItemBehaviour.OnGrab
    ///        &amp;&amp; player.isAiming
    ///        &amp;&amp; (player.playerCamera.fieldOfView &lt; settings.normalFovValue - aimCrosshairLimit
    ///            || instantAimLens))
    ///       ? aimCrosshair : invisibleCrosshair;
    /// </code>
    ///
    /// Six ways to fail, and every one of them is a plain field read — so rather than
    /// guess which, this samples all six per player per frame and names the FIRST one that
    /// is false. That is the whole design: the log should say which term killed it, not
    /// hand over a wall of numbers to interpret.
    ///
    /// It also checks the half the conjunction cannot explain. Getting the right sprite
    /// onto the right Image is necessary but not sufficient — the Image still has to have
    /// area on screen. <see cref="CrosshairSplit.Place"/> re-anchors the ScopeCrosshair into
    /// each player's viewport, and the scene ships that object stretched
    /// (anchorMin 0,0 / anchorMax 1,1 / sizeDelta 0,0), so collapsing its anchors to a point
    /// leaves a 0x0 rect. The ordinary crosshair survives the same treatment only because
    /// <c>Crosshair.Update</c> rewrites its sizeDelta to 400x400 every frame; nothing does
    /// that for the scope image. So the rect is measured and reported next to the sprite
    /// verdict, and a collapsed one is called out in as many words.
    ///
    /// Two independent event sources, same as <see cref="GrenadeDiagnostics"/>, so a mistake
    /// in either still leaves a working tool: Harmony postfixes around the crosshair writes
    /// (<see cref="Patches.ItemBehaviour_Scope_Watch_Patch"/>) and this per-frame poll.
    /// </summary>
    internal static class ScopeDiagnostics
    {
        /// <summary>
        /// What to delete when the bug is fixed:
        ///   1. this file
        ///   2. Patches/ScopeWatchPatches.cs
        ///   3. the two Plugin.cs config binds (WatchScopeReticle, ScopeWatchInterval) and
        ///      the startup log line that mentions them
        ///   4. the two call sites in SplitscreenCoordinator (Sample() in TickBody,
        ///      Report() under the dump key)
        ///   5. the "Scope reticle watch" section in docs/SPLITSCREEN.md
        /// Nothing else references it. Grep for "swatch" to confirm.
        /// </summary>
        internal const string RemovalChecklist = "see the doc comment on ScopeDiagnostics";

        private static bool Enabled
        {
            get { return Plugin.WatchScopeReticle != null && Plugin.WatchScopeReticle.Value; }
        }

        // ------------------------------------------------------------------ reflection

        // Everything the conjunction reads that the game keeps private. Resolved once;
        // AccessTools.FieldRef is a direct field access after construction, which matters
        // because this runs for every player every frame.
        private static readonly AccessTools.FieldRef<Crosshair, float> AimLimitRef =
            AccessTools.FieldRefAccess<Crosshair, float>("aimCrosshairLimit");
        private static readonly AccessTools.FieldRef<Crosshair, Settings> CrossSettingsRef =
            AccessTools.FieldRefAccess<Crosshair, Settings>("settings");
        private static readonly AccessTools.FieldRef<ItemBehaviour, Sprite> ItemAimSpriteRef =
            AccessTools.FieldRefAccess<ItemBehaviour, Sprite>("aimCrosshair");
        private static readonly AccessTools.FieldRef<ItemBehaviour, bool> ItemInstantLensRef =
            AccessTools.FieldRefAccess<ItemBehaviour, bool>("instantAimLens");

        // ------------------------------------------------------------------ state

        /// <summary>Last line printed per player, so a steady state prints once rather than every frame.</summary>
        private static readonly Dictionary<int, string> _lastKey = new Dictionary<int, string>();

        private static readonly Dictionary<int, float> _lastPrint = new Dictionary<int, float>();

        /// <summary>Every distinct verdict seen, for the summary on the dump key.</summary>
        private static readonly Dictionary<string, int> _verdicts = new Dictionary<string, int>();

        private static int _lines;
        private static bool _capWarned;
        private const int MaxLines = 4000;

        // ------------------------------------------------------------------ events

        /// <summary>
        /// One of the two crosshair writes the game makes for a held weapon, reported from a
        /// Harmony postfix. Prints unconditionally (these are rare — a grab is one call, and
        /// the per-frame Update write is reported only when it flips canScopeAim), because
        /// the question they answer is whether the singleton swap in
        /// <see cref="Patches.ItemBehaviour_Crosshair_Patch"/> put the write on the right
        /// crosshair, and that is invisible from the poll.
        /// </summary>
        internal static void NoteWrite(string what, ItemBehaviour item, Crosshair wroteTo)
        {
            if (!Enabled)
                return;

            try
            {
                var pawn = item != null && item.rootObject != null ? item.rootObject : null;
                var player = LocalPlayers.OwningPawn(pawn);
                var mine = player != null ? CrosshairSplit.For(player.Index) : null;

                // The point of the line: is the crosshair that just got written to the one
                // this player actually looks at?
                string routing;
                if (player == null)
                    routing = "holder unknown (dropped, or a remote player)";
                else if (mine == null)
                    routing = $"{player.Label} has NO crosshair rig";
                else if (ReferenceEquals(mine, wroteTo))
                    routing = $"-> {player.Label}'s own crosshair, correct";
                else
                    routing = $"-> '{Name(wroteTo)}' but {player.Label}'s is '{Name(mine)}' <== WRONG CROSSHAIR";

                Log($"[swatch/write] frame {Time.frameCount} {what} '{ItemName(item)}' " +
                    $"aimWeapon={Yn(item != null && item.aimWeapon)} " +
                    $"itemAimSprite={SpriteName(item != null ? ItemAimSpriteRef(item) : null)} " +
                    $"{routing} " +
                    $"(Crosshair.Instance='{Name(Crosshair.Instance)}')");
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        // ------------------------------------------------------------------ the tick

        /// <summary>
        /// One sample per player per frame. Called from the coordinator's Update tick, above
        /// the split check on purpose: the same weapon has to be watchable with one local
        /// player, since that is the only way to tell a splitscreen regression from something
        /// the game does anyway.
        /// </summary>
        internal static void Sample()
        {
            if (!Enabled)
                return;

            try
            {
                if (LocalPlayers.Count > 0)
                {
                    foreach (var player in LocalPlayers.All)
                        SamplePlayer(player.Index, player.Label, player.Controller);
                }
                else
                {
                    // No roster — vanilla single player, the comparison case.
                    SamplePlayer(0, "P1", FirstPersonController.instance);
                }

                WarnOnSharedScopeImages();
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private static void SamplePlayer(int index, string label, FirstPersonController fpc)
        {
            if (fpc == null)
                return;

            var pickup = fpc.playerPickupScript;
            var held = pickup != null ? pickup.sync___get_value_objInHand() : null;
            var item = held != null ? held.GetComponent<ItemBehaviour>() : null;

            // Only scoped weapons are interesting. An item with no aim sprite has no reticle
            // to show and would otherwise fill the log with "not a scope".
            Sprite itemAimSprite = item != null ? ItemAimSpriteRef(item) : null;
            if (item == null || !item.aimWeapon || itemAimSprite == null)
            {
                _lastKey.Remove(index);
                return;
            }

            var cross = CrosshairSplit.For(index);
            if (cross == null)
            {
                Emit(index, "no-rig|" + ItemName(item), "no crosshair rig",
                    $"[swatch] {label} holds '{ItemName(item)}' but has NO Crosshair — nothing can draw a reticle.");
                return;
            }

            var settings = CrossSettingsRef(cross);
            float normalFov = settings != null ? settings.normalFovValue : float.NaN;
            float limit = AimLimitRef(cross);
            float threshold = normalFov - limit;
            float camFov = fpc.playerCamera != null ? fpc.playerCamera.fieldOfView : float.NaN;

            bool hasHand = pickup != null && pickup.hasObjectInHand;
            bool pawnActive = fpc.gameObject.activeSelf;
            bool canScopeAim = cross.canScopeAim;
            bool hasAimSprite = cross.aimCrosshair != null;
            bool aiming = fpc.isAiming;
            bool fovOk = camFov < threshold;
            bool instantLens = cross.instantAimLens;

            // The conjunction, in the game's own order, so the first failure named here is
            // the first one the game hits. `code` is the same answer without the live
            // numbers in it — the tally on the dump key groups by that, or term 6 would
            // produce a fresh row for every FOV the zoom tween passes through.
            string why = null, code = null;
            if (!hasHand) { code = "hasObjectInHand"; why = "hasObjectInHand is false (the SyncVar has not reached this client)"; }
            else if (!pawnActive) { code = "pawn inactive"; why = "the pawn GameObject is inactive"; }
            else if (!canScopeAim) { code = "canScopeAim"; why = "canScopeAim is FALSE on this player's crosshair — ItemBehaviour wrote it somewhere else, or its Update is not running"; }
            else if (!hasAimSprite) { code = "aimCrosshair null"; why = "the crosshair's aimCrosshair sprite is NULL — OnGrab did not reach this crosshair"; }
            else if (!aiming) { code = "not aiming"; why = "not aiming"; }
            else if (!fovOk && !instantLens) { code = "fov not zoomed"; why = $"camera FOV {camFov:F1} has not dropped below {threshold:F1} (normalFov {normalFov:F1} - limit {limit:F1})"; }

            bool spriteOk = why == null;

            // The other half: the right sprite on an Image with no area is still nothing on
            // screen, and that failure mode is invisible in the conjunction above.
            var scope = cross.scopeImage;
            var srt = scope != null ? scope.rectTransform : null;
            float w = 0f, h = 0f;
            if (srt != null)
            {
                var r = srt.rect;
                var s = srt.lossyScale;
                w = Mathf.Abs(r.width * s.x);
                h = Mathf.Abs(r.height * s.y);
            }

            string geometry = null, geoCode = null;
            if (scope == null)
            { geoCode = "no scopeImage"; geometry = "the crosshair has NO scopeImage bound"; }
            else if (!scope.gameObject.activeInHierarchy)
            { geoCode = "scope object inactive"; geometry = "the scope image GameObject is inactive"; }
            else if (!scope.enabled)
            { geoCode = "scope Image disabled"; geometry = "the scope Image component is disabled"; }
            else if (w < 1f || h < 1f)
            {
                geoCode = "rect collapsed to 0x0";
                geometry = $"the scope image rect is {w:F0}x{h:F0} px — COLLAPSED TO NOTHING, so no sprite can draw " +
                           $"(anchors {Vec(srt.anchorMin)}..{Vec(srt.anchorMax)}, sizeDelta {Vec(srt.sizeDelta)}, scale {srt.localScale.x:F2})";
            }
            else if (scope.color.a < 0.01f)
            { geoCode = "alpha 0"; geometry = "the scope image alpha is 0"; }

            // Crosshair.Update ends with an unconditional blank-out when a camera is tagged
            // MainCamera. Nothing in the mod should leave one tagged, but if one is, it
            // outranks everything above and the reticle is gone whatever the terms say.
            bool mainCam = Camera.main != null;

            string verdict, tally;
            if (mainCam)
            {
                verdict = "HIDDEN: a camera is tagged MainCamera, and Crosshair.Update blanks both sprites unconditionally when Camera.main exists";
                tally = "HIDDEN: Camera.main exists";
            }
            else if (!spriteOk)
            {
                verdict = "HIDDEN: " + why;
                tally = "HIDDEN: " + code;
            }
            else if (geometry != null)
            {
                verdict = "DRAWN BUT INVISIBLE: " + geometry;
                tally = "DRAWN BUT INVISIBLE: " + geoCode;
            }
            else
            {
                verdict = "SHOWN";
                tally = "SHOWN";
            }

            // What is diffed. Discrete only — the raw FOV changes every frame while the zoom
            // tweens, and diffing it would print a line a frame.
            string key = string.Join("|", new[]
            {
                ItemName(item), tally,
                Yn(hasHand), Yn(pawnActive), Yn(canScopeAim), Yn(hasAimSprite),
                Yn(aiming), Yn(fovOk), Yn(instantLens),
                Name(cross), Name(scope), SpriteName(scope != null ? scope.sprite : null),
                Mathf.RoundToInt(w) + "x" + Mathf.RoundToInt(h),
            });

            string detail =
                $"[swatch] {label} '{ItemName(item)}': {verdict}\n" +
                $"    terms: hand={Yn(hasHand)} pawnActive={Yn(pawnActive)} canScopeAim={Yn(canScopeAim)} " +
                $"aimSprite={SpriteName(cross.aimCrosshair)} aiming={Yn(aiming)} " +
                $"fov={camFov:F1}<{threshold:F1}?{Yn(fovOk)} instantLens={Yn(instantLens)}\n" +
                $"    fov:   zoomFOV={fpc.zoomFOV:F1} aimFOV={item.aimFOV:F1} defaultFOV={fpc.defaultFOV:F1} " +
                $"normalFov={normalFov:F1} limit={limit:F1}{ZoomFovNote(fpc, item)}\n" +
                $"    rig:   crosshair='{Name(cross)}' isInstance={Yn(ReferenceEquals(cross, Crosshair.Instance))} " +
                $"player={(cross.player == fpc ? "correct" : "WRONG PAWN")} " +
                $"scopeImage='{Name(scope)}' sprite={SpriteName(scope != null ? scope.sprite : null)} " +
                $"alpha={(scope != null ? scope.color.a : 0f):F2} enabled={Yn(scope != null && scope.enabled)} " +
                $"active={Yn(scope != null && scope.gameObject.activeInHierarchy)} rect={w:F0}x{h:F0}px\n" +
                $"    item:  rootLayer={(item.rootObject != null ? item.rootObject.layer.ToString() : "-")} " +
                $"itemInstantLens={Yn(ItemInstantLensRef(item))} isScopeAiming={Yn(fpc.isScopeAiming)} " +
                $"reloading={Yn(IsReloading(item))} Camera.main={(mainCam ? Camera.main.name : "none")}";

            Emit(index, key, tally, detail);
        }

        /// <summary>
        /// PlayerPickup computes zoomFOV as <c>aimFOV + (canScopeAim ? 0 : defaultFOV - 68)</c>,
        /// reading <c>Crosshair.Instance.canScopeAim</c> — the singleton, unswapped, so on a
        /// grab it answers with player 1's crosshair whoever is picking the weapon up. When the
        /// two disagree the weapon zooms to the wrong FOV, which is exactly the term the
        /// reticle is gated on. Flagged rather than merely printed, because the two numbers
        /// look innocent side by side.
        /// </summary>
        private static string ZoomFovNote(FirstPersonController fpc, ItemBehaviour item)
        {
            float widened = fpc.defaultFOV - 68f;
            if (Mathf.Abs(fpc.zoomFOV - item.aimFOV) < 0.05f)
                return "";
            if (Mathf.Abs(fpc.zoomFOV - (item.aimFOV + widened)) < 0.05f)
                return $"  <== zoomFOV was widened by {widened:F1} (PlayerPickup read Crosshair.Instance.canScopeAim=false at grab time)";
            return "  <== zoomFOV does not match aimFOV";
        }

        private static bool IsReloading(ItemBehaviour item)
        {
            var weapon = item.GetComponent<Weapon>();
            return weapon != null && weapon.isReloading;
        }

        /// <summary>
        /// Two players pointing at one ScopeCrosshair cannot both show a reticle — the later
        /// writer wins every frame. That means the sibling rewire in
        /// <see cref="CrosshairSplit.EnsureRig"/> did not take, which is a different bug from
        /// anything the per-player terms can show.
        /// </summary>
        private static void WarnOnSharedScopeImages()
        {
            if (!LocalPlayers.SplitActive)
                return;

            for (int i = 0; i < LocalPlayers.Count; i++)
            {
                var a = CrosshairSplit.For(i);
                var ai = a != null ? a.scopeImage : null;
                if (ai == null)
                    continue;
                for (int j = i + 1; j < LocalPlayers.Count; j++)
                {
                    var b = CrosshairSplit.For(j);
                    var bi = b != null ? b.scopeImage : null;
                    if (bi != null && ReferenceEquals(ai, bi))
                        Emit(1000 + i * 10 + j, "shared|" + Name(ai), "shared scope image",
                            $"[swatch] P{i + 1} and P{j + 1} share ONE scopeImage ('{Name(ai)}') — " +
                            "the clone's sibling reference was not rewired, so only one of them can ever show a reticle.");
                }
            }
        }

        // ------------------------------------------------------------------ output

        /// <summary>
        /// Print when the discrete state changed, or once every ScopeWatchInterval seconds
        /// while it has not. The heartbeat exists because the interesting case is a state
        /// that is stuck: without it, "reticle hidden and staying hidden" prints once and
        /// then looks identical to "nothing is happening".
        /// </summary>
        private static void Emit(int index, string key, string tally, string detail)
        {
            string was;
            bool changed = !_lastKey.TryGetValue(index, out was) || was != key;

            float last;
            bool due = !_lastPrint.TryGetValue(index, out last) ||
                       Time.unscaledTime - last >= Mathf.Max(0.25f, Plugin.ScopeWatchInterval.Value);

            if (!changed && !due)
                return;

            _lastKey[index] = key;
            _lastPrint[index] = Time.unscaledTime;

            int count;
            _verdicts.TryGetValue(tally, out count);
            _verdicts[tally] = count + 1;

            Log(detail);
        }

        private static void Log(string message)
        {
            if (_lines >= MaxLines)
            {
                if (!_capWarned)
                {
                    _capWarned = true;
                    Plugin.Log.LogWarning("[swatch] line cap reached — the scope watch is now silent. " +
                                          "Set Debug/WatchScopeReticle=false and back on to reset it.");
                }
                return;
            }
            _lines++;
            Plugin.Log.LogWarning(message);
        }

        /// <summary>One-shot detail for every player, on the dump key.</summary>
        internal static void Report()
        {
            if (!Enabled)
            {
                Plugin.Log.LogInfo("[swatch] scope reticle watch is off (Debug/WatchScopeReticle).");
                return;
            }

            Plugin.Log.LogInfo("=============== SCOPE RETICLE WATCH ===============");

            // Force a full print of the current state rather than waiting for the heartbeat.
            _lastKey.Clear();
            _lastPrint.Clear();
            Sample();

            if (_verdicts.Count == 0)
            {
                Plugin.Log.LogInfo("Nothing sampled yet — hold a scoped weapon (M2000, HKG-11) and aim.");
            }
            else
            {
                Plugin.Log.LogInfo("Verdicts seen so far:");
                foreach (var pair in _verdicts)
                    Plugin.Log.LogInfo($"  {pair.Value,5}x  {pair.Key}");
            }

            Plugin.Log.LogInfo(
                "Temporary: set Debug/WatchScopeReticle=false to silence, and delete " +
                "ScopeDiagnostics.cs + Patches/ScopeWatchPatches.cs once the bug is fixed.");
            Plugin.Log.LogInfo("============= END SCOPE RETICLE WATCH =============");
        }

        // ------------------------------------------------------------------ formatting

        private static string Name(UnityEngine.Object o)
        {
            return o != null ? o.name : "null";
        }

        private static string ItemName(ItemBehaviour item)
        {
            if (item == null)
                return "nothing";
            return !string.IsNullOrEmpty(item.weaponName) ? item.weaponName : item.gameObject.name;
        }

        private static string SpriteName(Sprite s)
        {
            return s != null ? s.name : "NULL";
        }

        private static string Vec(Vector2 v)
        {
            return $"({v.x:F2},{v.y:F2})";
        }

        private static string Yn(bool b)
        {
            return b ? "Y" : "N";
        }

        private static void Fail(Exception ex)
        {
            Plugin.Log.LogError($"[swatch] threw, disabling the scope watch: {ex}");
            if (Plugin.WatchScopeReticle != null)
                Plugin.WatchScopeReticle.Value = false;
        }
    }
}
