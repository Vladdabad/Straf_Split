using System.Collections.Generic;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// The death view, confined to the viewport of whoever died.
    ///
    /// When a player dies their pawn is deactivated, taking its camera with it, and the scene's
    /// `Main Camera` — the one carrying `KillCam` — is switched on to spectate:
    ///
    ///     PlayerHealth.Update    health &lt;= 0  -> mainCamObject.GetComponent&lt;Camera&gt;().enabled = true
    ///     PlayerSetup.OnDisable                -> sceneCamera.enabled = true
    ///     PlayerSetup.Update     (every OWNED, living pawn, every frame)
    ///                                          -> Camera.main.enabled = false
    ///
    /// One camera with a full-screen rect, and a switch that every living local pawn flips back
    /// off. In splitscreen that gives the dead player a black slice — their own camera is gone
    /// and the kill cam is turned off again by whoever is still alive — while the frames it does
    /// survive paint over everybody.
    ///
    /// So the kill cam is given a viewport like any other player's camera, and held on while
    /// anyone local is dead. A second and third dead player get a **mirror**: a bare Camera of
    /// our own that copies the scene camera's settings and pose each frame and draws them into
    /// their own slice. `Camera.CopyFrom` rather than cloning the GameObject, so no second
    /// `KillCam`, `AudioListener` or post-process stack comes along — one KillCam still drives
    /// the pose, and the mirrors are pure output.
    ///
    /// Two cases deliberately keep vanilla's single full-screen kill cam:
    ///
    /// - **Nobody is dead** — there is nothing to show, and the game owns the switch.
    /// - **Everybody is dead** — which is also every round transition, when all the pawns are
    ///   briefly gone. Slicing the same spectator view four ways there would be flicker for no
    ///   information.
    ///
    /// What this does not fix: there is one `KillCam`, so every dead player shares its pose and
    /// its spectator target, and it reads the shared input static — free-cam and target
    /// switching answer to player 1. Splitting that means a second KillCam driving the shared
    /// `SwitchCamText` / `PlayerNameText` / `PlayerHpText`, which are singletons of their own.
    /// </summary>
    internal static class KillCamSplit
    {
        private static Camera _scene;
        private static Rect _sceneRect = new Rect(0f, 0f, 1f, 1f);
        private static bool _sceneRectCaptured;
        private static bool _resized;

        private static readonly Dictionary<int, Camera> _mirrors = new Dictionary<int, Camera>();

        /// <summary>
        /// Runs on the late tick, deliberately: `PlayerSetup.Update` disables the kill cam from
        /// every living pawn, so the state has to be settled after all of them have had their
        /// say and before anything renders.
        /// </summary>
        internal static void Tick()
        {
            if (!Plugin.SplitKillCam.Value)
            {
                Restore();
                return;
            }

            var scene = Resolve();
            if (scene == null)
                return;

            var pause = PauseManager.Instance;
            if (!LocalPlayers.SplitActive || pause == null || pause.inMainMenu)
            {
                Restore();
                return;
            }

            int count = LocalPlayers.Count;
            List<LocalPlayer> dead = null;
            foreach (var player in LocalPlayers.All)
            {
                var pawn = player.Pawn;
                if (pawn == null || !pawn.activeInHierarchy)
                    (dead ?? (dead = new List<LocalPlayer>())).Add(player);
            }

            if (dead == null || dead.Count >= count)
            {
                Restore();
                return;
            }

            // The first dead player gets the real camera; the rest get mirrors of it.
            scene.rect = PlayerVisibility.Viewport(dead[0].Index, count);
            scene.enabled = true;
            _resized = true;

            var wanted = new HashSet<int>();
            for (int i = 1; i < dead.Count; i++)
            {
                int index = dead[i].Index;
                var mirror = EnsureMirror(index);
                if (mirror == null)
                    continue;
                wanted.Add(index);

                mirror.CopyFrom(scene);
                mirror.rect = PlayerVisibility.Viewport(index, count);
                mirror.transform.SetPositionAndRotation(scene.transform.position, scene.transform.rotation);
                mirror.enabled = true;
            }
            DropMirrors(wanted);
        }

        /// <summary>
        /// The scene's kill cam. Found by its component rather than by name, and re-found on
        /// every map load, since each map scene ships its own.
        /// </summary>
        private static Camera Resolve()
        {
            if (_scene != null)
                return _scene;

            var killCam = Object.FindObjectOfType<KillCam>();
            var cam = killCam != null ? killCam.GetComponent<Camera>() : null;
            if (cam == null)
                return null;

            _scene = cam;
            _sceneRect = cam.rect;
            _sceneRectCaptured = true;
            _resized = false;
            Plugin.Log.LogInfo($"[killcam] scene kill cam '{cam.name}' found (rect {_sceneRect}).");
            return _scene;
        }

        /// <summary>
        /// Hand the kill cam back its full-screen rect and drop the mirrors. Its `enabled` is
        /// left to the game — vanilla's own switching is right once nobody needs a slice.
        /// </summary>
        private static void Restore()
        {
            if (_resized && _scene != null && _sceneRectCaptured)
                _scene.rect = _sceneRect;
            _resized = false;
            DropMirrors(null);
        }

        private static Camera EnsureMirror(int index)
        {
            Camera mirror;
            if (_mirrors.TryGetValue(index, out mirror) && mirror != null)
                return mirror;

            // A bare GameObject, deliberately untagged: a second object tagged MainCamera would
            // be picked up by Camera.main, and PlayerSetup.Update would start switching ours off
            // and caching it as the scene camera.
            var go = new GameObject("KillCamMirror_P" + (index + 1));
            mirror = go.AddComponent<Camera>();
            mirror.enabled = false;
            _mirrors[index] = mirror;
            Plugin.Log.LogInfo($"[killcam] created a death-view mirror for P{index + 1}.");
            return mirror;
        }

        private static void DropMirrors(HashSet<int> keep)
        {
            if (_mirrors.Count == 0)
                return;

            List<int> stale = null;
            foreach (var kv in _mirrors)
            {
                if (keep != null && keep.Contains(kv.Key) && kv.Value != null)
                    continue;
                (stale ?? (stale = new List<int>())).Add(kv.Key);
            }
            if (stale == null)
                return;

            foreach (int index in stale)
            {
                var mirror = _mirrors[index];
                if (mirror != null)
                    Object.Destroy(mirror.gameObject);
                _mirrors.Remove(index);
            }
        }
    }
}
