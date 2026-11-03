using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Opens the collision matrix for projectiles that damage on contact.
    ///
    /// <see cref="Projectile_Damage_Patch"/> handles the explosives that ask
    /// `Physics.OverlapSphere` who is nearby. A second family never asks: `Bubble` (the Bublee's
    /// projectile) and `HandGrenadeTwo` wait for `OnCollisionEnter` and read the layer of
    /// whatever they touched. Both accept layer 11 OR layer 16 — so unlike every other case in
    /// this mod, the layer test is not what rejects a local player. The collision itself never
    /// happens, because the shipped matrix ignores the projectile's layer against 6 and 16.
    ///
    /// That is invisible in vanilla: the only pawn on those layers is your own, and a projectile
    /// passing through yourself is the intent. With several local players it means the thing
    /// passes through everyone, which is exactly the Bublee doing no damage.
    ///
    /// A layer swap cannot fix this one. The swap is scoped to the inside of a damage call, and
    /// a contact projectile has no such call — the physics step decides, asynchronously, whether
    /// there is anything to report. The matrix row has to be open before the two ever touch.
    ///
    /// So the row is opened when a projectile spawns, keyed on the layer that instance is
    /// actually on: those prefabs' layers are not in the ripped project, and asking the object
    /// is both simpler and correct if the game changes them. Rows are restored with the melee
    /// rows when the split ends, so a single-player session is left exactly as the game shipped.
    /// </summary>
    [HarmonyPatch]
    internal static class Projectile_Collision_Patch
    {
        /// <summary>
        /// Contact-damage projectiles, hooked at `Initialize` — not `Start` or `Awake`.
        ///
        /// Initialize is where the launcher is handed over, and the launcher is needed in the
        /// same breath as opening the collision row: the row is what lets the projectile touch
        /// local players, and the launcher exemption is what stops it touching the person who
        /// fired it. Doing one without the other kills the shooter on the first physics step.
        /// Both signatures name the parameter `rootObject`, so Harmony injects it by name into a
        /// single patch despite the argument lists differing.
        /// </summary>
        private static readonly string[][] Targets =
        {
            new[] { "Bubble", "Initialize" },
            new[] { "HandGrenadeTwo", "Initialize" },
        };

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var found = new List<MethodBase>();
            var missing = new List<string>();

            foreach (var entry in Targets)
            {
                var type = AccessTools.TypeByName(entry[0]);
                var method = type != null ? AccessTools.DeclaredMethod(type, entry[1]) : null;
                if (method == null)
                {
                    missing.Add(entry[0] + "." + entry[1]);
                    continue;
                }
                found.Add(method);
            }

            if (missing.Count > 0)
            {
                Plugin.Log.LogWarning(
                    $"[damage] contact-projectile hook(s) not found: {string.Join(", ", missing.ToArray())}. " +
                    "Those weapons will pass straight through local players.");
            }

            return found;
        }

        private static void Postfix(object __instance, GameObject rootObject)
        {
            var component = __instance as Component;
            if (component == null)
                return;

            DamageLayers.EnsureProjectileCollisions(component.gameObject.layer);
            DamageLayers.IgnoreLauncherCollisions(component, rootObject);
        }
    }
}
