using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Reconnaissance patch. PlayerSetup.OnStartClient is where the game decides which
    /// player is "local": IsOwner gates camera enabling, HUD, and Settings.Instance.localPlayer.
    /// That branch is the seam a splitscreen mod has to widen.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSetup))]
    internal static class PlayerSetupPatch
    {
        // cameras / graphics are [SerializeField] private, so reach them reflectively.
        private static readonly AccessTools.FieldRef<PlayerSetup, Camera[]> CamerasRef =
            AccessTools.FieldRefAccess<PlayerSetup, Camera[]>("cameras");

        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerSetup.OnStartClient))]
        private static void AfterOnStartClient(PlayerSetup __instance)
        {
            if (!Plugin.VerboseLogging.Value)
                return;

            var cameras = CamerasRef(__instance);
            var localPlayer = Settings.Instance != null ? Settings.Instance.localPlayer : null;

            Plugin.Log.LogInfo(
                $"PlayerSetup.OnStartClient on '{__instance.gameObject.name}' " +
                $"IsOwner={__instance.IsOwner} " +
                $"cameras={(cameras == null ? "null" : cameras.Length.ToString())} " +
                $"localPlayer={(localPlayer == null ? "null" : localPlayer.gameObject.name)}");

            if (cameras == null)
                return;

            for (int i = 0; i < cameras.Length; i++)
            {
                var cam = cameras[i];
                if (cam == null)
                    continue;

                // rect is the whole screen today; splitscreen means giving each local
                // player its own Camera.rect (and its own AudioListener / HUD canvas).
                Plugin.Log.LogInfo(
                    $"  camera[{i}] '{cam.name}' enabled={cam.enabled} " +
                    $"depth={cam.depth} rect={cam.rect} cullingMask=0x{cam.cullingMask:X}");
            }
        }
    }
}
