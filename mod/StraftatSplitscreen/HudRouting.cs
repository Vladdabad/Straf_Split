using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Per-player HUD values.
    ///
    /// The heads-up display is already per-pawn: every `PlayerIK` carries its own diegetic HUD
    /// rig under `Camera/UICam` — cable meshes, both bullet counters
    /// (`SM_HUD_00/SM_BulletCounter_{L,R}_00/.../ammolefttext`), the heart and its health
    /// number (`SM_Heart_00/hEALTHtext`), score and match points — all on layer 25, drawn by
    /// that pawn's own UICam. So nothing has to be built; the values just have to stop being
    /// shared.
    ///
    /// Health already works per player: `PlayerHealth.tweenScript` is wired in the prefab to
    /// that pawn's own `HealthTween`, which renders its own `health` field. It only ever
    /// looked like player 1's HUD because the mod was switching everyone else's UICam off.
    ///
    /// Ammo is the one genuinely shared value, and it travels a bus rather than a reference:
    ///
    ///     Weapon.WeaponUpdate()  ->  PauseManager.ChangeAmmoText(text, reload, right)
    ///                            ->  writes PauseManager.Instance.{left,right}GunAmmo.text
    ///     HUD.Update()           ->  copies that text onto its own TMP every frame
    ///
    /// One global pair of strings, written every frame by every owned weapon and read every
    /// frame by every pawn's counters: last writer wins, so all players show one player's
    /// ammo. This class gives each player their own pair of that bus, keyed by roster index.
    /// </summary>
    internal static class HudRouting
    {
        /// <summary>
        /// The player whose weapon is currently pushing a value. Set around
        /// `Weapon.WeaponUpdate`, which is the only caller that reports real ammo — the other
        /// call sites only ever clear to "---", and an empty hand is derived from staleness
        /// instead, which covers every way of losing a weapon without patching each of them.
        /// </summary>
        internal static LocalPlayer Current;

        /// <summary>
        /// How long a pushed value stays believable. `WeaponUpdate` re-pushes every frame while
        /// a weapon is held, so anything older than this means that hand is empty.
        /// </summary>
        private const float StaleAfter = 0.25f;

        internal static void Record(string combined, bool right)
        {
            var player = Current;
            if (player == null)
                return;

            if (right)
            {
                player.AmmoRight = combined;
                player.AmmoRightAt = Time.unscaledTime;
            }
            else
            {
                player.AmmoLeft = combined;
                player.AmmoLeftAt = Time.unscaledTime;
            }
        }

        /// <summary>The string this player's counter should show: their own ammo, or "---" for an empty hand.</summary>
        internal static string Value(LocalPlayer player, bool right)
        {
            if (player == null)
                return "---";
            float at = right ? player.AmmoRightAt : player.AmmoLeftAt;
            if (at <= 0f || Time.unscaledTime - at > StaleAfter)
                return "---";
            return right ? player.AmmoRight : player.AmmoLeft;
        }
    }
}
