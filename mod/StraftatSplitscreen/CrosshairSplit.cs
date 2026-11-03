using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StraftatSplitscreen
{
    /// <summary>
    /// One crosshair per local player.
    ///
    /// Unlike the rest of the HUD, the crosshair is NOT pawn geometry — it is a screen-space
    /// UI image that exists once per scene:
    ///
    ///     ---USER INTERFACE---            Canvas, ScreenSpaceOverlay
    ///         Crosshair                   anchor (0.5, 0.5), the Crosshair component
    ///         ScopeCrosshair              the scope overlay, a SIBLING
    ///
    /// A ScreenSpaceOverlay canvas ignores camera viewport rects, so the single crosshair sits
    /// at the centre of the whole screen — which in splitscreen is the seam between viewports,
    /// wrong for everybody. And `Crosshair.player` is one reference, so its sprite tracks one
    /// player's weapon, sprint and aim state.
    ///
    /// So each extra player gets a clone of that pair, anchored to the centre of their own
    /// viewport. Normalized viewport coordinates map straight onto canvas anchors for a
    /// full-screen overlay canvas, which is what makes this a two-line placement rather than a
    /// projection problem:
    ///
    ///     anchorMin = anchorMax = (rect.x + rect.width/2, rect.y + rect.height/2)
    ///
    /// Player 1 is not special: their scene crosshair is re-anchored by the same code, and
    /// restored to dead centre when the roster drops back to one.
    ///
    /// The clone must be a pair. `Crosshair.scopeImage` points at ScopeCrosshair, a sibling
    /// OUTSIDE the crosshair's own hierarchy, and Instantiate only remaps references that
    /// point within the copied subtree — so a lone clone would keep driving player 1's scope
    /// overlay.
    ///
    /// And the pair is placed by two different rules, not one. The Crosshair is anchored at a
    /// point; ScopeCrosshair is stretched to fill its parent, and a stretched rect carries its
    /// size in the gap between its anchors — so the point rule collapses it to 0x0 and the
    /// scope reticle disappears while the crosshair, whose sizeDelta the game rewrites every
    /// frame anyway, looks perfectly fine. See <see cref="Place"/>.
    /// </summary>
    internal static class CrosshairSplit
    {
        private sealed class Rig
        {
            internal Crosshair Cross;
            internal GameObject Scope;
        }

        /// <summary>
        /// How a RectTransform was authored, captured before anything here moves it.
        ///
        /// <see cref="Stretched"/> is the field that matters, and it is the difference
        /// between the two objects in the rig. The Crosshair is anchored at a point and
        /// carries its size in sizeDelta; ScopeCrosshair is anchored (0,0)..(1,1) with
        /// sizeDelta (0,0), which is how Unity spells "fill the parent". Those need
        /// opposite treatment, and the authored values are the only place that is
        /// recoverable from — once a placement has run, the live anchors are ours, not the
        /// scene's.
        /// </summary>
        private struct Layout
        {
            internal Vector2 AnchorMin;
            internal Vector2 AnchorMax;
            internal Vector2 SizeDelta;
            internal Vector2 AnchoredPosition;
            internal Vector3 Scale;
            internal bool Valid;

            /// <summary>Authored to fill its parent rather than to sit at a point.</summary>
            internal bool Stretched => AnchorMin != AnchorMax;
        }

        private static readonly Dictionary<int, Rig> _rigs = new Dictionary<int, Rig>();

        private static Crosshair _source;
        private static GameObject _sourceScope;
        private static Layout _sourceLayout;
        private static Layout _sourceScopeLayout;

        /// <summary>
        /// This player's own Crosshair component, or null if they do not have one yet.
        ///
        /// Player 1 keeps the scene's original — the one `Crosshair.Instance` already points at —
        /// so it is not in the rig table and is answered from the static instead.
        /// </summary>
        internal static Crosshair For(int index)
        {
            Rig rig;
            if (_rigs.TryGetValue(index, out rig) && rig != null && rig.Cross != null)
                return rig.Cross;
            return index == 0 ? Crosshair.Instance : null;
        }

        internal static void Apply()
        {
            var source = Crosshair.Instance;
            if (source == null)
                return;

            // Every map scene carries its own crosshair, so a map load replaces the source and
            // orphans the clones. Rebuild against whatever is current.
            if (!ReferenceEquals(source, _source))
            {
                _rigs.Clear();
                _source = source;
                _sourceScope = source.scopeImage != null ? source.scopeImage.gameObject : null;
                Capture();
            }

            if (!LocalPlayers.SplitActive)
            {
                RestoreSource();
                DestroyRigs(0);
                return;
            }

            int count = LocalPlayers.Count;
            foreach (var player in LocalPlayers.All)
            {
                Crosshair cross;
                GameObject scope;

                if (player.Index == 0)
                {
                    cross = source;
                    scope = _sourceScope;
                }
                else
                {
                    var rig = EnsureRig(player.Index);
                    if (rig == null)
                        continue;
                    cross = rig.Cross;
                    scope = rig.Scope;

                    // A player between pawns has nothing to aim: hide their crosshair rather
                    // than leaving the last sprite frozen on screen.
                    bool live = player.Controller != null;
                    if (cross.gameObject.activeSelf != live)
                        cross.gameObject.SetActive(live);
                    if (scope != null && scope.activeSelf != live)
                        scope.SetActive(live);
                }

                if (cross == null)
                    continue;

                cross.player = player.Controller;
                Rect rect = PlayerVisibility.Viewport(player.Index, count);

                // The authored layout of the SOURCE for both, clone or not: a clone comes
                // out of Instantiate with exactly the source's values, and by the second
                // frame the live ones are whatever the last placement wrote.
                Place(ResolveRect(cross), rect, _sourceLayout);
                if (scope != null)
                    Place(scope.GetComponent<RectTransform>(), rect, _sourceScopeLayout);
            }

            DestroyRigs(count);
        }

        // ------------------------------------------------------------------ placement

        /// <summary>
        /// Move one element of the rig into a player's viewport, the way it was authored to
        /// be sized.
        ///
        /// The two objects need opposite treatment, and treating them alike is what made
        /// scoped rifles show no reticle:
        ///
        ///  - **Point-anchored** (the Crosshair) — pin the anchor to the centre of the
        ///    viewport and scale it down. Scale matters as much as position: the crosshair is
        ///    a fixed 400x400 canvas units and Crosshair.Update rewrites sizeDelta every
        ///    frame, so sizeDelta cannot be the lever here; at quarter-screen an unscaled
        ///    crosshair would swallow the viewport. localScale is untouched by the game,
        ///    which makes it the safe one.
        ///
        ///  - **Stretched** (ScopeCrosshair) — re-stretch it across the viewport instead.
        ///    The scene authors it anchorMin (0,0) / anchorMax (1,1) / sizeDelta (0,0), which
        ///    is Unity's spelling of "fill the parent": the size lives entirely in the gap
        ///    between the anchors. Collapsing both anchors to a point, as the point-anchored
        ///    branch does, therefore leaves sizeDelta (0,0) meaning a rect of literally zero
        ///    size — a full-screen scope overlay reduced to 0x0 pixels, present and correct
        ///    in every other respect and drawing nothing at all.
        ///
        /// The ordinary crosshair survived that same collapse only by accident, because
        /// Crosshair.Update puts its sizeDelta back to 400x400 every frame. Nothing does that
        /// for the scope image, which is why exactly the scope reticle went missing while the
        /// crosshair looked fine.
        ///
        /// Spanning the viewport is also what the overlay means: a scope vignette covers the
        /// screen, and in splitscreen that player's screen is their slice of it.
        /// </summary>
        private static void Place(RectTransform rt, Rect rect, Layout authored)
        {
            if (rt == null)
                return;

            if (authored.Stretched)
            {
                rt.anchorMin = new Vector2(rect.xMin, rect.yMin);
                rt.anchorMax = new Vector2(rect.xMax, rect.yMax);
                // Restored rather than assumed: these carry the authored inset and scale, so
                // the object fills its viewport exactly as it filled the screen.
                rt.anchoredPosition = authored.AnchoredPosition;
                rt.sizeDelta = authored.SizeDelta;
                rt.localScale = authored.Scale;
                return;
            }

            var centre = new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);
            rt.anchorMin = centre;
            rt.anchorMax = centre;
            rt.anchoredPosition = Vector2.zero;
            float scale = Mathf.Min(rect.width, rect.height);
            rt.localScale = new Vector3(scale, scale, 1f);
        }

        /// <summary>
        /// The crosshair's RectTransform, repairing the reference if the scene left it unset.
        /// Crosshair.Update dereferences this field every frame, so a null there is a
        /// per-frame exception waiting to happen; pointing it at its own RectTransform is what
        /// it was always meant to be.
        /// </summary>
        private static RectTransform ResolveRect(Crosshair cross)
        {
            if (cross.rectTransform != null)
                return cross.rectTransform;
            var rt = cross.GetComponent<RectTransform>();
            if (rt != null)
                cross.rectTransform = rt;
            return rt;
        }

        private static void Capture()
        {
            _sourceLayout = CaptureOne(ResolveRect(_source));
            _sourceScopeLayout = CaptureOne(ScopeRect());
        }

        private static Layout CaptureOne(RectTransform rt)
        {
            if (rt == null)
                return default(Layout);
            return new Layout
            {
                AnchorMin = rt.anchorMin,
                AnchorMax = rt.anchorMax,
                SizeDelta = rt.sizeDelta,
                AnchoredPosition = rt.anchoredPosition,
                Scale = rt.localScale,
                Valid = true,
            };
        }

        private static RectTransform ScopeRect()
        {
            return _sourceScope != null ? _sourceScope.GetComponent<RectTransform>() : null;
        }

        private static void RestoreSource()
        {
            if (_source == null)
                return;
            Restore(ResolveRect(_source), _sourceLayout);
            Restore(ScopeRect(), _sourceScopeLayout);
        }

        /// <summary>
        /// Put one element back exactly as the scene authored it. All five fields, not the
        /// anchor alone: a stretched object carries its size in anchorMax and sizeDelta
        /// together, so restoring half of them is how it comes back the wrong size.
        /// </summary>
        private static void Restore(RectTransform rt, Layout authored)
        {
            if (rt == null || !authored.Valid)
                return;
            rt.anchorMin = authored.AnchorMin;
            rt.anchorMax = authored.AnchorMax;
            rt.anchoredPosition = authored.AnchoredPosition;
            rt.sizeDelta = authored.SizeDelta;
            rt.localScale = authored.Scale;
        }

        // ------------------------------------------------------------------ clones

        private static Rig EnsureRig(int index)
        {
            Rig rig;
            if (_rigs.TryGetValue(index, out rig) && rig.Cross != null)
                return rig;

            if (_source == null)
                return null;

            var parent = _source.transform.parent;
            var crossGo = Object.Instantiate(_source.gameObject, parent);
            crossGo.name = "Crosshair_P" + (index + 1);
            var cross = crossGo.GetComponent<Crosshair>();
            if (cross == null)
            {
                Object.Destroy(crossGo);
                return null;
            }

            // Awake on the clone leaves Crosshair.Instance alone (it is already player 1's)
            // and re-resolves its own Image, so only the sibling reference needs rewiring.
            GameObject scopeGo = null;
            if (_sourceScope != null)
            {
                scopeGo = Object.Instantiate(_sourceScope, parent);
                scopeGo.name = "ScopeCrosshair_P" + (index + 1);
                var img = scopeGo.GetComponent<Image>();
                if (img != null)
                    cross.scopeImage = img;
            }

            rig = new Rig { Cross = cross, Scope = scopeGo };
            _rigs[index] = rig;
            Plugin.Log.LogInfo($"[crosshair] created a crosshair for P{index + 1}.");
            return rig;
        }

        private static void DestroyRigs(int keepBelow)
        {
            var stale = new List<int>();
            foreach (var kv in _rigs)
            {
                if (kv.Key >= keepBelow)
                    stale.Add(kv.Key);
            }
            foreach (int index in stale)
            {
                var rig = _rigs[index];
                if (rig.Cross != null)
                    Object.Destroy(rig.Cross.gameObject);
                if (rig.Scope != null)
                    Object.Destroy(rig.Scope);
                _rigs.Remove(index);
                Plugin.Log.LogInfo($"[crosshair] removed the crosshair for P{index + 1}.");
            }
        }
    }
}
