using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Melee hit detection that does not depend on a physics contact happening.
    ///
    /// Melee resolves through `MeleeChildCollision.OnCollisionEnter`, and opening the
    /// collision matrix (<see cref="DamageLayers.EnsureMeleeCollisions"/>) is only enough if a
    /// contact can be generated at all. There is good reason to doubt it: the blade carries a
    /// **kinematic** Rigidbody and the pawn has **no Rigidbody whatsoever** — 81 static
    /// colliders plus a CharacterController — and Unity sends no collision events between a
    /// kinematic body and a static collider. How vanilla lands melee on a remote player under
    /// those rules is not something the ripped project can answer: it has already been caught
    /// losing the collision matrix and the crosshair's serialized RectTransform, so its
    /// `isKinematic` flag is not evidence either.
    ///
    /// Rather than keep guessing at physics semantics, this adds an overlap test that reaches
    /// the same conclusion by a route with no such preconditions. While a swing is live, the
    /// blade's bounds are tested against the layers local players actually occupy (6 `Self`
    /// for the capsule, 16 `SelfBody` for the bone colliders), and any local player found
    /// there is passed to the game's own `MeleeWeapon.HitServer` — the same call, with the
    /// same arguments, that the collision path would have made. Damage, knockback, hit markers
    /// and headshot detection all stay the game's.
    ///
    /// Only OTHER LOCAL players are considered. Remote players are reached by the vanilla
    /// contact path, which works, and must not be hit twice.
    ///
    /// Double-hits are prevented from both directions. Each swing keeps a set of victims
    /// already struck: the overlap skips anyone in it, and a prefix on `OnCollisionEnter`
    /// skips a contact whose victim is in it too — so whichever path gets there first wins and
    /// the other stands down. The set is cleared when a new swing begins.
    /// </summary>
    internal static class MeleeSplit
    {
        /// <summary>Victims already struck this swing, per blade.</summary>
        private static readonly Dictionary<int, HashSet<int>> _struck = new Dictionary<int, HashSet<int>>();

        /// <summary>Whether each blade was mid-swing last frame, so a new swing can be detected.</summary>
        private static readonly Dictionary<int, bool> _wasSwinging = new Dictionary<int, bool>();

        /// <summary>Hits landed this swing, mirroring the vanilla hitsCounter.</summary>
        private static readonly Dictionary<int, int> _hits = new Dictionary<int, int>();

        private static readonly Collider[] _overlap = new Collider[32];

        private static bool _announced;

        private static int LocalPlayerMask
        {
            get { return (1 << DamageLayers.SelfLayer) | (1 << DamageLayers.SelfBodyLayer); }
        }

        private static HashSet<int> StruckSet(int bladeId)
        {
            HashSet<int> set;
            if (!_struck.TryGetValue(bladeId, out set))
            {
                set = new HashSet<int>();
                _struck[bladeId] = set;
            }
            return set;
        }

        /// <summary>True if this victim has already been struck by this blade during this swing.</summary>
        internal static bool AlreadyStruck(MeleeChildCollision blade, PlayerHealth victim)
        {
            if (blade == null || victim == null)
                return false;
            return StruckSet(blade.GetInstanceID()).Contains(victim.GetInstanceID());
        }

        internal static void RecordStrike(MeleeChildCollision blade, PlayerHealth victim, string via)
        {
            if (blade == null || victim == null)
                return;
            if (StruckSet(blade.GetInstanceID()).Add(victim.GetInstanceID()) && Plugin.VerboseLogging.Value)
            {
                var player = LocalPlayers.OwningPawn(victim.gameObject);
                Plugin.Log.LogInfo(
                    $"[melee] hit on {(player != null ? player.Label : "remote")} via {via}.");
            }
        }

        /// <summary>
        /// Runs every frame off MeleeChildCollision.Update, which the game already ticks to pin
        /// the blade to its layer.
        /// </summary>
        internal static void Tick(MeleeChildCollision blade)
        {
            if (!LocalPlayers.SplitActive || !Plugin.MeleeOverlapFallback.Value || blade == null)
                return;

            int bladeId = blade.GetInstanceID();
            bool swinging = blade.canHit;

            bool wasSwinging;
            _wasSwinging.TryGetValue(bladeId, out wasSwinging);
            _wasSwinging[bladeId] = swinging;

            if (!swinging)
                return;

            // New swing: forget who was struck by the previous one.
            if (!wasSwinging)
            {
                StruckSet(bladeId).Clear();
                _hits[bladeId] = 0;
            }

            var weapon = WeaponOf(blade);
            var behaviour = BehaviourOf(blade);
            if (weapon == null || behaviour == null)
                return;

            // A weapon lying on the ground is layer 7; vanilla ignores those contacts too.
            if (weapon.gameObject.layer == 7)
                return;

            var holderRoot = behaviour.rootObject != null ? behaviour.rootObject.transform : null;
            var bladeCollider = blade.GetComponent<Collider>();
            if (bladeCollider == null)
                return;

            var bounds = bladeCollider.bounds;
            int count = Physics.OverlapBoxNonAlloc(
                bounds.center, bounds.extents, _overlap, Quaternion.identity, LocalPlayerMask,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                var other = _overlap[i];
                if (other == null)
                    continue;

                // Never the wielder — the same test vanilla makes, before anything else.
                if (holderRoot != null && other.transform.root == holderRoot)
                    continue;

                var victim = other.GetComponentInParent<PlayerHealth>();
                if (victim == null)
                    continue;

                // Remote players already work through the contact path; hitting them here too
                // would double their damage.
                if (LocalPlayers.OwningPawn(victim.gameObject) == null)
                    continue;

                if (AlreadyStruck(blade, victim))
                    continue;

                Vector3 point = other.ClosestPoint(bounds.center);
                Vector3 normal = (bounds.center - point).normalized;
                if (normal == Vector3.zero)
                    normal = Vector3.up;

                RecordStrike(blade, victim, "overlap");
                weapon.HitServer(victim, point, normal, other.gameObject.name);

                if (!_announced)
                {
                    _announced = true;
                    Plugin.Log.LogInfo(
                        "[melee] overlap fallback landed a hit — the vanilla contact path is not " +
                        "reaching local players, so this is what makes melee work here.");
                }

                int hits;
                _hits.TryGetValue(bladeId, out hits);
                hits++;
                _hits[bladeId] = hits;
                if (hits >= weapon.hitsAmount)
                {
                    blade.canHit = false;
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ reflection

        private static readonly Dictionary<string, System.Reflection.FieldInfo> _fields =
            new Dictionary<string, System.Reflection.FieldInfo>();

        private static object Field(object obj, string name)
        {
            if (obj == null)
                return null;
            var type = obj.GetType();
            string key = type.FullName + "." + name;
            System.Reflection.FieldInfo f;
            if (!_fields.TryGetValue(key, out f))
            {
                f = AccessTools.Field(type, name);
                _fields[key] = f;
            }
            return f == null ? null : f.GetValue(obj);
        }

        internal static MeleeWeapon WeaponOf(MeleeChildCollision blade)
        {
            return Field(blade, "weaponScript") as MeleeWeapon;
        }

        internal static ItemBehaviour BehaviourOf(MeleeChildCollision blade)
        {
            return Field(blade, "behaviour") as ItemBehaviour;
        }
    }
}
