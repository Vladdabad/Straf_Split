using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Makes rockets, grenades, mines and shrapnel damage local players.
    ///
    /// <see cref="Weapon_ShootServer_Patch"/> covers the eight hitscan weapons, whose damage is
    /// resolved inside a `ShootServer*` call on the weapon itself. Nineteen weapons do not work
    /// that way: `DualLauncher` (RocketLauncher, HandGrenade, StunGrenade, Gamma, Kanye,
    /// Mortini, Nugget, Prophet, Bublee, HK_Caws, GlandGrenade, DF_Blister, DF_Cyst) and
    /// `WeaponHandSpawner` (APMine, Claymore, ProximityMine, StunMine) spawn an object, and the
    /// damage happens later, in that object's own class, on its own call stack.
    ///
    /// Every one of those classes resolves its victims the same way:
    ///
    /// <code>
    ///   Physics.OverlapSphere(transform.position, explosionRadius, this.bodyLayer)
    ///   // then: GetComponentInParent&lt;PlayerHealth&gt;() on what it found
    /// </code>
    ///
    /// `bodyLayer`/`playerLayer` admit layer 11 and not layer 16, and every local pawn sits on
    /// 16 because each of them runs `PlayerSetup`'s owner branch — see <see cref="DamageLayers"/>
    /// for why. So the overlap simply never returns another local player, and a rocket to the
    /// face did nothing at all. This was also true of rockets fired by REMOTE players: their
    /// explosions could only ever kill P1, because P1 is the only local pawn a remote client
    /// leaves on layer 11.
    ///
    /// The fix is the swap already used for guns, wrapped around each damage method. What
    /// differs is finding the shooter: there is no shooter on this call stack, only a projectile
    /// that went off. Every class stores the pawn that launched it in `_rootObject` — the same
    /// field they each already compare against to decide whether a kill counts as a suicide —
    /// so that is what decides who to leave alone.
    ///
    /// The launcher keeps layer 16 and stays unable to blow themselves up, exactly as the host
    /// does in vanilla. That asymmetry is the game's, not ours: a remote player's own pawn is on
    /// 11 on the host, so remote players can already suicide with their own grenades while the
    /// host cannot. Extending the mod's protection to every local player keeps splitscreen
    /// consistent with what the person hosting already experiences, and avoids inventing
    /// self-kills nobody asked for.
    ///
    /// Prefix and Finalizer rather than Prefix and Postfix, deliberately: a Postfix is skipped
    /// when the method throws, which would leave every other local player parked on layer 11 —
    /// visible to their own next shot. A Finalizer runs either way, so the swap is always undone.
    /// </summary>
    [HarmonyPatch]
    internal static class Projectile_Damage_Patch
    {
        /// <summary>
        /// The damage method of each spawned object, by type. Listed by hand because these are
        /// not a class hierarchy — they share no base type and no naming rule, only the shape of
        /// what they do. A name that stops resolving is logged rather than skipped silently.
        /// </summary>
        private static readonly string[][] Targets =
        {
            new[] { "PredictedProjectile", "HandleCollision" },
            new[] { "HandGrenade", "HandleCollision" },
            new[] { "HandGrenade", "HandleExplosion" },
            new[] { "HandGrenadeTwo", "HandleExplosion" },
            new[] { "Obus", "RpcLogic___HandleExplosion_4276783012" },
            new[] { "PhysicsGrenade", "RpcLogic___HandleExplosion_4276783012" },
            new[] { "ProximityMine", "HandleExplosion" },
            new[] { "ShrapnelBallistic", "Explode" },
            new[] { "BumpBullet", "HandleCollision" },
            new[] { "RebondBalle", "HandleCollision" },
            new[] { "Claymore", "HandleExplosion" },
        };

        private static readonly Dictionary<System.Type, FieldInfo> RootFields =
            new Dictionary<System.Type, FieldInfo>();

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var found = new List<MethodBase>();
            var names = new List<string>();
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
                names.Add(entry[0] + "." + entry[1]);
            }

            if (missing.Count > 0)
            {
                Plugin.Log.LogWarning(
                    $"[damage] {missing.Count} explosive damage method(s) not found — those weapons " +
                    $"still cannot hurt other local players: {string.Join(", ", missing.ToArray())}. " +
                    "The game's classes have changed since this was written.");
            }

            if (found.Count > 0)
            {
                Plugin.Log.LogInfo(
                    $"[damage] wrapping {found.Count} explosive/projectile damage method(s) with the " +
                    $"hit-layer swap: {string.Join(", ", names.ToArray())}.");
            }

            return found;
        }

        /// <summary>
        /// The pawn that launched this projectile, or null if it came from a remote player (in
        /// which case every local player is a target and none is excluded).
        /// </summary>
        private static GameObject RootOf(object instance)
        {
            if (instance == null)
                return null;

            var type = instance.GetType();
            FieldInfo field;
            if (!RootFields.TryGetValue(type, out field))
            {
                field = AccessTools.Field(type, "_rootObject");
                RootFields[type] = field;
                if (field == null)
                    Plugin.Log.LogWarning(
                        $"[damage] {type.Name} has no _rootObject field — its explosion will swap " +
                        "every local player, so the launcher can be caught in their own blast.");
            }

            return field != null ? field.GetValue(instance) as GameObject : null;
        }

        /// <summary>
        /// Far enough that no explosion in the game reaches it, close enough that a rocket in
        /// flight does not rewrite the hitbox layers of players on the other side of the map
        /// every frame. See <c>DamageLayers.BeginExcluding</c>.
        /// </summary>
        private const float RelevantRadius = 30f;

        private static void Prefix(object __instance)
        {
            var component = __instance as Component;
            if (component == null)
            {
                DamageLayers.BeginExcluding(RootOf(__instance));
                return;
            }

            DamageLayers.BeginExcluding(RootOf(__instance), component.transform.position, RelevantRadius);
        }

        private static void Finalizer()
        {
            DamageLayers.End();
        }
    }
}
