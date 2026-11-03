using System.Collections.Generic;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Makes local players damageable by each other.
    ///
    /// Your instinct was right — there IS a mechanism stopping you hurting yourself, and it is
    /// what breaks splitscreen. It is not a distance or direction test though; it is a layer.
    ///
    /// The hitbox skeleton is authored on layer 11 (`Body`) and every hitscan weapon gates its
    /// damage on exactly that:
    ///
    /// <code>
    ///   // Gun.ShootServer, and the same line in Shotgun, Minigun, LargeRaycastGun
    ///   if (Physics.Raycast(position, direction, out hit, inf, this.playerLayer)
    ///       &amp;&amp; hit.transform.gameObject.layer == 11
    ///       &amp;&amp; ...PlayerHealth found...)
    /// </code>
    ///
    /// and `PlayerSetup.OnStartClient`'s owner branch ends with:
    ///
    /// <code>
    ///   this.SetGameLayerRecursive(this.graphics, 16);   // 16 = "SelfBody"
    /// </code>
    ///
    /// `colliderParent` lives under `graphics`, so **the owner's own hitboxes move to layer
    /// 16** and can never satisfy `layer == 11`. That is the self-damage guard: in vanilla
    /// exactly one pawn is an owner, everyone else stays on 11, and it works perfectly.
    ///
    /// With several local players every pawn runs the owner branch, so every local pawn sits
    /// on 16 and **no local player can damage another local player**. Remote players are
    /// unaffected: on their machine our pawns are non-owners and stay on 11, which is why this
    /// only ever shows up between players sharing a screen. BeamGun, ChargeGun, RepulsiveGun
    /// and Taser have no `layer == 11` line but cast against the same `playerLayer` mask, so
    /// they fail the same way.
    ///
    /// The fix cannot be "put local pawns back on 11 permanently". The layer is doing real
    /// work: these are 81 non-trigger colliders, and on 11 the shooter's own hitboxes become
    /// raycast targets, so a shot taken while your own arm crosses the muzzle line would find
    /// you first, hit the `enemyHealth.gameObject == transform.root.gameObject` guard and be
    /// silently swallowed. It would also expose your own body colliders to your own character
    /// controller, and the shipped collision matrix is not readable from the ripped project.
    ///
    /// So the swap is momentary and scoped to one hit resolution: for the duration of a
    /// `ShootServer` call, every local player EXCEPT the shooter is moved to layer 11, then
    /// put straight back. The shooter keeps 16 and stays unable to block or hit themselves;
    /// everyone else reads as an ordinary target. No physics step runs inside a synchronous
    /// method call, so nothing else can observe the change.
    /// </summary>
    internal static class DamageLayers
    {
        /// <summary>"Body" — the layer every weapon's damage check demands.</summary>
        internal const int BodyLayer = 11;

        /// <summary>"SelfBody" — where the owner branch parks your own hitboxes.</summary>
        internal const int SelfBodyLayer = 16;

        private static readonly List<GameObject> _swapped = new List<GameObject>();
        private static int _depth;
        private static bool _announced;

        internal static void Begin(Component shooter)
        {
            LocalPlayer shooterPlayer = shooter != null && LocalPlayers.SplitActive
                ? LocalPlayers.OwningPawn(shooter.transform.root.gameObject)
                : null;
            BeginExcluding(shooterPlayer, null, 0f);
        }

        /// <summary>
        /// The same swap for damage that does not come from a weapon in someone's hands:
        /// rockets, grenades, mines, shrapnel. Their damage lands in the projectile's own class,
        /// long after the shot, so the shooter cannot be recovered from the call stack — but
        /// every one of them stores the pawn that launched it in `_rootObject`, which is the
        /// same thing.
        ///
        /// A null owner means the projectile came from a remote player. Then every local player
        /// is swapped, which is exactly right: all of them are legitimate targets, and it is the
        /// case that made remote rockets unable to kill anybody but P1.
        /// </summary>
        internal static void BeginExcluding(GameObject ownerPawn)
        {
            BeginExcluding(ownerPawn != null ? LocalPlayers.OwningPawn(ownerPawn) : null, null, 0f);
        }

        /// <summary>
        /// As above, but only for players within <paramref name="maxDistance"/> of
        /// <paramref name="origin"/>.
        ///
        /// A projectile checks for collisions every frame it is in the air, so an unconditional
        /// swap would rewrite every hitbox layer in the match, twice, every frame, for each
        /// rocket in flight. Nobody metres away can be hit by the blast in any case, so the
        /// distance test costs a subtraction per player and skips the work that matters.
        /// The cutoff is deliberately far larger than any explosion radius in the game.
        ///
        /// It never affects correctness of the restore: End puts back exactly what was swapped,
        /// and the depth counter is kept whether anything was swapped or not.
        /// </summary>
        internal static void BeginExcluding(GameObject ownerPawn, Vector3 origin, float maxDistance)
        {
            BeginExcluding(ownerPawn != null ? LocalPlayers.OwningPawn(ownerPawn) : null, origin, maxDistance);
        }

        private static void BeginExcluding(LocalPlayer shooterPlayer, Vector3? origin, float maxDistance)
        {
            if (!LocalPlayers.SplitActive)
                return;

            // Shotguns and burst weapons re-enter this per pellet; only the outermost call
            // swaps, and only it restores.
            if (_depth++ > 0)
                return;

            float maxSqr = maxDistance * maxDistance;

            foreach (var player in LocalPlayers.All)
            {
                if (shooterPlayer != null && player.Index == shooterPlayer.Index)
                    continue;

                if (origin.HasValue)
                {
                    var pawn = player.Pawn;
                    if (pawn == null)
                        continue;
                    if ((pawn.transform.position - origin.Value).sqrMagnitude > maxSqr)
                        continue;
                }

                var colliders = BodyColliders(player);
                if (colliders == null)
                    continue;

                for (int i = 0; i < colliders.Length; i++)
                {
                    var col = colliders[i];
                    if (col == null)
                        continue;
                    var go = col.gameObject;
                    if (go.layer != SelfBodyLayer)
                        continue;
                    go.layer = BodyLayer;
                    _swapped.Add(go);
                }
            }

            if (!_announced && _swapped.Count > 0)
            {
                _announced = true;
                Plugin.Log.LogInfo(
                    $"[damage] hit-layer swap engaged: {_swapped.Count} collider(s) moved " +
                    $"{SelfBodyLayer}->{BodyLayer} for one shot from " +
                    $"{(shooterPlayer != null ? shooterPlayer.Label : "a remote/untagged pawn")}.");
            }
        }

        internal static void End()
        {
            if (_depth == 0)
                return;
            if (--_depth > 0)
                return;

            for (int i = 0; i < _swapped.Count; i++)
            {
                if (_swapped[i] != null)
                    _swapped[i].layer = SelfBodyLayer;
            }
            _swapped.Clear();
        }

        /// <summary>
        /// A pawn's colliders, cached until the pawn is replaced. Rebuilt by pawn identity
        /// rather than on a timer: a shot must never resolve against the previous round's
        /// collider list, and pawns are the only thing that changes here.
        /// </summary>
        private static Collider[] BodyColliders(LocalPlayer player)
        {
            var pawn = player.Pawn;
            if (pawn == null)
            {
                player.BodyColliders = null;
                player.BodyCollidersPawnId = 0;
                return null;
            }

            int id = pawn.GetInstanceID();
            if (player.BodyColliders == null || player.BodyCollidersPawnId != id)
            {
                player.BodyColliders = pawn.GetComponentsInChildren<Collider>(true);
                player.BodyCollidersPawnId = id;
            }
            return player.BodyColliders;
        }

        /// <summary>"Melee" — the layer MeleeChildCollision forces onto its blade every frame.</summary>
        internal const int MeleeLayer = 21;

        /// <summary>"Self" — where the owner branch puts the pawn root, and with it the CharacterController.</summary>
        internal const int SelfLayer = 6;

        private static bool _meleeApplied;
        private static bool _originalMeleeVsSelf;
        private static bool _originalMeleeVsSelfBody;

        /// <summary>
        /// Let melee reach other local players.
        ///
        /// Melee never raycasts. `MeleeChildCollision.OnCollisionEnter` resolves a real physics
        /// contact and identifies the victim with `GetComponentInParent&lt;PlayerHealth&gt;()`,
        /// with no layer test at all — so unlike the hitscan weapons it is governed entirely by
        /// the collision matrix, and a swap scoped to a method call cannot help something
        /// resolved during the physics step.
        ///
        /// It is the same underlying bug though. `PlayerSetup.OnStartClient`'s owner branch
        /// moves two things out of the way of the player's own weapons:
        ///
        /// <code>
        ///   base.gameObject.layer = 6;                     // pawn root, and the CharacterController on it
        ///   this.SetGameLayerRecursive(this.graphics, 16);  // the bone hitboxes
        /// </code>
        ///
        /// A remote player keeps the prefab's layer 3 (`Player`) on the root and 11 (`Body`) on
        /// the hitboxes, and the blade on layer 21 collides with those. Every local pawn is an
        /// owner, so all of them sit on 6 and 16, which layer 21 does not collide with — no
        /// contact, no `OnCollisionEnter`, no damage.
        ///
        /// So the matrix itself is opened up for the duration of splitscreen: 21 is allowed to
        /// touch 6 and 16, and the original values are restored when the roster drops back to
        /// one. Self-hits are already impossible twice over —
        /// `MeleeChildCollision.OnCollisionEnter` returns early when
        /// `collision.transform.root == behaviour.rootObject.transform`, before it even
        /// increments its hit counter, and `MeleeWeapon.HitServer` checks again. The blade is a
        /// kinematic Rigidbody and a CharacterController only moves under script control, so
        /// the extra contacts carry no force either way; what changes is that the callback now
        /// fires.
        ///
        /// Both layers matter: 6 is the capsule, which is what a body blow lands on, and 16 is
        /// the bone hitboxes, which is where `Head_Col` comes from — without it local players
        /// could be hit but never headshot.
        /// </summary>
        /// <summary>Projectile layers we have opened against the local-player layers, and what they were.</summary>
        private static readonly Dictionary<int, bool[]> _projectileLayers = new Dictionary<int, bool[]>();

        /// <summary>
        /// Let a projectile collide with local players at all.
        ///
        /// Some projectiles do not resolve damage with an overlap query — they wait for
        /// `OnCollisionEnter` and read the layer of what they touched. `Bubble` and
        /// `HandGrenadeTwo` both accept layer 11 OR layer 16, so the layer test is not what
        /// stops them: the collision never happens, because the shipped collision matrix ignores
        /// their layer against 6 and 16, the two layers every local pawn lives on. In vanilla
        /// that costs nothing — the only pawn on those layers is your own, and a projectile
        /// passing through yourself is intended. With several local players it means the
        /// projectile passes through everybody.
        ///
        /// Discovered at runtime rather than listed: the projectile prefabs' layers are not in
        /// the ripped project, and a spawned instance knows its own. Idempotent per layer, so
        /// calling it on every spawn costs a dictionary lookup.
        /// </summary>
        internal static void EnsureProjectileCollisions(int layer)
        {
            if (!LocalPlayers.SplitActive || _projectileLayers.ContainsKey(layer))
                return;

            var original = new bool[]
            {
                Physics.GetIgnoreLayerCollision(layer, SelfLayer),
                Physics.GetIgnoreLayerCollision(layer, SelfBodyLayer),
            };
            _projectileLayers[layer] = original;

            if (!original[0] && !original[1])
                return; // already colliding with both; nothing to open and nothing to restore

            Physics.IgnoreLayerCollision(layer, SelfLayer, false);
            Physics.IgnoreLayerCollision(layer, SelfBodyLayer, false);
            Plugin.Log.LogInfo(
                $"[damage] projectile layer {layer} enabled against local players: vs {SelfLayer} " +
                $"(was {(original[0] ? "ignored" : "colliding")}) and vs {SelfBodyLayer} " +
                $"(was {(original[1] ? "ignored" : "colliding")}).");
        }

        /// <summary>
        /// Make one projectile pass through the player who launched it.
        ///
        /// Opening the projectile's collision row (see above) has a consequence vanilla never
        /// had to think about: layer 16 is the LAUNCHER's own hitboxes, and the projectile is
        /// born at their muzzle, inside them. With the row closed the game got self-immunity for
        /// free; with it open, `Bubble` touches its owner on the first physics step, reads layer
        /// 16, and kills them instantly.
        ///
        /// So immunity has to be restored explicitly, and narrowly: per collider pair, for this
        /// projectile and this launcher only. Every other local player keeps colliding with it,
        /// which is the entire point of opening the row. `Physics.IgnoreCollision` is scoped to
        /// the two colliders and dies with them, so nothing has to be undone afterwards.
        /// </summary>
        internal static void IgnoreLauncherCollisions(Component projectile, GameObject launcherPawn)
        {
            if (projectile == null || launcherPawn == null)
                return;

            var projectileColliders = projectile.GetComponentsInChildren<Collider>(true);
            if (projectileColliders.Length == 0)
                return;

            var launcherColliders = launcherPawn.GetComponentsInChildren<Collider>(true);
            int pairs = 0;

            foreach (var pc in projectileColliders)
            {
                if (pc == null)
                    continue;
                foreach (var lc in launcherColliders)
                {
                    if (lc == null)
                        continue;
                    Physics.IgnoreCollision(pc, lc, true);
                    pairs++;
                }
            }

            if (!_launcherIgnoreAnnounced && pairs > 0)
            {
                _launcherIgnoreAnnounced = true;
                var owner = LocalPlayers.OwningPawn(launcherPawn);
                Plugin.Log.LogInfo(
                    $"[damage] '{projectile.gameObject.name}' passes through its launcher " +
                    $"({(owner != null ? owner.Label : "remote")}): {pairs} collider pair(s) ignored.");
            }
        }

        private static bool _launcherIgnoreAnnounced;

        private static void RestoreProjectileCollisions()
        {
            foreach (var pair in _projectileLayers)
            {
                Physics.IgnoreLayerCollision(pair.Key, SelfLayer, pair.Value[0]);
                Physics.IgnoreLayerCollision(pair.Key, SelfBodyLayer, pair.Value[1]);
            }
            if (_projectileLayers.Count > 0)
                Plugin.Log.LogInfo($"[damage] {_projectileLayers.Count} projectile collision row(s) restored to vanilla.");
            _projectileLayers.Clear();
        }

        internal static void EnsureMeleeCollisions()
        {
            bool want = LocalPlayers.SplitActive;
            if (want == _meleeApplied)
                return;

            if (want)
            {
                _originalMeleeVsSelf = Physics.GetIgnoreLayerCollision(MeleeLayer, SelfLayer);
                _originalMeleeVsSelfBody = Physics.GetIgnoreLayerCollision(MeleeLayer, SelfBodyLayer);
                Physics.IgnoreLayerCollision(MeleeLayer, SelfLayer, false);
                Physics.IgnoreLayerCollision(MeleeLayer, SelfBodyLayer, false);
                Plugin.Log.LogInfo(
                    $"[damage] melee enabled against local players: layer {MeleeLayer} vs {SelfLayer} " +
                    $"(was {(_originalMeleeVsSelf ? "ignored" : "colliding")}) and vs {SelfBodyLayer} " +
                    $"(was {(_originalMeleeVsSelfBody ? "ignored" : "colliding")}).");
            }
            else
            {
                Physics.IgnoreLayerCollision(MeleeLayer, SelfLayer, _originalMeleeVsSelf);
                Physics.IgnoreLayerCollision(MeleeLayer, SelfBodyLayer, _originalMeleeVsSelfBody);
                Plugin.Log.LogInfo("[damage] melee collision matrix restored to vanilla.");

                // Same lifetime, same reason: opened only while a split is running.
                RestoreProjectileCollisions();
            }

            _meleeApplied = want;
        }

        /// <summary>
        /// Report the collision matrix rows that decide whether melee can land, so the shipped
        /// matrix can be read off the running game — AssetRipper did not recover it.
        /// </summary>
        internal static void LogCollisionMatrix()
        {
            int[] interesting = { 3, 6, 11, 16, 21, 23 };
            var sb = new System.Text.StringBuilder();
            foreach (int a in interesting)
            {
                sb.Append($"\n    layer {a} ({LayerMask.LayerToName(a)}): ");
                foreach (int b in interesting)
                    sb.Append($"{b}={(Physics.GetIgnoreLayerCollision(a, b) ? "ignore" : "collide")} ");
            }
            Plugin.Log.LogInfo($"[damage] collision matrix rows that matter:{sb}");
        }
    }
}
