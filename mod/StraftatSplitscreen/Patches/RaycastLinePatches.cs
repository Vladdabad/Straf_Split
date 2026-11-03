using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Reports the game's runtime-drawn `RaycastLine` objects, which are the leading suspect for
    /// "a beam of light from a player's head that both players can see".
    ///
    /// `PhysicsGrenade`, `ShrapnelBallistic` and `Claymore` each build a bare
    /// `new GameObject("RaycastLine")` with a `LineRenderer` on it — white and fading after 2.5s
    /// for the first two, a persistent red tripwire for the Claymore. They are created at the
    /// SCENE ROOT, under no pawn, so <see cref="PlayerVisibility"/> cannot sort them into
    /// anybody's set and they necessarily draw in every player's slice at once. In vanilla that
    /// is unremarkable — one screen, one viewer.
    ///
    /// Logging where each line starts, which way it points and how far it is from each player's
    /// head identifies it without having to catch a 2.5-second object with a screenshot key. If
    /// the reported start position sits on somebody's head, this is the bug; if it sits at an
    /// explosion out in the map, it is not and the search continues elsewhere.
    ///
    /// Diagnostic only — it draws nothing, hides nothing, and changes no behaviour.
    /// </summary>
    [HarmonyPatch]
    internal static class RaycastLine_Report_Patch
    {
        private static readonly string[] Types = { "PhysicsGrenade", "ShrapnelBallistic", "Claymore" };

        private static float _nextLog;

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            var found = new List<MethodBase>();
            foreach (var name in Types)
            {
                var type = AccessTools.TypeByName(name);
                var method = type != null ? AccessTools.DeclaredMethod(type, "RenderObject") : null;
                if (method != null)
                    found.Add(method);
                else
                    Plugin.Log.LogWarning($"[line] {name}.RenderObject not found — that source of RaycastLine goes unreported.");
            }
            return found;
        }

        private static void Postfix(object __instance, Vector3 direction, float maxDistance)
        {
            if (!LocalPlayers.SplitActive)
                return;

            // Shrapnel draws one line per fragment; a few per second is enough to identify them.
            if (Time.unscaledTime < _nextLog)
                return;
            _nextLog = Time.unscaledTime + 0.25f;

            var component = __instance as Component;
            if (component == null)
                return;

            Vector3 start = component.transform.position;

            var heads = new System.Text.StringBuilder();
            foreach (var player in LocalPlayers.All)
            {
                var cams = player.Cameras;
                if (cams == null || cams.Length == 0 || cams[0] == null)
                    continue;
                if (heads.Length > 0)
                    heads.Append(' ');
                heads.Append($"{player.Label}+{Vector3.Distance(start, cams[0].transform.position):F2}m");
            }

            Plugin.Log.LogInfo(
                $"[line] {component.GetType().Name} drew a RaycastLine from {start} " +
                $"dir={direction} len={maxDistance:F1} | distance to heads: {heads}");
        }
    }
}
