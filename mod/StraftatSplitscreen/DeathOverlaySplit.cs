using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// One death overlay per local player.
    ///
    /// The "DEAD" banner and the "ENEMY HEALTH : n HP" line under it are not pawn geometry —
    /// like the crosshair they are screen-space UI that exists once per scene, held by
    /// `PauseManager` in two private fields (`deadText`, `enemyHealthText`) and driven by one
    /// coroutine:
    ///
    ///     PlayerSetup.OnDisable (the dying pawn)
    ///         -> PauseManager.ShowEnemyHealth(enemyHealth, ownHealth)
    ///         -> wait 0.2s, write "DEAD" + "ENEMY HEALTH : n HP", wait 2.7s, clear
    ///
    /// A ScreenSpaceOverlay canvas ignores camera viewport rects, so whoever dies, that single
    /// pair of texts appears at its authored place on the whole screen — inside player 1's
    /// slice. Every player was being told player 2's death was theirs.
    ///
    /// Same shape of fix as <see cref="CrosshairSplit"/>: each extra player gets a clone of the
    /// pair, placed inside their own viewport, and player 1 is not special — the same code runs
    /// for them (and works out as a no-op, since every layout puts player 1's viewport in the
    /// screen's top-left corner).
    ///
    /// Placement is a pure TRANSLATION, not the crosshair's proportional remap. The banner is
    /// authored at a fixed offset from the top-left of the screen; it keeps that offset, its
    /// size and its scale exactly, and only the corner it is measured from changes — from the
    /// screen's top-left to the viewport's:
    ///
    ///     delta = (rect.x, (rect.y + rect.height) - 1)      // viewport's top-left, screen-relative
    ///
    /// Two stacked players put P2's banner half a screen down, on the left; four players put
    /// P2's at the top centre and P4's at dead centre, each still carrying its original offset.
    /// Translating via `anchoredPosition` rather than the anchors keeps the element's size and
    /// shape whatever its anchors are, stretched or fixed.
    ///
    /// The write is attributed by patching `ShowEnemyHealth`, which is handed the dying pawn's
    /// own `PlayerHealth` — so the roster lookup names the player without touching the two
    /// dozen paths that can kill someone. Player 1 keeps the vanilla coroutine; the others are
    /// driven by <see cref="Tick"/>, which reproduces its timing rather than starting coroutines
    /// (the mod owns no MonoBehaviour to run them on).
    /// </summary>
    internal static class DeathOverlaySplit
    {
        /// <summary>Vanilla `ShowEnemyHealthCoroutine`: 0.2 s before the text appears, 2.7 s on screen.</summary>
        private const float ShowDelay = 0.2f;

        private const float HoldFor = 2.7f;

        private sealed class Rig
        {
            internal TMP_Text Dead;
            internal TMP_Text Enemy;

            /// <summary>Scaled time the banner is due to appear, or 0 when nothing is pending.</summary>
            internal float ShowAt;

            /// <summary>Scaled time the banner is due to clear, or 0 when nothing is showing.</summary>
            internal float HideAt;

            internal PlayerHealth Health;

            /// <summary>
            /// This player's own health when the request came in. Read only if the pawn is gone
            /// by the time the banner is due: a round-end despawn calls the same method with a
            /// living pawn, and it is the health check that keeps "DEAD" off a survivor's screen.
            /// </summary>
            internal float RequestHealth;

            internal float EnemyHealth;
        }

        /// <summary>Where the game authored an element, so every placement starts from vanilla rather than from the last one.</summary>
        private sealed class Home
        {
            internal bool Valid;
            internal Vector2 Position;
        }

        private static readonly Dictionary<int, Rig> _rigs = new Dictionary<int, Rig>();

        private static PauseManager _source;
        private static TMP_Text _sourceDead;
        private static TMP_Text _sourceEnemy;
        private static Home _deadHome = new Home();
        private static Home _enemyHome = new Home();
        private static bool _warned;

        // ------------------------------------------------------------------ placement

        /// <summary>
        /// Re-anchor player 1's texts and every clone into their own viewport. Cheap and
        /// idempotent; called on the same cadence as the crosshair, since both follow the
        /// viewport layout rather than the pawn.
        /// </summary>
        internal static void Apply()
        {
            var pause = PauseManager.Instance;
            if (pause == null)
                return;

            var dead = Resolve(pause, "deadText");
            var enemy = Resolve(pause, "enemyHealthText");

            // Every map scene carries its own UI canvas, so a map load replaces the source pair
            // and orphans the clones. Rebuild against whatever is current.
            if (!ReferenceEquals(pause, _source) ||
                !ReferenceEquals(dead, _sourceDead) ||
                !ReferenceEquals(enemy, _sourceEnemy))
            {
                DestroyRigs(0);
                _source = pause;
                _sourceDead = dead;
                _sourceEnemy = enemy;
                _deadHome = Capture(dead);
                _enemyHome = Capture(enemy);

                if (dead == null)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Plugin.Log.LogWarning(
                            "[death] PauseManager.deadText could not be resolved — the death banner " +
                            "stays shared and will keep appearing in P1's viewport.");
                    }
                }
                else
                {
                    Plugin.Log.LogInfo(
                        $"[death] overlay source: '{dead.name}'" +
                        (enemy != null ? $" + '{enemy.name}'" : " (no enemy-health text)") + ".");
                    Describe(dead);
                    Describe(enemy);
                }
            }

            if (_sourceDead == null)
                return;

            if (!LocalPlayers.SplitActive)
            {
                RestoreSource();
                DestroyRigs(0);
                return;
            }

            int count = LocalPlayers.Count;
            foreach (var player in LocalPlayers.All)
            {
                Rect view = PlayerVisibility.Viewport(player.Index, count);

                if (player.Index == 0)
                {
                    Place(_sourceDead, _deadHome, view);
                    Place(_sourceEnemy, _enemyHome, view);
                    continue;
                }

                var rig = EnsureRig(player.Index);
                if (rig == null)
                    continue;
                Place(rig.Dead, _deadHome, view);
                Place(rig.Enemy, _enemyHome, view);
            }

            DestroyRigs(count);
        }

        /// <summary>
        /// Move an element from the screen's top-left corner to its player's viewport's, keeping
        /// its authored offset from that corner, its size and its scale.
        ///
        /// Always measured from the element's home rather than from wherever it currently sits,
        /// so re-applying every layout change cannot accumulate drift.
        /// </summary>
        private static void Place(TMP_Text text, Home home, Rect view)
        {
            if (text == null || home == null || !home.Valid)
                return;
            var rt = text.rectTransform;
            if (rt == null)
                return;
            rt.anchoredPosition = home.Position + Offset(rt, view);
        }

        /// <summary>
        /// How far a viewport's top-left corner sits from the screen's, in the units the
        /// element's own parent measures in.
        ///
        /// Canvas rect coordinates run bottom-up, so the viewport's top edge is
        /// `rect.y + rect.height` and the screen's is 1: the difference is zero or negative, and
        /// a lower slice moves the banner down. Player 1 always comes out at (0,0) — every
        /// layout in <see cref="PlayerVisibility.Viewport"/> starts their slice in that corner —
        /// so this leaves their banner exactly where the game put it.
        /// </summary>
        private static Vector2 Offset(RectTransform rt, Rect view)
        {
            float dx = view.x;
            float dy = view.y + view.height - 1f;
            if (Mathf.Approximately(dx, 0f) && Mathf.Approximately(dy, 0f))
                return Vector2.zero;

            var canvas = rt.GetComponentInParent<Canvas>();
            var canvasRt = canvas != null ? canvas.rootCanvas.GetComponent<RectTransform>() : null;
            if (canvasRt == null)
                return Vector2.zero;
            Vector2 size = canvasRt.rect.size;

            // anchoredPosition is in the parent's local units. Those are canvas units unless
            // something in between is scaled — PauseManager drives `container.localScale` for
            // the round-start animation, so do not assume they are the same.
            float ratio = 1f;
            var parent = rt.parent as RectTransform;
            if (parent != null && Mathf.Abs(parent.lossyScale.x) > 0.0001f)
                ratio = canvasRt.lossyScale.x / parent.lossyScale.x;

            return new Vector2(dx * size.x * ratio, dy * size.y * ratio);
        }

        private static Home Capture(TMP_Text text)
        {
            var home = new Home();
            var rt = text != null ? text.rectTransform : null;
            if (rt == null)
                return home;
            home.Valid = true;
            home.Position = rt.anchoredPosition;
            return home;
        }

        private static void RestoreSource()
        {
            Restore(_sourceDead, _deadHome);
            Restore(_sourceEnemy, _enemyHome);
        }

        private static void Restore(TMP_Text text, Home home)
        {
            if (text == null || home == null || !home.Valid)
                return;
            var rt = text.rectTransform;
            if (rt == null)
                return;
            rt.anchoredPosition = home.Position;
        }

        /// <summary>
        /// The geometry of a source element, once, when it is first resolved. This is the only
        /// record of how these two texts are authored — AssetRipper lost the scene's references
        /// to them, so the shipped anchors cannot be read from the exported project.
        /// </summary>
        private static void Describe(TMP_Text text)
        {
            if (text == null)
                return;
            var rt = text.rectTransform;
            if (rt == null)
                return;
            var canvas = rt.GetComponentInParent<Canvas>();
            var canvasRt = canvas != null ? canvas.rootCanvas.GetComponent<RectTransform>() : null;
            Plugin.Log.LogInfo(
                $"[death]   '{text.name}' under '{(rt.parent != null ? rt.parent.name : "<none>")}': " +
                $"anchors {rt.anchorMin}-{rt.anchorMax} pivot {rt.pivot} pos {rt.anchoredPosition} " +
                $"size {rt.sizeDelta} scale {rt.localScale} " +
                $"canvas {(canvasRt != null ? canvasRt.rect.size.ToString() : "<none>")}");
        }

        // ------------------------------------------------------------------ the banner

        /// <summary>
        /// Take over one player's death banner. Returns false when it cannot be honoured — the
        /// caller then lets the vanilla coroutine run, so a failure here is a misplaced banner
        /// rather than no banner at all.
        /// </summary>
        internal static bool Request(LocalPlayer player, float enemyHealth, PlayerHealth own)
        {
            if (player == null || player.Index == 0)
                return false;

            var rig = EnsureRig(player.Index);
            if (rig == null || rig.Dead == null)
                return false;

            rig.Health = own;
            rig.RequestHealth = own != null ? own.health : 0f;
            rig.EnemyHealth = enemyHealth;
            rig.ShowAt = Time.time + ShowDelay;
            rig.HideAt = 0f;
            return true;
        }

        /// <summary>
        /// Drive every clone's banner. This is `ShowEnemyHealthCoroutine` unrolled onto the
        /// frame tick, including the end-of-round blanking `PauseManager.Update` does to the
        /// original. Scaled time, deliberately — WaitForSeconds stops while the game is paused
        /// and so should this.
        /// </summary>
        internal static void Tick()
        {
            if (_rigs.Count == 0)
                return;

            var pause = PauseManager.Instance;
            bool blank = pause == null || pause.onEndRoundScreen || pause.inMainMenu;
            var manager = GameManager.Instance;
            bool roundWasWon = manager != null && manager.roundWasWon;
            float now = Time.time;

            foreach (var kv in _rigs)
            {
                var rig = kv.Value;

                if (blank)
                {
                    if (rig.ShowAt > 0f || rig.HideAt > 0f)
                    {
                        rig.ShowAt = 0f;
                        rig.HideAt = 0f;
                    }
                    Write(rig, "", "");
                    continue;
                }

                if (rig.ShowAt > 0f && now >= rig.ShowAt)
                {
                    rig.ShowAt = 0f;
                    rig.HideAt = now + HoldFor;

                    float health = rig.Health != null ? rig.Health.health : rig.RequestHealth;
                    if (health <= 0f && !roundWasWon)
                    {
                        Write(rig, "DEAD",
                            "ENEMY HEALTH : " + Mathf.Ceil(rig.EnemyHealth / 4f * 100f) + " HP");
                    }
                }

                if (rig.HideAt > 0f && now >= rig.HideAt)
                {
                    rig.HideAt = 0f;
                    Write(rig, "", "");
                }
            }
        }

        private static void Write(Rig rig, string dead, string enemy)
        {
            if (rig.Dead != null && rig.Dead.text != dead)
                rig.Dead.text = dead;
            if (rig.Enemy != null && rig.Enemy.text != enemy)
                rig.Enemy.text = enemy;
        }

        // ------------------------------------------------------------------ clones

        private static Rig EnsureRig(int index)
        {
            Rig rig;
            if (_rigs.TryGetValue(index, out rig) && rig.Dead != null)
                return rig;

            if (_sourceDead == null)
                return null;

            rig = new Rig
            {
                Dead = Clone(_sourceDead, "DeadText_P" + (index + 1)),
                Enemy = Clone(_sourceEnemy, "EnemyHealthText_P" + (index + 1)),
            };
            if (rig.Dead == null)
                return null;

            // A clone inherits whatever the original was showing at the moment it was made,
            // which would be somebody else's death.
            Write(rig, "", "");

            _rigs[index] = rig;
            Rect view = PlayerVisibility.Viewport(index, LocalPlayers.Count);
            Place(rig.Dead, _deadHome, view);
            Place(rig.Enemy, _enemyHome, view);

            Plugin.Log.LogInfo($"[death] created a death overlay for P{index + 1}.");
            return rig;
        }

        private static TMP_Text Clone(TMP_Text source, string name)
        {
            if (source == null)
                return null;
            var go = Object.Instantiate(source.gameObject, source.transform.parent);
            go.name = name;
            var text = go.GetComponent<TMP_Text>();
            if (text == null)
                Object.Destroy(go);
            return text;
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
                if (rig.Dead != null)
                    Object.Destroy(rig.Dead.gameObject);
                if (rig.Enemy != null)
                    Object.Destroy(rig.Enemy.gameObject);
                _rigs.Remove(index);
                Plugin.Log.LogInfo($"[death] removed the death overlay for P{index + 1}.");
            }
        }

        /// <summary>
        /// Both texts are [SerializeField] private. Looked up by name rather than through a
        /// cached FieldRef so a rename degrades to "leave it to vanilla" instead of throwing at
        /// class-load time.
        /// </summary>
        private static TMP_Text Resolve(PauseManager pause, string field)
        {
            var f = AccessTools.Field(typeof(PauseManager), field);
            return f != null ? f.GetValue(pause) as TMP_Text : null;
        }
    }
}
