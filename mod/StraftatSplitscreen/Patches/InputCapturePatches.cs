using HarmonyLib;
using UnityEngine;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Records the exact moment each pawn component captures its action set.
    ///
    /// The whole input split rests on one assumption: that every component reading
    /// <c>InputManager.inputActions</c> does so inside the window where
    /// <see cref="PlayerManager_SpawnPlayer_Patch"/> has the static swapped to player 2's
    /// controls. That assumption has never been checked — the mod logs its intent and moves
    /// on. These postfixes log what each component actually got, next to what the static held
    /// at that instant, so a component whose Awake falls outside the window is visible
    /// immediately rather than inferred from gameplay three layers later.
    ///
    /// All of them are opt-in behind <c>[Debug] TraceInputCapture</c>: they fire for player 1
    /// too, and on every respawn.
    /// </summary>
    internal static class CaptureLog
    {
        /// <summary>
        /// Guard for the patch targets. A silently skipped patch is worse than a missing one:
        /// the log would look healthy while the capture trace quietly covered only part of the
        /// pawn, so say so out loud.
        /// </summary>
        internal static bool Require(System.Type type, string method)
        {
            if (AccessTools.Method(type, method) != null)
                return true;
            Plugin.Log.LogWarning(
                $"[capture] {type.Name}.{method} not found — capture tracing for that component " +
                "is disabled. The game's method names have changed since this was written.");
            return false;
        }

        internal static void Record(Component c, PlayerControls captured)
        {
            if (!Plugin.TraceInputCapture.Value)
                return;

            var shared = InputManager.inputActions;

            // Naming the player whose asset the static currently holds is the point:
            // "swap ACTIVE for P3" means the spawn window is covering this Awake.
            string staticIs = shared == null ? "null" : "shared";
            foreach (var p in LocalPlayers.All)
            {
                if (p.Controls != null && ReferenceEquals(shared, p.Controls))
                {
                    staticIs = p.Label + " (swap ACTIVE)";
                    break;
                }
            }

            string capturedIs;
            if (captured == null)
            {
                capturedIs = "null";
            }
            else
            {
                capturedIs = "ORPHAN";
                foreach (var p in LocalPlayers.All)
                {
                    if (p.Controls != null && ReferenceEquals(captured.asset, p.Controls.asset))
                    {
                        capturedIs = p.Label;
                        break;
                    }
                }
                if (capturedIs == "ORPHAN" && shared != null && ReferenceEquals(captured.asset, shared.asset))
                    capturedIs = "shared";
            }

            Plugin.Log.LogInfo(
                $"[capture] {c.GetType().Name,-22} on '{c.transform.root.name}' " +
                $"static={staticIs} captured={capturedIs} " +
                $"asset={InputDiagnostics.AssetLabel(captured != null ? captured.asset : null)}");
        }
    }

    [HarmonyPatch(typeof(FirstPersonController), "Awake___UserLogic")]
    internal static class FirstPersonController_Awake_CapturePatch
    {
        private static bool Prepare()
        {
            return CaptureLog.Require(typeof(FirstPersonController), "Awake___UserLogic");
        }

        private static void Postfix(FirstPersonController __instance)
        {
            CaptureLog.Record(__instance, __instance.playerControls);
        }
    }

    [HarmonyPatch(typeof(PlayerShoot), "Awake___UserLogic")]
    internal static class PlayerShoot_Awake_CapturePatch
    {
        private static bool Prepare()
        {
            return CaptureLog.Require(typeof(PlayerShoot), "Awake___UserLogic");
        }

        private static void Postfix(PlayerShoot __instance)
        {
            CaptureLog.Record(__instance, __instance.playerControls);
        }
    }

    [HarmonyPatch(typeof(PlayerPickup), "Awake___UserLogic")]
    internal static class PlayerPickup_Awake_CapturePatch
    {
        private static bool Prepare()
        {
            return CaptureLog.Require(typeof(PlayerPickup), "Awake___UserLogic");
        }

        private static void Postfix(PlayerPickup __instance)
        {
            // playerControls is private here; read it by name rather than through a cached
            // FieldRef, which would throw at class-load time if the field were ever renamed.
            CaptureLog.Record(__instance, InputDiagnostics.ControlsOf(__instance));
        }
    }

    [HarmonyPatch(typeof(FPArms), "Awake")]
    internal static class FPArms_Awake_CapturePatch
    {
        private static bool Prepare()
        {
            return CaptureLog.Require(typeof(FPArms), "Awake");
        }

        private static void Postfix(FPArms __instance)
        {
            CaptureLog.Record(__instance, InputDiagnostics.ControlsOf(__instance));
        }
    }

    /// <summary>
    /// The game rebuilds its action asset from scratch on a settings/rebind change. Every
    /// pawn alive at that moment keeps the OLD instance, which no longer matches the static
    /// the mod filters — so P1 silently reverts to hearing every device, including the
    /// gamepad. Never observed, but it is the one event that can undo the split without
    /// anything in the log changing, so it is worth a line of its own.
    /// </summary>
    [HarmonyPatch(typeof(InputManager), nameof(InputManager.RestartEngine))]
    internal static class InputManager_RestartEngine_Patch
    {
        private static bool Prepare()
        {
            return CaptureLog.Require(typeof(InputManager), "RestartEngine");
        }

        private static void Postfix()
        {
            Plugin.Log.LogWarning(
                "[capture] InputManager.RestartEngine rebuilt the action asset — every live pawn " +
                $"still holds the old one. New static = {InputDiagnostics.AssetLabel(InputManager.inputActions != null ? InputManager.inputActions.asset : null)}.");
        }
    }
}
