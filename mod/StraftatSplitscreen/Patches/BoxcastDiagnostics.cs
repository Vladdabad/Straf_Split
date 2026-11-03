using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Why a boxcast weapon's shot did or did not find a local player.
    ///
    /// `LargeRaycastGun.ShootServerBox` is the flamethrower/spray path — Phoenix, BlankState and
    /// Bayshore all take it, because their prefabs set `boxcast: 1`. It resolves damage with a
    /// single overlap test rather than a ray:
    ///
    /// <code>
    ///   Physics.OverlapBox(position + direction * (boxdimensions.z / 2), boxdimensions,
    ///                      Quaternion.LookRotation(direction), this.playerLayer)
    ///   // then: the first collider with a PlayerHealth in its parents wins
    /// </code>
    ///
    /// Which means three separate things can make it come up empty against another local
    /// player, and from the outside they look identical — no damage, no hit marker:
    ///
    ///   1. the hit-layer swap never ran, so the target is still on SelfBody (16);
    ///   2. the swap ran, but `playerLayer` does not admit the layer they are on;
    ///   3. both were fine and the box simply did not reach them.
    ///
    /// Reporting the shooter, the swap state, the box, and every collider the same call finds
    /// separates the three on the next trigger pull. Postfix only — it re-runs the overlap for
    /// observation and changes nothing.
    ///
    /// Gated behind `[Debug] TraceBoxcast`, and rate-limited: a flamethrower calls this every
    /// frame it is held down.
    /// </summary>
    [HarmonyPatch(typeof(LargeRaycastGun), "ShootServerBox")]
    internal static class LargeRaycastGun_ShootServerBox_Patch
    {
        private static readonly AccessTools.FieldRef<LargeRaycastGun, Vector3> BoxRef =
            AccessTools.FieldRefAccess<LargeRaycastGun, Vector3>("boxdimensions");

        private static readonly System.Reflection.FieldInfo EnemyHealthField =
            AccessTools.Field(typeof(LargeRaycastGun), "enemyHealth");

        private static float _nextLog;

        // Priority.First so this runs BEFORE Weapon_ShootServer_Patch's postfix, which calls
        // DamageLayers.End(). Without it the swap is already undone by the time we look, and the
        // two things worth knowing — what layer each player was on and what the box could see —
        // are both read from the restored state, which always says 16 and always finds nobody.
        [HarmonyPriority(Priority.First)]
        private static void Postfix(LargeRaycastGun __instance, Vector3 position, Vector3 direction)
        {
            if (!Plugin.TraceBoxcast.Value || !LocalPlayers.SplitActive)
                return;

            // Held triggers call this every frame; one line every half second is enough to see
            // what is happening without burying the rest of the log.
            if (Time.unscaledTime < _nextLog)
                return;
            _nextLog = Time.unscaledTime + 0.5f;

            var shooter = LocalPlayers.OwningPawn(__instance.transform.root.gameObject);
            var hit = EnemyHealthField != null ? EnemyHealthField.GetValue(__instance) as PlayerHealth : null;

            var box = BoxRef(__instance);
            var found = Physics.OverlapBox(
                position + direction * (box.z / 2f), box, Quaternion.LookRotation(direction), __instance.playerLayer);

            var sb = new System.Text.StringBuilder();
            foreach (var col in found)
            {
                if (col == null)
                    continue;
                var health = col.GetComponentInParent<PlayerHealth>();
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append($"{col.name}(layer {col.gameObject.layer}{(health != null ? ", PlayerHealth" : "")})");
            }

            // What the other local players' HITBOXES are on right now, which is the whole
            // question: 11 means the swap reached them, 16 means it did not.
            //
            // The hitbox colliders, not Graphics.layer — the swap moves the individual collider
            // objects under colliderParent, so the subtree root reads 16 either way and reported
            // a swap failure that was not happening.
            var layers = new System.Text.StringBuilder();
            foreach (var p in LocalPlayers.All)
            {
                if (layers.Length > 0)
                    layers.Append(' ');
                layers.Append($"{p.Label}=");

                var cols = p.BodyColliders;
                if (cols == null || cols.Length == 0)
                {
                    layers.Append(p.Pawn != null ? "no hitboxes cached" : "no pawn");
                    continue;
                }

                var seen = new List<int>();
                foreach (var c in cols)
                {
                    if (c == null)
                        continue;
                    int l = c.gameObject.layer;
                    if (!seen.Contains(l))
                        seen.Add(l);
                }
                layers.Append(seen.Count == 0 ? "none" : string.Join("/", seen.ConvertAll(l => l.ToString()).ToArray()));
            }

            Plugin.Log.LogInfo(
                $"[boxcast] {__instance.GetType().Name} '{__instance.name}' from " +
                $"{(shooter != null ? shooter.Label : "remote/untagged")}: box={box} mask={(int)__instance.playerLayer} " +
                $"hit={(hit != null ? hit.name : "NOTHING")} | body layers: {layers} | in box: {(sb.Length > 0 ? sb.ToString() : "nothing")}");
        }
    }
}
