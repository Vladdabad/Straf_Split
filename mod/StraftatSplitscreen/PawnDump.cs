using System.Text;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Roster and collider dump: who exists at the connection level, and what a shot can hit.
    ///
    /// Renderers, cameras and per-mesh visibility live in <see cref="RenderDiagnostics"/> and
    /// input in <see cref="InputDiagnostics"/>; this covers the two things neither does — the
    /// ClientInstance list (which decides who spawns a pawn at all) and the collider tree
    /// (which decides what intercepts a shot).
    /// </summary>
    internal static class PawnDump
    {
        private static string Path(Transform t, Transform stopAt)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null && p != stopAt; p = p.parent)
                sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        internal static void Dump()
        {
            Plugin.Log.LogInfo("================ PAWN DUMP ================");

            var pause = PauseManager.Instance;
            Plugin.Log.LogInfo(
                $"  local players={LocalPlayers.Count} (max {LocalPlayers.MaxPlayers}), " +
                $"split={LocalPlayers.SplitActive}, " +
                $"scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}' " +
                $"inMainMenu={(pause != null ? pause.inMainMenu.ToString() : "?")}");

            foreach (var player in LocalPlayers.All)
            {
                var pawn = player.Pawn;
                Plugin.Log.LogInfo(
                    $"  {player.Label} client={(player.Client == null ? "NONE" : player.Client.gameObject.name)} " +
                    $"PlayerId={(player.Client != null ? player.Client.PlayerId.ToString() : "?")} " +
                    $"readied={player.Readied} " +
                    $"device={(player.Device != null ? player.Device.displayName : "NONE (waiting)")} " +
                    $"pawn={(pawn == null ? "NONE" : pawn.name)} " +
                    $"viewport={PlayerVisibility.Viewport(player.Index, LocalPlayers.Count)}");
            }

            // Anything at the connection level we did NOT put on the roster is worth seeing:
            // a stray ClientInstance is how a phantom player entry shows up in the lobby.
            foreach (var client in Object.FindObjectsOfType<ClientInstance>())
            {
                if (LocalPlayers.Owning(client) != null)
                    continue;
                Plugin.Log.LogInfo(
                    $"  UNTRACKED ClientInstance '{client.gameObject.name}' PlayerId={client.PlayerId} " +
                    $"IsOwner={client.IsOwner}");
            }

            foreach (var setup in Object.FindObjectsOfType<PlayerSetup>())
            {
                var player = LocalPlayers.OwningPawn(setup.gameObject);
                Plugin.Log.LogInfo(
                    $"--- pawn '{setup.gameObject.name}' owner={setup.IsOwner} " +
                    $"which={(player != null ? player.Label : "REMOTE")} rootLayer={setup.gameObject.layer} " +
                    $"hasPlayerHealth={setup.GetComponent<PlayerHealth>() != null}");

                foreach (var c in setup.GetComponentsInChildren<Collider>(true))
                {
                    Plugin.Log.LogInfo(
                        $"    collider '{Path(c.transform, setup.transform)}' enabled={c.enabled} " +
                        $"trigger={c.isTrigger} layer={c.gameObject.layer} " +
                        $"activeInHierarchy={c.gameObject.activeInHierarchy} type={c.GetType().Name}");
                }
            }

            // Melee is resolved by a real physics contact rather than a raycast, so whether it
            // can land between local players is a matrix question, not a layer-swap one.
            DamageLayers.LogCollisionMatrix();

            Plugin.Log.LogInfo("============== END PAWN DUMP ==============");
        }
    }
}
