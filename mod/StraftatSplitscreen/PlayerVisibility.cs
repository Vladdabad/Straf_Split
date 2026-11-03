using System;
using System.Collections.Generic;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Who can see whom, and which slice of the screen each player gets.
    ///
    /// Vanilla hides your own body with SetActive(false) on the third-person meshes, which
    /// works for exactly one local player: with several, every pawn runs the owner branch, so
    /// all of them switch themselves off and nobody can see anybody.
    ///
    /// The previous approach put each player's body on a private layer and culled it from
    /// their own camera. That works, but it costs TWO layers per player (body + first-person
    /// arms) and STRAFTAT has three free layers in total — a hard ceiling of two players, with
    /// one layer spare. It also drags the hitbox skeleton along with it, since colliderParent
    /// lives under Graphics, which is why the old code had to clone physics collision rows
    /// onto the borrowed layers to keep shooting honest.
    ///
    /// So visibility is decided per camera instead, in Camera.onPreCull:
    ///
    ///     for a camera belonging to player O, for every local player p:
    ///         p == O  ->  switch p's body renderers off    // never see your own body
    ///         p != O  ->  switch p's first-person arms off // your arms are yours alone
    ///
    /// and exactly those renderers are switched back on in onPostRender, so the rest of the
    /// game — and the diagnostics — always observe a sane state. That is the whole rule, for
    /// any number of players. Camera culling masks are left exactly as the game set them, so
    /// quality settings keep working, and every pawn keeps its vanilla layers, so hitboxes
    /// never move and the physics-row cloning is gone.
    ///
    /// Note the asymmetry, which cost a bug (v0.27.0): this only ever switches renderers
    /// OFF, and only ever switches back on the ones it switched off itself. The obvious
    /// formulation — `enabled = (p != O)` for bodies, `enabled = (p == O)` for arms — also
    /// force-enables renderers the GAME deliberately keeps disabled, and a held item is
    /// parented under the camera rig, so every one of its renderers joins the first-person
    /// set. The grenade prefabs carry a disabled LineRenderer on their root (the throw arc,
    /// on ParticleFirework.mat); picking one up therefore lit a bright line across the
    /// screen a quarter-second later — the refresh cadence, not the pickup. Suppress-and-
    /// restore makes "the game had this off" survive by construction, for every renderer
    /// that is or ever will be down there.
    /// </summary>
    internal static class PlayerVisibility
    {
        private sealed class RendererSet
        {
            internal LocalPlayer Player;
            internal Renderer[] Body;   // under Graphics: the third-person model
            internal Renderer[] Fp;     // under the camera rig: first-person arms and held weapon
        }

        private static readonly List<RendererSet> _sets = new List<RendererSet>();

        /// <summary>Camera instance id -> the player it belongs to (null for scene/kill cams).</summary>
        private static readonly Dictionary<int, LocalPlayer> _cameraOwners = new Dictionary<int, LocalPlayer>();

        private static bool _hooked;
        private static float _nextRefresh;
        private static bool _dirty;

        // ------------------------------------------------------------------ hook

        internal static void Install()
        {
            if (_hooked)
                return;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
            _hooked = true;
            Plugin.Log.LogInfo("Per-camera visibility installed.");
        }

        internal static void Uninstall()
        {
            if (!_hooked)
                return;
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            _hooked = false;
            RestoreAll();
            _sets.Clear();
            _cameraOwners.Clear();
            Plugin.Log.LogInfo("Per-camera visibility removed.");
        }

        private static void OnPreCull(Camera cam)
        {
            if (!LocalPlayers.SplitActive)
                return;

            try
            {
                // Cached rather than GetComponentInParent: this is the hottest path in the mod,
                // running once per camera per frame, and the cameras deep in a pawn hierarchy
                // are several levels down from their marker.
                LocalPlayer owner;
                if (!_cameraOwners.TryGetValue(cam.GetInstanceID(), out owner))
                {
                    owner = LocalPlayers.Owning(cam);
                    _cameraOwners[cam.GetInstanceID()] = owner;
                }

                // Always start from the world as the game left it, whatever the previous
                // camera hid. A scene camera, kill cam or reflection probe then simply sees
                // that, since it belongs to no local player and hides nothing.
                RestoreAll();
                if (owner == null)
                    return;

                for (int i = 0; i < _sets.Count; i++)
                {
                    var set = _sets[i];
                    if (ReferenceEquals(set.Player, owner))
                        Suppress(set.Body);
                    else
                        Suppress(set.Fp);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Visibility pre-cull threw, restoring and unhooking: {ex}");
                Uninstall();
            }
        }

        private static void OnPostRender(Camera cam)
        {
            if (!LocalPlayers.SplitActive)
                return;
            RestoreAll();
        }

        /// <summary>
        /// Switch these renderers off for the camera about to draw, remembering exactly which
        /// ones were actually switched — a renderer the game already had disabled is left
        /// alone, and therefore never handed back enabled by <see cref="RestoreAll"/>.
        /// </summary>
        private static void Suppress(Renderer[] renderers)
        {
            if (renderers == null)
                return;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null || !r.enabled)
                    continue;
                r.enabled = false;
                _suppressed.Add(r);
            }
        }

        /// <summary>
        /// Undo the suppression, and nothing else. Called after every camera and as a safety
        /// net each frame: if an exception ever escaped between pre-cull and post-render, a
        /// player would otherwise stay invisible until the next respawn.
        /// </summary>
        internal static void RestoreAll()
        {
            for (int i = 0; i < _suppressed.Count; i++)
            {
                var r = _suppressed[i];
                if (r != null)
                    r.enabled = true;
            }
            _suppressed.Clear();
        }

        /// <summary>What this camera switched off, to be switched back on after it draws.
        /// Reused rather than reallocated: this runs once per camera per frame.</summary>
        private static readonly List<Renderer> _suppressed = new List<Renderer>();

        // ------------------------------------------------------------------ caches

        internal static void Invalidate()
        {
            _dirty = true;
        }

        /// <summary>
        /// Rebuild the per-player renderer lists. Held weapons are instantiated under the arms
        /// at pickup time, so this cannot be done once — but it also must not run per frame,
        /// so it is refreshed on a short timer and immediately whenever a pawn changes.
        /// </summary>
        internal static void Refresh()
        {
            _dirty = false;
            _nextRefresh = Time.unscaledTime + 0.25f;
            _sets.Clear();

            // Pawns are replaced every round, so cached camera ownership goes stale with them.
            _cameraOwners.Clear();

            foreach (var player in LocalPlayers.All)
            {
                var setup = player.Setup;
                if (setup == null)
                    continue;

                var graphics = player.Graphics;
                var cameras = player.Cameras;
                Transform camRoot = cameras != null && cameras.Length > 0 && cameras[0] != null
                    ? cameras[0].transform
                    : null;

                var body = new List<Renderer>();
                var fp = new List<Renderer>();

                foreach (var r in setup.GetComponentsInChildren<Renderer>(true))
                {
                    if (graphics != null && r.transform.IsChildOf(graphics.transform))
                        body.Add(r);
                    else if (camRoot != null && r.transform.IsChildOf(camRoot))
                        fp.Add(r);
                }

                _sets.Add(new RendererSet
                {
                    Player = player,
                    Body = body.ToArray(),
                    Fp = fp.ToArray(),
                });
            }

            // Same cadence, same triggers: pawns change every round, so each player's
            // crosshair has to follow their new controller and their new pawn's AudioSources
            // need re-blending.
            CrosshairSplit.Apply();
            DeathOverlaySplit.Apply();
            ScreenEffects.Apply();
            AudioSplit.Apply();
            AudioSplit.EnforceSingleListener();

            // Idempotent and self-cancelling: opens the melee collision rows while there is
            // more than one local player, and puts the matrix back when there is not.
            DamageLayers.EnsureMeleeCollisions();
        }

        internal static void Tick()
        {
            if (!LocalPlayers.SplitActive)
                return;
            if (_dirty || Time.unscaledTime >= _nextRefresh)
                Refresh();
        }

        // ------------------------------------------------------------------ layout

        /// <summary>
        /// The slice of the screen each player gets, in Camera.rect coordinates (origin bottom
        /// left). Two players split the way the config asks; three put the odd one across the
        /// bottom; four take a quadrant each.
        /// </summary>
        internal static Rect Viewport(int index, int count)
        {
            if (count <= 1)
                return new Rect(0f, 0f, 1f, 1f);

            if (count == 2)
            {
                if (Plugin.VerticalSplit.Value)
                    return index == 0 ? new Rect(0f, 0f, 0.5f, 1f) : new Rect(0.5f, 0f, 0.5f, 1f);
                return index == 0 ? new Rect(0f, 0.5f, 1f, 0.5f) : new Rect(0f, 0f, 1f, 0.5f);
            }

            if (count == 3)
            {
                switch (index)
                {
                    case 0: return new Rect(0f, 0.5f, 0.5f, 0.5f);
                    case 1: return new Rect(0.5f, 0.5f, 0.5f, 0.5f);
                    default: return new Rect(0f, 0f, 1f, 0.5f);
                }
            }

            switch (index)
            {
                case 0: return new Rect(0f, 0.5f, 0.5f, 0.5f);
                case 1: return new Rect(0.5f, 0.5f, 0.5f, 0.5f);
                case 2: return new Rect(0f, 0f, 0.5f, 0.5f);
                default: return new Rect(0.5f, 0f, 0.5f, 0.5f);
            }
        }

        /// <summary>
        /// Lay out viewports and apply the per-player consequences of the singletons the game
        /// keeps: one HUD, one audio listener. Safe to call repeatedly, and restores vanilla
        /// once the roster is back down to one player.
        /// </summary>
        internal static void Apply()
        {
            bool split = LocalPlayers.SplitActive;
            if (split)
                Install();
            else
                Uninstall();

            int count = LocalPlayers.Count;
            foreach (var player in LocalPlayers.All)
            {
                var setup = player.Setup;
                if (setup == null)
                    continue;

                Rect rect = Viewport(player.Index, count);
                var cameras = player.Cameras;
                if (cameras != null)
                {
                    for (int i = 0; i < cameras.Length; i++)
                    {
                        var cam = cameras[i];
                        if (cam == null)
                            continue;
                        cam.rect = rect;
                    }
                }

                // cameras[2] (UICam) and hudObject are deliberately NOT touched. Every pawn
                // carries its own diegetic HUD rig under Camera/UICam on layer 25 — bullet
                // counters, heart, health number, score — and the per-camera rule above
                // already shows each player only their own, since those renderers sit under
                // cameras[0] and are therefore in the first-person set. The game's own
                // HideHUD / quality handling stays in charge of whether they are on.

                if (!split)
                {
                    // Back to one local player: hand the listener back to their own pawn.
                    foreach (var listener in setup.GetComponentsInChildren<AudioListener>(true))
                        listener.enabled = true;
                }

                if (Plugin.VerboseLogging.Value && split)
                    Plugin.Log.LogInfo($"[layout] {player.Label} viewport {rect}");
            }

            // Viewports just moved, so the crosshairs and death banners have to move with them
            // — before the next refresh tick rather than up to a quarter-second later.
            CrosshairSplit.Apply();
            DeathOverlaySplit.Apply();

            // Self-restoring: hand every camera effect its shared material back, and the kill
            // cam its full-screen rect, when the last added player is removed. Neither the
            // refresh nor the late tick runs once the split is off, so this is the only path
            // that reaches them.
            ScreenEffects.Apply();
            KillCamSplit.Tick();

            // Also here, not only in Refresh: Refresh runs only while the split is active, so
            // this is the path that puts the melee collision rows back when the last added
            // player is removed.
            DamageLayers.EnsureMeleeCollisions();

            Invalidate();
        }

        // ------------------------------------------------------------------ body override

        /// <summary>
        /// Keep every local player's third-person body switched on, every frame, from LateUpdate.
        ///
        /// FirstPersonController.HandleAnimation() runs in Update on every pawn the connection
        /// owns and ends with:
        ///
        /// <code>
        ///   if (torso.activeSelf    &amp;&amp; !isSliding &amp;&amp; !isCrouchSlopeSliding) torso.SetActive(false);
        ///   if (legLeft.activeSelf  &amp;&amp; ...) legLeft.SetActive(false);
        ///   if (legRight.activeSelf &amp;&amp; ...) legRight.SetActive(false);
        /// </code>
        ///
        /// That is vanilla's "see your own torso and legs only while sliding" effect, and a
        /// second hiding mechanism entirely separate from gameObjectsToDisableForMe. With
        /// several local players every pawn is an owner, so all of them hide their own lower
        /// body on every non-sliding frame.
        ///
        /// Timing is the fix. Re-asserting on a 0.25 s timer produced a visible flicker — the
        /// game switched the meshes off every frame and the mod switched them back on four
        /// times a second. Running every frame in LateUpdate lands after every Update and
        /// before rendering, so the meshes are always on at the moment they are drawn.
        /// </summary>
        internal static void ReassertBodyVisibility()
        {
            if (!LocalPlayers.SplitActive)
                return;

            foreach (var player in LocalPlayers.All)
            {
                var setup = player.Setup;
                if (setup == null)
                    continue;
                var meshes = HideList(setup);
                if (meshes == null)
                    continue;
                foreach (var mesh in meshes)
                {
                    if (mesh != null && !mesh.activeSelf)
                        mesh.SetActive(true);
                }
            }
        }

        private static readonly HarmonyLib.AccessTools.FieldRef<PlayerSetup, GameObject[]> HideListRef =
            HarmonyLib.AccessTools.FieldRefAccess<PlayerSetup, GameObject[]>("gameObjectsToDisableForMe");

        internal static GameObject[] HideList(PlayerSetup setup)
        {
            return setup == null ? null : HideListRef(setup);
        }
    }
}
