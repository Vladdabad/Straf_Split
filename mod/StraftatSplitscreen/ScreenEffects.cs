using System.Collections.Generic;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Per-player full-screen camera effects: the red damage flash and the suppression blur.
    ///
    /// The component is already per-pawn. `CameraEffect` sits on that pawn's own camera and
    /// blits in `OnRenderImage`, so the wiring looked right:
    ///
    ///     PlayerHealth.HitFeedbackObservers (on the pawn that was hit)
    ///         -> HitFeeback() -> if (IsOwner) camHit.TakeHit()
    ///     Suppression.SuppressionTrigger   (a near miss past that pawn)
    ///         -> if (IsOwner) camPP.TrigSup()
    ///
    /// What is shared is the **material**. `CameraEffect.material` is a serialized reference to
    /// one Material asset in the project, so every pawn's effect points at the same object, and
    /// the effect's whole state lives in that object's `_Damage` / `_Suppression` floats —
    /// `TakeHit` writes them and `OnRenderImage` reads, decays and re-writes them. One pawn gets
    /// shot; every local camera blits the same red.
    ///
    /// So each local player gets their own instance of the material. Nothing else changes: the
    /// game's own trigger paths already name the right pawn, and each one now writes state only
    /// its owner's camera reads.
    ///
    /// Player 1 is not special — they get an instance too. It costs one material and it means
    /// nothing at runtime writes to the shipped asset at all.
    ///
    /// Two effects that were checked and need nothing:
    ///
    /// - **The taser stun.** `PlayerSetup.StunMatObservers` toggles `VFX_TaserStunned_00`, a
    ///   particle system on the stunned pawn, and sets `Float2` through `Renderer.materials`,
    ///   which hands back per-renderer instances. Already per-player, and correctly visible to
    ///   everyone — being able to see who is stunned is the point of it.
    /// - **Screen shake.** `PlayerHealth.LocalScreenshake` tweens that pawn's own camera
    ///   transform.
    /// </summary>
    internal static class ScreenEffects
    {
        private static readonly int DamageId = Shader.PropertyToID("_Damage");
        private static readonly int SuppressionId = Shader.PropertyToID("_Suppression");

        private sealed class Owned
        {
            /// <summary>The asset the game shipped. Never written, never destroyed — it is a project asset.</summary>
            internal Material Shared;

            /// <summary>The copy this player's camera reads and writes.</summary>
            internal Material Instance;
        }

        private static readonly Dictionary<CameraEffect, Owned> _owned = new Dictionary<CameraEffect, Owned>();

        /// <summary>
        /// Give every local pawn's camera effect its own material. Idempotent and cheap — a
        /// pawn that already holds its own instance is skipped — so it can ride the refresh
        /// tick, which is what picks up each round's new pawns.
        /// </summary>
        internal static void Apply()
        {
            if (!LocalPlayers.SplitActive)
            {
                RestoreAll();
                return;
            }

            foreach (var player in LocalPlayers.All)
            {
                var setup = player.Setup;
                if (setup == null)
                    continue;
                foreach (var fx in setup.GetComponentsInChildren<CameraEffect>(true))
                    Isolate(fx, player);
            }

            Prune();
        }

        private static void Isolate(CameraEffect fx, LocalPlayer player)
        {
            if (fx == null || fx.material == null)
                return;

            Owned owned;
            bool known = _owned.TryGetValue(fx, out owned);
            if (known && ReferenceEquals(fx.material, owned.Instance))
                return; // already ours

            // Keep the first material we ever saw on this component as the one to hand back:
            // whatever it holds now may already be one of ours from an earlier pass.
            Material shared = known ? owned.Shared : fx.material;
            if (known && owned.Instance != null)
                Object.Destroy(owned.Instance);

            var instance = new Material(shared) { name = shared.name + " (" + player.Label + ")" };
            instance.SetFloat(DamageId, 0f);
            instance.SetFloat(SuppressionId, 0f);

            fx.material = instance;
            _owned[fx] = new Owned { Shared = shared, Instance = instance };

            Plugin.Log.LogInfo(
                $"[fx] {player.Label} now has their own '{shared.name}' — damage and suppression " +
                "stay on their own screen.");
        }

        /// <summary>Drop instances whose component died with last round's pawn.</summary>
        private static void Prune()
        {
            List<CameraEffect> stale = null;
            foreach (var kv in _owned)
            {
                if (kv.Key != null)
                    continue;
                (stale ?? (stale = new List<CameraEffect>())).Add(kv.Key);
                if (kv.Value.Instance != null)
                    Object.Destroy(kv.Value.Instance);
            }
            if (stale == null)
                return;
            foreach (var key in stale)
                _owned.Remove(key);
        }

        /// <summary>Back to one local player: hand every component its shipped material back.</summary>
        private static void RestoreAll()
        {
            if (_owned.Count == 0)
                return;

            foreach (var kv in _owned)
            {
                var fx = kv.Key;
                if (fx != null && ReferenceEquals(fx.material, kv.Value.Instance))
                    fx.material = kv.Value.Shared;
                if (kv.Value.Instance != null)
                    Object.Destroy(kv.Value.Instance);
            }
            _owned.Clear();
            Plugin.Log.LogInfo("[fx] one local player again — camera effects are back on the shared material.");
        }
    }
}
