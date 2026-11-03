using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Forensics for "the other player's body is missing".
    ///
    /// The eight third-person meshes are indistinguishable in the prefab — same parent
    /// (Graphics/PF_Aboubi_04/SK_Aboubi_00), same layer 3, all renderers enabled — and
    /// <see cref="PlayerVisibility"/> treats all eight identically. So whatever hides a subset of
    /// them does it at runtime, and static reading cannot find it. There are only four ways a
    /// mesh that exists can fail to appear, and this module reports all four side by side so
    /// the answer is read off rather than guessed:
    ///
    ///   1. the GameObject is inactive          -> activeSelf / the first inactive ancestor
    ///   2. the Renderer is disabled            -> renderer.enabled
    ///   3. its layer is culled by that camera  -> mask test, per owned camera, per mesh
    ///   4. it draws but is invisible anyway    -> material / shader, and renderer.isVisible
    ///
    /// Note that with several local players, final visibility is decided per camera in
    /// <see cref="PlayerVisibility"/> by switching Renderer.enabled off during culling and
    /// back on immediately afterwards — so a snapshot taken from Update or LateUpdate always
    /// sees the game's own state, never ours. The mask column below therefore reports whether
    /// the LAYER passes the camera, not the final answer; the per-camera rule ("you never see
    /// your own body, your arms are yours alone") supplies the rest.
    ///
    /// <see cref="Report"/> is the snapshot. <see cref="Watch"/> is the more useful half: it
    /// samples the same four properties every frame and logs only transitions, so the moment
    /// a mesh goes dark is timestamped with what changed — which is the one fact that
    /// identifies the culprit, and the one no snapshot can give you.
    /// </summary>
    internal static class RenderDiagnostics
    {
        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject[]> DisableForMeRef =
            AccessTools.FieldRefAccess<PlayerSetup, GameObject[]>("gameObjectsToDisableForMe");
        private static readonly AccessTools.FieldRef<PlayerSetup, GameObject> GraphicsRef =
            AccessTools.FieldRefAccess<PlayerSetup, GameObject>("graphics");
        private static readonly AccessTools.FieldRef<PlayerSetup, Camera[]> CamerasRef =
            AccessTools.FieldRefAccess<PlayerSetup, Camera[]>("cameras");

        // ------------------------------------------------------------------ helpers

        private static string Who(PlayerSetup setup)
        {
            var player = LocalPlayers.OwningPawn(setup.gameObject);
            return player != null ? player.Label : "REMOTE";
        }

        /// <summary>
        /// The nearest ancestor that is switched off, or null. A mesh with activeSelf=true and
        /// activeInHierarchy=false is hidden by something above it, and naming that object is
        /// the difference between "the torso is off" and "PF_Aboubi_04 is off, taking seven
        /// meshes with it".
        /// </summary>
        private static string FirstInactiveAncestor(GameObject go)
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                    return t.gameObject.name + (t.gameObject == go ? " (itself)" : "");
            }
            return null;
        }

        private static string MaterialOf(Renderer r)
        {
            var m = r.sharedMaterial;
            if (m == null)
                return "NULL MATERIAL (renders nothing)";
            var shader = m.shader;
            return $"{m.name}/{(shader == null ? "NULL SHADER" : shader.name)}";
        }

        // ------------------------------------------------------------------ snapshot

        internal static void Report()
        {
            try
            {
                Plugin.Log.LogInfo("=============== RENDER DIAGNOSTIC ===============");

                var setups = new List<PlayerSetup>();
                foreach (var s in UnityEngine.Object.FindObjectsOfType<PlayerSetup>())
                    setups.Add(s);

                // The cameras that matter: every owned pawn's cam[0]. A mesh is visible to a
                // player only if that player's cam[0] passes its layer.
                var viewers = new List<KeyValuePair<string, Camera>>();
                foreach (var setup in setups)
                {
                    string who = Who(setup);
                    if (who == "REMOTE")
                        continue;
                    var cams = CamerasRef(setup);
                    if (cams != null && cams.Length > 0 && cams[0] != null)
                        viewers.Add(new KeyValuePair<string, Camera>(who, cams[0]));
                }

                foreach (var kv in viewers)
                {
                    var cam = kv.Value;
                    Plugin.Log.LogInfo(
                        $"[view] {kv.Key} cam0 '{cam.name}' enabled={cam.enabled} rect={cam.rect} " +
                        $"mask=0x{cam.cullingMask:X8} layers={DescribeMask(cam.cullingMask)}");
                }

                foreach (var setup in setups)
                {
                    string who = Who(setup);
                    Plugin.Log.LogInfo($"--- {who} pawn '{setup.gameObject.name}' rootLayer={setup.gameObject.layer}");

                    // The eight the game hides from its owner — the mod's whole job is keeping
                    // these on and layered per player.
                    var hideList = DisableForMeRef(setup);
                    if (hideList != null)
                    {
                        foreach (var go in hideList)
                        {
                            if (go != null)
                                ReportMesh(who, "bodyMesh", go, viewers);
                        }
                    }

                    // The first-person arms: "arms are visible" is ambiguous between these and
                    // the third-person SK_Aboubi00_Arm_* above, so report both explicitly.
                    //
                    // The kind tells you which of PlayerVisibility's two sets each renderer
                    // falls into, because a renderer in NEITHER is never toggled by anyone and
                    // therefore draws in every player's slice at once. That is the signature of
                    // "both players can see it", and it is invisible in a plain renderer list.
                    var graphics = GraphicsRef(setup);
                    var cams = CamerasRef(setup);
                    Transform camRoot = cams != null && cams.Length > 0 && cams[0] != null
                        ? cams[0].transform
                        : null;

                    foreach (var r in setup.GetComponentsInChildren<Renderer>(true))
                    {
                        bool underGraphics = graphics != null && r.transform.IsChildOf(graphics.transform);
                        if (underGraphics)
                            continue; // covered by the hide list pass above

                        bool underCam = camRoot != null && r.transform.IsChildOf(camRoot);
                        ReportMesh(who, underCam ? "fp" : "UNTOGGLED", r.gameObject, viewers);
                    }

                    // Lights are not Renderers, so PlayerVisibility cannot switch them per
                    // camera at all: one on a pawn lights the scene for everybody, and a
                    // spotlight or its cone mesh reads as a beam coming out of that player.
                    foreach (var light in setup.GetComponentsInChildren<Light>(true))
                    {
                        Plugin.Log.LogInfo(
                            $"    [light] {Path(light.transform, setup.transform),-40} type={light.type} " +
                            $"enabled={light.enabled} activeInHierarchy={light.gameObject.activeInHierarchy} " +
                            $"range={light.range} intensity={light.intensity} " +
                            $"cullingMask=0x{light.cullingMask:X8}");
                    }
                }

                // Held weapons are NOT children of the pawn — PlayerPickup positions them
                // against the camera instead — so nothing above sees them, and PlayerVisibility
                // never sorts them into anybody's set. Whatever a weapon draws (a beam, a laser,
                // a muzzle glow) is therefore drawn for EVERY player at once. That is the shape
                // of "both players can see it", so weapons get their own pass, in world space.
                foreach (var weapon in UnityEngine.Object.FindObjectsOfType<Weapon>())
                {
                    var holder = HolderOf(weapon.gameObject);
                    var t = weapon.transform;
                    Plugin.Log.LogInfo(
                        $"  [weapon] '{weapon.gameObject.name}' ({weapon.GetType().Name}) " +
                        $"held by {(holder != null ? holder.Label : "nobody")} " +
                        $"parent={(t.parent != null ? t.parent.name : "SCENE ROOT")} " +
                        $"layer={weapon.gameObject.layer} pos={t.position}");

                    foreach (var r in weapon.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!r.enabled || !r.gameObject.activeInHierarchy)
                            continue;
                        ReportMesh(holder != null ? holder.Label : "?", "weaponMesh", r.gameObject, viewers);
                    }

                    // A Light is not a Renderer, so no amount of per-camera renderer toggling
                    // can hide one: it lights every slice of the screen by construction.
                    foreach (var light in weapon.GetComponentsInChildren<Light>(true))
                    {
                        Plugin.Log.LogInfo(
                            $"    [light] {light.gameObject.name,-28} type={light.type} " +
                            $"enabled={light.enabled} active={light.gameObject.activeInHierarchy} " +
                            $"range={light.range} intensity={light.intensity} " +
                            $"cullingMask=0x{light.cullingMask:X8}");
                    }
                }

                // Anything drawing near a player's head that is NOT part of a pawn.
                //
                // PlayerVisibility can only toggle what it can classify, and it classifies what
                // hangs under a pawn. A beam that appears to come out of somebody's head while
                // both players can see it is, by elimination, something else standing in that
                // spot — a projectile, a weapon effect, a stray VFX left behind. This pass is
                // how such an object gets named instead of guessed at.
                //
                // Static map geometry is skipped: the floor and walls are always within arm's
                // reach and never the answer.
                int reported = 0;
                foreach (var player in LocalPlayers.All)
                {
                    var cams = player.Cameras;
                    if (cams == null || cams.Length == 0 || cams[0] == null)
                        continue;
                    Vector3 head = cams[0].transform.position;

                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        if (reported >= 40)
                            break;
                        if (!r.enabled || r.gameObject.isStatic)
                            continue;
                        if (r.GetComponentInParent<PlayerSetup>() != null)
                            continue; // covered by the pawn passes above
                        float d = Vector3.Distance(r.transform.position, head);
                        if (d > 3f)
                            continue;

                        reported++;
                        Plugin.Log.LogInfo(
                            $"  [near] {player.Label} head +{d:F2}m: '{r.gameObject.name}' " +
                            $"layer={r.gameObject.layer} type={r.GetType().Name} " +
                            $"root='{r.transform.root.name}' mat={MaterialOf(r)}");
                    }

                    foreach (var light in UnityEngine.Object.FindObjectsOfType<Light>())
                    {
                        if (!light.enabled || light.GetComponentInParent<PlayerSetup>() != null)
                            continue;
                        float d = Vector3.Distance(light.transform.position, head);
                        if (d > 3f)
                            continue;

                        Plugin.Log.LogInfo(
                            $"  [near] {player.Label} head +{d:F2}m: LIGHT '{light.gameObject.name}' " +
                            $"type={light.type} range={light.range} intensity={light.intensity} " +
                            $"root='{light.transform.root.name}' cullingMask=0x{light.cullingMask:X8}");
                    }
                }

                // Every line- and trail-shaped renderer in the scene, at any distance.
                //
                // A distance test cannot find these: a LineRenderer drawn from an explosion to a
                // player has its transform back AT the explosion, so a beam ending on somebody's
                // head sits tens of metres away by the only measure the [near] pass has. They are
                // also the only renderer types that can look like a beam at all, and there are
                // never many, so listing them outright is both cheap and complete.
                foreach (var line in UnityEngine.Object.FindObjectsOfType<LineRenderer>())
                {
                    var sb = new StringBuilder();
                    int count = line.positionCount;
                    for (int i = 0; i < count && i < 4; i++)
                    {
                        if (sb.Length > 0)
                            sb.Append(" -> ");
                        sb.Append(line.GetPosition(i).ToString("F1"));
                    }

                    Plugin.Log.LogInfo(
                        $"  [beam] LineRenderer '{line.gameObject.name}' root='{line.transform.root.name}' " +
                        $"layer={line.gameObject.layer} enabled={line.enabled} " +
                        $"active={line.gameObject.activeInHierarchy} points={count} [{sb}] " +
                        $"mat={MaterialOf(line)}");
                }

                foreach (var trail in UnityEngine.Object.FindObjectsOfType<TrailRenderer>())
                {
                    Plugin.Log.LogInfo(
                        $"  [beam] TrailRenderer '{trail.gameObject.name}' root='{trail.transform.root.name}' " +
                        $"layer={trail.gameObject.layer} enabled={trail.enabled} " +
                        $"active={trail.gameObject.activeInHierarchy} pos={trail.transform.position} " +
                        $"mat={MaterialOf(trail)}");
                }

                Plugin.Log.LogInfo("============= END RENDER DIAGNOSTIC =============");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Render diagnostic threw: {ex}");
            }
        }

        private static string Path(Transform t, Transform stopAt)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && p != stopAt; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        /// <summary>Which local player is holding this object, by asking each one's PlayerPickup.</summary>
        private static LocalPlayer HolderOf(GameObject obj)
        {
            foreach (var player in LocalPlayers.All)
            {
                var pawn = player.Pawn;
                var pickup = pawn != null ? pawn.GetComponent<PlayerPickup>() : null;
                if (pickup == null)
                    continue;
                // The SyncVar accessor properties are not callable from C#; the generated
                // getter methods are what FishNet leaves usable.
                if (ReferenceEquals(pickup.sync___get_value_objInHand(), obj) ||
                    ReferenceEquals(pickup.sync___get_value_objInLeftHand(), obj))
                    return player;
            }
            return null;
        }

        private static void ReportMesh(string who, string kind, GameObject go, List<KeyValuePair<string, Camera>> viewers)
        {
            var r = go.GetComponent<Renderer>();
            string inactiveAncestor = FirstInactiveAncestor(go);

            var sb = new StringBuilder();
            foreach (var kv in viewers)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                bool passesMask = (kv.Value.cullingMask & (1 << go.layer)) != 0;
                bool camOn = kv.Value.enabled;
                sb.Append($"{kv.Key}:{(passesMask && camOn ? "layerOK" : "CULLED")}");
            }

            Plugin.Log.LogInfo(
                $"    [{kind}] {go.name,-28} layer={go.layer,-2} activeSelf={go.activeSelf,-5} " +
                $"inHierarchy={go.activeInHierarchy,-5} " +
                $"{(inactiveAncestor != null ? "hiddenBy=" + inactiveAncestor + " " : "")}" +
                $"renderer={(r == null ? "none" : "enabled=" + r.enabled + ",isVisible=" + r.isVisible)} " +
                $"mat={(r == null ? "-" : MaterialOf(r))} -> {sb}");
        }

        private static string DescribeMask(int mask)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append(',');
                sb.Append(i);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ watchdog

        private struct MeshState
        {
            public bool ActiveInHierarchy;
            public bool RendererEnabled;
            public int Layer;
            public int MaterialId;
            public string Name;
            public string Who;
        }

        private static readonly Dictionary<string, MeshState> _states = new Dictionary<string, MeshState>();
        private static int _transitionsLogged;
        private const int MaxTransitions = 400;

        /// <summary>
        /// Log every change to a body mesh's visibility inputs, once per change, per phase.
        ///
        /// A snapshot taken after the fact cannot say who turned a mesh off, and the game
        /// disables these in exactly one place at spawn — so a mesh going dark later is
        /// someone else's doing. Sampling every frame and printing only transitions turns that
        /// into a timestamped record: which mesh, which property, and what else happened on
        /// the same frame in the log above it.
        ///
        /// <paramref name="phase"/> exists because a single sample per frame lies. The torso
        /// flicker was a mesh being switched off in Update and on again in LateUpdate: at the
        /// one instant this was sampled it read "off" every single frame, so the watchdog
        /// reported a stable state while the screen was visibly flashing. Sampling in both
        /// phases and keeping their histories apart makes an oscillation legible — the same
        /// mesh reports a different steady value under "update" than under "late".
        /// </summary>
        internal static void Watch(string phase)
        {
            if (!Plugin.WatchBodyVisibility.Value || !LocalPlayers.SplitActive)
                return;
            if (_transitionsLogged >= MaxTransitions)
                return;

            try
            {
                // Straight off the roster: this runs twice a frame, and FindObjectsOfType at
                // that rate is not something to leave in a build people play.
                foreach (var player in LocalPlayers.All)
                {
                    var setup = player.Setup;
                    if (setup == null)
                        continue;
                    string who = player.Label;

                    var hideList = DisableForMeRef(setup);
                    if (hideList == null)
                        continue;

                    foreach (var go in hideList)
                    {
                        if (go == null)
                            continue;
                        var r = go.GetComponent<Renderer>();
                        var mat = r != null ? r.sharedMaterial : null;

                        var now = new MeshState
                        {
                            ActiveInHierarchy = go.activeInHierarchy,
                            RendererEnabled = r != null && r.enabled,
                            Layer = go.layer,
                            MaterialId = mat != null ? mat.GetInstanceID() : 0,
                            Name = go.name,
                            Who = who,
                        };

                        string key = phase + ":" + go.GetInstanceID();
                        MeshState was;
                        if (!_states.TryGetValue(key, out was))
                        {
                            // First sighting. Log it: a mesh that was already switched off
                            // before the watchdog ever saw it never produces a transition, and
                            // that is exactly the case where the game disabled it at spawn and
                            // the mod's re-enable did not stick.
                            _states[key] = now;
                            Plugin.Log.LogInfo(
                                $"[render/{phase}] frame {Time.frameCount} {now.Who} {now.Name}: first seen " +
                                $"active={now.ActiveInHierarchy} renderer={now.RendererEnabled} layer={now.Layer}" +
                                $"{(now.ActiveInHierarchy ? "" : " hiddenBy=" + FirstInactiveAncestor(go))}");
                            continue;
                        }
                        if (was.ActiveInHierarchy == now.ActiveInHierarchy &&
                            was.RendererEnabled == now.RendererEnabled &&
                            was.Layer == now.Layer &&
                            was.MaterialId == now.MaterialId &&
                            was.Who == now.Who)
                        {
                            continue;
                        }
                        _states[key] = now;

                        var changes = new StringBuilder();
                        if (was.ActiveInHierarchy != now.ActiveInHierarchy)
                            changes.Append($"active {was.ActiveInHierarchy}->{now.ActiveInHierarchy} ");
                        if (was.RendererEnabled != now.RendererEnabled)
                            changes.Append($"renderer {was.RendererEnabled}->{now.RendererEnabled} ");
                        if (was.Layer != now.Layer)
                            changes.Append($"layer {was.Layer}->{now.Layer} ");
                        if (was.MaterialId != now.MaterialId)
                            changes.Append($"material {was.MaterialId}->{now.MaterialId} ");
                        if (was.Who != now.Who)
                            changes.Append($"owner {was.Who}->{now.Who} ");

                        string hiddenBy = now.ActiveInHierarchy ? "" : " hiddenBy=" + FirstInactiveAncestor(go);
                        _transitionsLogged++;
                        Plugin.Log.LogWarning(
                            $"[render/{phase}] frame {Time.frameCount} {now.Who} {now.Name}: {changes}{hiddenBy}");

                        if (_transitionsLogged == MaxTransitions)
                            Plugin.Log.LogWarning("[render] transition cap reached — further changes suppressed.");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[render] watchdog threw, disabling: {ex}");
                Plugin.WatchBodyVisibility.Value = false;
            }
        }

        internal static void Reset()
        {
            _states.Clear();
            _transitionsLogged = 0;
        }
    }
}
