using System;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Makes shooting work with two local players.
    ///
    /// Vanilla's server-side hit resolution is:
    ///
    ///     Physics.Raycast(pos, dir, out hit, inf)
    ///       &amp;&amp; hit.transform.TryGetComponent&lt;PlayerHealth&gt;(out hp)
    ///       &amp;&amp; hit.transform.gameObject != base.gameObject
    ///
    /// It damages only when the FIRST collider along the ray is the exact GameObject
    /// carrying PlayerHealth — the pawn root and its CharacterController capsule. That
    /// holds in vanilla because the shooter's own body meshes are switched off and the
    /// enclosing capsule of the target is the outermost thing the ray meets.
    ///
    /// Splitscreen breaks both halves of that assumption: every local pawn runs the owner
    /// branch, so all of them keep their third-person bodies enabled (we need them visible
    /// to each other),
    /// which puts the shooter's own arms/hands directly in front of the camera and the
    /// target's limb colliders in front of the target's root. Either one makes
    /// TryGetComponent fail on the hit object, and the shot silently does nothing.
    ///
    /// This replacement keeps the same rules — first thing hit wins, walls still block,
    /// no self-damage — but resolves them correctly:
    ///   * ignore every collider belonging to the shooter's own pawn,
    ///   * take the nearest remaining hit,
    ///   * look for PlayerHealth/DollHealth on it OR its parents, so limb colliders count.
    ///
    /// Only active while more than one local player exists; otherwise vanilla runs untouched.
    /// </summary>
    [HarmonyPatch(typeof(PlayerShoot), "RpcLogic___ShootServer_1867452766")]
    internal static class PlayerShoot_ShootServer_Patch
    {
        private static bool Prefix(PlayerShoot __instance, float damageToGive, Vector3 position, Vector3 direction)
        {
            if (!LocalPlayers.SplitActive)
                return true; // vanilla behaviour when not in splitscreen

            Transform shooterRoot = __instance.transform.root;
            RaycastHit[] hits = Physics.RaycastAll(position, direction, float.PositiveInfinity);
            if (hits.Length == 0)
            {
                if (Plugin.VerboseLogging.Value)
                    Plugin.Log.LogInfo("shot: nothing hit.");
                return false;
            }

            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var hit in hits)
            {
                // Skip the shooter's own body — own arms/hands sit right in front of the camera.
                if (hit.transform.root == shooterRoot)
                    continue;

                var health = hit.transform.GetComponentInParent<PlayerHealth>();
                if (health != null)
                {
                    health.sync___set_value_health(health.sync___get_value_health() - damageToGive, true);
                    if (Plugin.VerboseLogging.Value)
                        Plugin.Log.LogInfo(
                            $"shot: hit '{hit.transform.name}' (layer {hit.transform.gameObject.layer}) -> " +
                            $"{health.gameObject.name} takes {damageToGive}, health now {health.sync___get_value_health()}.");
                    return false;
                }

                var doll = hit.transform.GetComponentInParent<DollHealth>();
                if (doll != null)
                {
                    doll.health -= damageToGive;
                    if (Plugin.VerboseLogging.Value)
                        Plugin.Log.LogInfo($"shot: hit training doll '{hit.transform.name}'.");
                    return false;
                }

                // First non-self thing hit is scenery — the shot is blocked, as in vanilla.
                if (Plugin.VerboseLogging.Value)
                    Plugin.Log.LogInfo(
                        $"shot: blocked by '{hit.transform.name}' (layer {hit.transform.gameObject.layer}).");
                return false;
            }

            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo("shot: only own colliders in the way.");
            return false;
        }
    }
}
