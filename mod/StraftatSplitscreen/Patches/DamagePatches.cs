using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Wraps every hitscan weapon's server-side hit resolution with the damage-layer swap.
    ///
    /// They funnel through a method whose name STARTS WITH `ShootServer` — Gun, Shotgun,
    /// Minigun, LargeRaycastGun, BeamGun, ChargeGun, RepulsiveGun, Taser — so the targets are
    /// discovered by reflection over every `Weapon` subclass rather than listed by hand. A
    /// weapon added by a game update is covered automatically, and a renamed one shows up as a
    /// count change in the log line below instead of failing silently.
    ///
    /// The prefix match is what catches `LargeRaycastGun.ShootServerBox`, and it is not a
    /// nicety: `LargeRaycastGun.Shoot` forks on a per-prefab `boxcast` flag, and the boxcast
    /// branch returns before `ShootServer` is ever reached. Bayshore is authored with
    /// `boxcast: 1`, so matching the exact name left that whole weapon unable to damage another
    /// local player while its siblings worked — the kind of gap a hand-written list hides and a
    /// prefix does not.
    ///
    /// Any name containing `Effect` is excluded. They are the cosmetic RPCs
    /// (`ShootServerEffect`, `ShootServerEffect2`), carry no hit resolution, and swapping 144
    /// colliders around them would be motion without meaning. Matching on `Contains` rather
    /// than `EndsWith` is deliberate — `BeamGun.ShootServerEffect2` ends in a digit and slipped
    /// through the first version of this filter.
    ///
    /// `PlayerShoot` is deliberately excluded. Its `ShootServer` is only the FishNet writer
    /// stub — the real work happens later in `RpcLogic___ShootServer_1867452766`, on a
    /// different call stack, where a swap started here would already have been undone. That
    /// path has its own patch (<see cref="PlayerShoot_ShootServer_Patch"/>) which resolves hits
    /// without depending on layers at all.
    /// </summary>
    [HarmonyPatch]
    internal static class Weapon_ShootServer_Patch
    {
        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var found = new List<MethodBase>();
            var names = new List<string>();

            System.Type[] types;
            try
            {
                types = typeof(Weapon).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // A partially loadable assembly still yields the types that did load.
                types = ex.Types ?? new System.Type[0];
            }

            foreach (var type in types)
            {
                if (type == null || !typeof(Weapon).IsAssignableFrom(type))
                    continue;

                foreach (var method in AccessTools.GetDeclaredMethods(type))
                {
                    if (method == null || !method.Name.StartsWith("ShootServer"))
                        continue;
                    if (method.Name.Contains("Effect"))
                        continue;
                    found.Add(method);
                    names.Add(type.Name + "." + method.Name);
                }
            }

            if (found.Count == 0)
            {
                Plugin.Log.LogError(
                    "[damage] found no weapon ShootServer methods to wrap — local players will not " +
                    "be able to damage each other. The game's weapon classes have changed shape.");
            }
            else
            {
                Plugin.Log.LogInfo(
                    $"[damage] wrapping {found.Count} weapon hit-resolution method(s) with the hit-layer " +
                    $"swap: {string.Join(", ", names.ToArray())}.");
            }
            return found;
        }

        private static void Prefix(object __instance)
        {
            DamageLayers.Begin(__instance as UnityEngine.Component);
        }

        /// <summary>
        /// Finalizer, not Postfix: a Postfix is skipped when the wrapped method throws, and then
        /// the swap is never undone — every other local player is left on layer 11, hittable by
        /// their own next shot, for the rest of the round.
        ///
        /// That is not hypothetical. `Gun.BurstFire` reads `cam.transform` and `playerController`
        /// across a `yield return new WaitForSeconds(...)`, so a holder who dies or drops the
        /// weapon mid-burst makes the next iteration throw — observed in the wild as
        /// `NullReferenceException at Gun+&lt;BurstFire&gt;d__29.MoveNext`. A Finalizer runs whether
        /// the method returned or threw, so the layers always go back.
        /// </summary>
        private static void Finalizer()
        {
            DamageLayers.End();
        }
    }
}
