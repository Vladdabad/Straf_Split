using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Open the menu belonging to whoever pressed Start.
    ///
    /// <c>PauseManager.menu</c> is a standalone serialized InputAction rather than part of
    /// anybody's pinned asset, so every controller in the room reaches this method — which is
    /// what we want, and the callback context names the device that did it. The game's own body
    /// then runs unchanged with <see cref="MenuSplit.Borrow"/> holding the manager's menu
    /// references on that player's rig.
    /// </summary>
    [HarmonyPatch(typeof(PauseManager), "Menu")]
    internal static class PauseManager_Menu_MenuSplitPatch
    {
        private static void Prefix(InputAction.CallbackContext ctx, out MenuSplit.Borrowed __state)
        {
            __state = default(MenuSplit.Borrowed);
            if (!MenuSplit.Active)
                return;

            var device = ctx.control != null ? ctx.control.device : null;
            var player = LocalPlayers.Holder(device);
            if (player == null && (device is Keyboard || device is Mouse))
            {
                // The keyboard belongs to player 1, who is the host sitting at the machine —
                // the same rule SettingsRouting.WhoIsDriving applies.
                player = LocalPlayers.At(0);
            }
            if (player == null)
                return;

            __state = MenuSplit.Borrow(player);
        }

        private static Exception Finalizer(Exception __exception, MenuSplit.Borrowed __state)
        {
            MenuSplit.Return(__state);
            return __exception;
        }
    }

    /// <summary>
    /// Everything in <c>PauseManager.Update</c> that asks "is the game paused?" by reading
    /// <c>pauseMenu.activeSelf</c>.
    ///
    /// With four menus that one object answers for player 1 only, and both things the answer
    /// drives are global: whether the cursor is freed, and whether the game considers itself
    /// paused. Both should be true if *anybody* has a menu up, or player 2 opens theirs and
    /// cannot move a mouse to it because player 1's is closed.
    ///
    /// A postfix rather than a borrow: there is no single player this call belongs to.
    /// </summary>
    [HarmonyPatch(typeof(PauseManager), "Update")]
    internal static class PauseManager_Update_MenuSplitPatch
    {
        private static void Postfix(PauseManager __instance)
        {
            if (!MenuSplit.Active)
                return;

            // pause and otherPauseBools are both public on PauseManager, so no reflection here.
            bool anyOpen = MenuSplit.AnyOpen();
            __instance.pause = anyOpen;

            if (anyOpen || __instance.otherPauseBools)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }

            // Vanilla ends with Crosshair.Instance.image.enabled = !pauseMenu.activeSelf — one
            // crosshair, one menu. Each local player has their own of both now, so the rule is
            // applied per player; otherwise player 2 opening their menu blanks player 1's
            // crosshair mid-fight.
            foreach (var player in LocalPlayers.All)
            {
                var cross = CrosshairSplit.For(player.Index);
                if (cross == null || cross.image == null)
                    continue;
                cross.image.enabled = !MenuSplit.IsOpenFor(player);
            }
        }
    }

    /// <summary>
    /// Stop a cloned options screen from filling itself in.
    ///
    /// This is rule 3 of <see cref="MenuSplit"/>, and it is the patch that keeps the previous
    /// attempt's worst bug from coming back. <c>DisplayLoadValues.LoadValues</c> is a coroutine
    /// that assigns <c>Toggle.isOn</c> and <c>Slider.value</c> — the setters that FIRE
    /// onValueChanged — from <c>Settings.Instance</c>, which at rest holds player 1's values. On
    /// a clone that is twenty-odd real <c>Settings.Change*</c> calls attributed to whoever the
    /// menu belongs to, each one saved to their PlayerPrefs. Opening player 3's menu would
    /// quietly overwrite player 3's saved settings with player 1's, FOV included.
    ///
    /// The clone does not need it: <see cref="SettingsUi.Refresh"/> paints those same widgets
    /// with <c>SetValueWithoutNotify</c> the moment the menu opens, which is the whole point of
    /// that method existing.
    ///
    /// Player 1's own copy is left completely alone — it is the scene's original, it is where the
    /// game's values actually come from, and nothing about it changes.
    /// </summary>
    [HarmonyPatch(typeof(DisplayLoadValues), "Start")]
    internal static class DisplayLoadValues_Clone_Suppression
    {
        private static bool Prefix(DisplayLoadValues __instance)
        {
            if (!MenuSplit.Active)
                return true;
            if (!MenuSplit.IsClonedWidget(__instance.transform))
                return true;

            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo(
                    $"[menu] suppressed the value-loading coroutine on a cloned '{__instance.name}'.");
            return false;
        }
    }
}
