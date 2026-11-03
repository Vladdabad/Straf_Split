using System.Collections.Generic;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Audio for several local players, within the one constraint that cannot be worked
    /// around: Unity mixes for exactly one AudioListener. Two enabled listeners is not "two
    /// players hearing" — it is an error the engine warns about and then resolves arbitrarily.
    /// One stereo output cannot carry N independent spatializations, so this is not a matter of
    /// finding the right API.
    ///
    /// What CAN be fixed is the part that actually goes wrong. Every one of the nine
    /// AudioSources on a pawn ships with `spatialBlend = 1` (fully 3D, MinDistance 3.54,
    /// MaxDistance 60-500) and the listener lives on the pawn's own `Camera`. For the player
    /// holding the listener that is correct: their footsteps, landings and the suppression
    /// whip-cracks of near-misses all originate at their own head, well inside MinDistance, so
    /// they play at full volume. For every other local player those same sounds are emitted
    /// metres away from the one listener and attenuate to near silence — they cannot hear
    /// themselves move, land, or get shot at.
    ///
    /// The game already has a rule for this, and applies it to weapons:
    ///
    ///     Weapon.WeaponUpdate():  IsOwner ? spatialBlend = 0 : spatialBlend = 1
    ///
    /// Your own weapon is 2D so you always hear it at full volume regardless of where the
    /// listener is. Splitscreen simply means there are several owners. So the same rule is
    /// extended to the rest of the pawn: **every local player who is not holding the listener
    /// gets their own pawn's sounds in 2D**, and hears themselves properly.
    ///
    /// The listener holder keeps vanilla 3D throughout, so nothing changes for them — they
    /// keep directional footsteps and directional near-misses exactly as in single player.
    /// Which player that is comes from `[Audio] ListenerPlayer` rather than being pinned to
    /// player 1.
    ///
    /// The honest limitation: with one output, "everyone hears themselves" also means everyone
    /// hears everyone else's own-sounds at full volume without direction. World audio — doors,
    /// props, remote players' weapons — is still heard from the listener holder's position.
    /// That is the ceiling of one AudioListener, not a shortcut taken here.
    /// </summary>
    internal static class AudioSplit
    {
        /// <summary>Original spatialBlend per source, so the listener holder can be handed back untouched.</summary>
        private static readonly Dictionary<int, float> _originalBlend = new Dictionary<int, float>();

        /// <summary>Roster index of the player whose ears the world is heard from.</summary>
        internal static int ListenerIndex
        {
            get
            {
                int index = Plugin.ListenerPlayer.Value - 1;
                if (index < 0)
                    return 0;
                return index >= LocalPlayers.Count ? 0 : index;
            }
        }

        /// <summary>
        /// Re-apply spatial blends across the roster. Cheap and idempotent; runs on the same
        /// refresh cadence as the renderer sets, so a pawn that respawned mid-round is picked
        /// up without a per-frame sweep.
        /// </summary>
        internal static void Apply()
        {
            if (!LocalPlayers.SplitActive)
            {
                RestoreAll();
                return;
            }

            int listener = ListenerIndex;

            foreach (var player in LocalPlayers.All)
            {
                var pawn = player.Pawn;
                if (pawn == null)
                    continue;

                bool holdsListener = player.Index == listener;

                foreach (var source in pawn.GetComponentsInChildren<AudioSource>(true))
                {
                    if (source == null)
                        continue;

                    int key = source.GetInstanceID();
                    if (!_originalBlend.ContainsKey(key))
                        _originalBlend[key] = source.spatialBlend;

                    float wanted = holdsListener || !Plugin.HearOwnSounds.Value
                        ? _originalBlend[key]
                        : 0f;

                    if (!Mathf.Approximately(source.spatialBlend, wanted))
                        source.spatialBlend = wanted;
                }
            }
        }

        private static void RestoreAll()
        {
            if (_originalBlend.Count == 0)
                return;
            foreach (var player in LocalPlayers.All)
            {
                var pawn = player.Pawn;
                if (pawn == null)
                    continue;
                foreach (var source in pawn.GetComponentsInChildren<AudioSource>(true))
                {
                    float original;
                    if (source != null && _originalBlend.TryGetValue(source.GetInstanceID(), out original))
                        source.spatialBlend = original;
                }
            }
            _originalBlend.Clear();
        }

        /// <summary>
        /// Keep exactly one AudioListener enabled, and make it the right one.
        ///
        /// Several things in the game switch listeners on without knowing about each other:
        /// `PlayerHealth` enables the kill cam's listener the moment a pawn dies, and
        /// `PlayerSetup.OnDisable` re-enables the scene camera's. With one local player those
        /// fire once and hand over cleanly; with four, somebody is dying most of the time, and
        /// every one of those events would add a second listener and make Unity pick between
        /// them arbitrarily.
        ///
        /// So: while the listener holder has a pawn, theirs is the only one enabled and
        /// everything else is switched off. If they have no pawn, vanilla's kill-cam handover
        /// is left alone — but the other players' listeners are still suppressed, because a
        /// dead player's kill cam has no business stealing the mix from a living one.
        /// </summary>
        internal static void EnforceSingleListener()
        {
            if (!LocalPlayers.SplitActive)
                return;

            var holder = LocalPlayers.At(ListenerIndex);
            AudioListener wanted = null;
            if (holder != null)
            {
                var setup = holder.Setup;
                if (setup != null)
                {
                    foreach (var listener in setup.GetComponentsInChildren<AudioListener>(true))
                    {
                        wanted = listener;
                        break;
                    }
                }
            }

            foreach (var listener in Object.FindObjectsOfType<AudioListener>())
            {
                if (listener == null)
                    continue;

                if (wanted != null)
                {
                    bool isWanted = ReferenceEquals(listener, wanted);
                    if (listener.enabled != isWanted)
                        listener.enabled = isWanted;
                    continue;
                }

                // No pawn for the listener holder: leave the scene/kill cam to vanilla, but
                // never let another local player's pawn listener take over.
                if (LocalPlayers.OwningPawn(listener.transform.root.gameObject) != null && listener.enabled)
                    listener.enabled = false;
            }
        }
    }
}
