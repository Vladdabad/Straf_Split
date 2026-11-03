using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace StraftatSplitscreen.Patches
{
    /// <summary>
    /// Route an options edit to the player who opened the menu.
    ///
    /// One patch covers every per-player handler, because none of them needs individual
    /// treatment: each reads a widget, writes the matching field on the singleton, saves it to
    /// PlayerPrefs, and a couple reach through <c>Settings.localPlayer</c> to poke the pawn.
    /// Run any of them with the singleton borrowed for player 3 and pointed at player 3's pawn,
    /// and it does exactly the right thing to exactly the right player — including the pawn
    /// pokes, which is the part a hand-written reimplementation would get wrong.
    ///
    /// Afterwards the value the handler wrote is lifted off the singleton and filed under that
    /// player's own keys. See <see cref="SettingsRouting.Finish"/> for why player 1 has to be
    /// re-saved in the same breath.
    ///
    /// When player 1 owns the menu — and whenever there is no split — nothing is patched at all
    /// in effect: the prefix returns without borrowing and the game's own handler runs as
    /// shipped, writing the singleton and the vanilla prefs keys.
    /// </summary>
    [HarmonyPatch]
    internal static class Settings_OptionsHandler_Routing
    {
        /// <summary>
        /// The handlers that edit something a person owns. Everything absent from this list —
        /// resolution, fullscreen, vsync, quality, frame cap, the volumes, voice chat, reduced
        /// VFX — is a property of the machine, is left entirely alone, and stays global.
        /// </summary>
        private static readonly string[] Handlers =
        {
            nameof(Settings.ChangeMouseSensitivity),
            nameof(Settings.ChangeAimScopeMouseSensitivity),
            nameof(Settings.ChangeAimMouseSensitivity),
            nameof(Settings.ChangeMouseXSensitivity),
            nameof(Settings.ChangeMouseYSensitivity),
            nameof(Settings.ChangeFov),
            nameof(Settings.ChangeBrightness),
            nameof(Settings.ChangeDamageIntensity),
            nameof(Settings.ToggleTwoAxisMouse),
            nameof(Settings.ToggleVerticalInvert),
            nameof(Settings.ToggleHorizontalInvert),
            nameof(Settings.ToggleSprint),
            nameof(Settings.ToggleAim),
            nameof(Settings.ToggleLean),
            nameof(Settings.ToggleCrouch),
            nameof(Settings.ToggleReverseSprintBind),
            nameof(Settings.ToggleMinimalistUi),
            nameof(Settings.ToggleMotionBlur),
            nameof(Settings.InverseFireBinding),
            nameof(Settings.EnableFixedCrosshair),
            nameof(Settings.EnableSpeedometer),
            nameof(Settings.DisableCrosshair),
        };

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            var found = new List<MethodBase>();
            foreach (string name in Handlers)
            {
                var method = AccessTools.Method(typeof(Settings), name);
                if (method == null)
                {
                    // A renamed handler in a game update: that setting quietly stops being
                    // per-player. Name it rather than letting the whole patch fail to apply.
                    Plugin.Log.LogError($"[settings] Settings.{name} not found — that option cannot be routed per player.");
                    continue;
                }
                found.Add(method);
            }
            return found;
        }

        private static void Prefix(out SettingsRouting.Borrowed __state)
        {
            __state = default(SettingsRouting.Borrowed);
            if (!SettingsRouting.Enabled || !LocalPlayers.SplitActive)
                return;

            // Best answer first: the event system that dispatched this call. With a menu per
            // player, MenuSplit.EditingPlayer names the owner exactly — and it answers only
            // while that player's own menu is on screen, so it cannot attribute a stray write to
            // somebody who merely owns the event system that ticked last.
            var owner = MenuSplit.EditingPlayer();
            if (owner == null)
            {
                owner = SettingsRouting.Owner;
                if (owner == null || owner.Index == 0)
                    return;

                // Only an open screen can be edited by a person. These handlers are also called
                // by the game itself, from the coroutine in DisplayLoadValues that fills the
                // widgets in — assigning Slider.value fires onValueChanged exactly like a drag
                // does. Routing that would hand player 1's saved values to whoever last opened
                // the menu. (On a clone that coroutine is suppressed outright; see
                // DisplayLoadValues_Clone_Suppression.)
                if (!SettingsUi.IsOpen)
                {
                    SettingsRouting.NoteEditWhileClosed();
                    return;
                }
            }
            else if (owner.Index == 0)
            {
                // Player 1 edits down the game's own untouched path, as always.
                return;
            }

            __state = SettingsRouting.Borrow(owner, swapLocalPlayer: true);
        }

        /// <summary>
        /// A finalizer rather than a postfix, throughout this file. A postfix is skipped when the
        /// method it follows throws, and a skipped restore would leave one player's numbers in
        /// the singleton for every other player to read — the settings would appear to "drift"
        /// onto whoever last touched them, with nothing in the log to say why.
        /// </summary>
        private static Exception Finalizer(Exception __exception, SettingsRouting.Borrowed __state)
        {
            SettingsRouting.Finish(__state);
            return __exception;
        }
    }

    /// <summary>
    /// Give every local player their look settings each frame.
    ///
    /// <c>Settings.Update</c> pushes sensitivity, invert and the hold/toggle binds onto
    /// <c>Settings.localPlayer</c> — one pawn, and the mod pins that reference to player 1, so
    /// everybody else was left on the prefab's stock values with no way to change them. The
    /// postfix repeats the same push for the whole roster, each player from their own settings.
    /// </summary>
    [HarmonyPatch(typeof(Settings), "Update")]
    internal static class Settings_Update_Patch
    {
        private static void Postfix()
        {
            SettingsRouting.ApplyToPawns();
        }
    }

    /// <summary>
    /// Who pressed Start.
    ///
    /// <c>PauseManager.menu</c> is a standalone serialized InputAction, not part of anybody's
    /// pinned action asset, so every controller in the room can open the pause menu — which is
    /// what we want, and it also means the callback context carries the one piece of information
    /// the whole feature turns on: the device that did it.
    /// </summary>
    [HarmonyPatch(typeof(PauseManager), "Menu")]
    internal static class PauseManager_Menu_Patch
    {
        private static void Prefix(InputAction.CallbackContext ctx)
        {
            var control = ctx.control;
            SettingsRouting.NoteMenuPress(control != null ? control.device : null);
        }
    }

    /// <summary>
    /// FOV, brightness and the speedometer, read straight off the singleton inside every pawn's
    /// own Update. Borrowed for whichever local player's pawn is ticking.
    /// </summary>
    [HarmonyPatch(typeof(FirstPersonController), "Update")]
    internal static class FirstPersonController_Update_SettingsPatch
    {
        private static void Prefix(FirstPersonController __instance, out SettingsRouting.Borrowed __state)
        {
            __state = default(SettingsRouting.Borrowed);
            if (!SettingsRouting.Enabled || !LocalPlayers.SplitActive)
                return;

            var player = LocalPlayers.OwningPawn(__instance.gameObject);
            if (player == null || player.Index == 0)
                return;

            __state = SettingsRouting.Borrow(player, swapLocalPlayer: false);
        }

        private static Exception Finalizer(Exception __exception, SettingsRouting.Borrowed __state)
        {
            SettingsRouting.Return(__state);
            return __exception;
        }
    }

    /// <summary>
    /// The crosshair reads three settings — disable, fixed, and the FOV it compares the aim zoom
    /// against. Each local player has their own Crosshair (see <see cref="CrosshairSplit"/>), and
    /// each one knows the pawn it tracks, so the borrow follows that pawn's owner.
    /// </summary>
    [HarmonyPatch(typeof(Crosshair), "Update")]
    internal static class Crosshair_Update_SettingsPatch
    {
        private static void Prefix(Crosshair __instance, out SettingsRouting.Borrowed __state)
        {
            __state = default(SettingsRouting.Borrowed);
            if (!SettingsRouting.Enabled || !LocalPlayers.SplitActive)
                return;

            var tracked = __instance.player;
            if (tracked == null)
                return;

            var player = LocalPlayers.OwningPawn(tracked.gameObject);
            if (player == null || player.Index == 0)
                return;

            __state = SettingsRouting.Borrow(player, swapLocalPlayer: false);
        }

        private static Exception Finalizer(Exception __exception, SettingsRouting.Borrowed __state)
        {
            SettingsRouting.Return(__state);
            return __exception;
        }
    }

    /// <summary>
    /// The red damage flash. <c>TakeHit</c> copies <c>Settings.damageIntensity</c> into a static
    /// and then writes the material, so the value has to be right *before* it runs — a postfix
    /// would arrive after the flash was already sized. The material itself is already per-player
    /// (see <see cref="ScreenEffects"/>); this makes its strength per-player too.
    /// </summary>
    [HarmonyPatch(typeof(CameraEffect), nameof(CameraEffect.TakeHit))]
    internal static class CameraEffect_TakeHit_SettingsPatch
    {
        private static void Prefix(CameraEffect __instance, out SettingsRouting.Borrowed __state)
        {
            __state = default(SettingsRouting.Borrowed);
            if (!SettingsRouting.Enabled || !LocalPlayers.SplitActive)
                return;

            var player = LocalPlayers.Owning(__instance);
            if (player == null || player.Index == 0)
                return;

            __state = SettingsRouting.Borrow(player, swapLocalPlayer: false);
        }

        private static Exception Finalizer(Exception __exception, SettingsRouting.Borrowed __state)
        {
            SettingsRouting.Return(__state);
            return __exception;
        }
    }

    /// <summary>
    /// The settings a pawn is built with: minimalist UI and motion blur, both read off the
    /// singleton in the owner branch of <c>PlayerSetup.OnStartClient</c> and applied to the pawn
    /// being set up. Borrowed so each player's pawn is built to their own taste.
    ///
    /// Deliberately a separate patch from the one in <see cref="SplitscreenPatches"/> that saves
    /// and restores <c>Settings.localPlayer</c> around the same method. They are independent —
    /// this one does not touch that reference — and keeping them apart means this one can be a
    /// finalizer without changing how the other behaves.
    /// </summary>
    [HarmonyPatch(typeof(PlayerSetup), nameof(PlayerSetup.OnStartClient))]
    internal static class PlayerSetup_OnStartClient_SettingsPatch
    {
        private static void Prefix(PlayerSetup __instance, out SettingsRouting.Borrowed __state)
        {
            __state = default(SettingsRouting.Borrowed);
            if (!SettingsRouting.Enabled || !LocalPlayers.SplitActive || !__instance.IsOwner)
                return;

            var player = LocalPlayers.OwningPawn(__instance.gameObject);
            if (player == null || player.Index == 0)
                return;

            __state = SettingsRouting.Borrow(player, swapLocalPlayer: false);
        }

        private static Exception Finalizer(Exception __exception, SettingsRouting.Borrowed __state)
        {
            SettingsRouting.Return(__state);
            return __exception;
        }
    }

    /// <summary>
    /// Swapped fire buttons when holding two weapons.
    ///
    /// One line of <c>WeaponUpdate</c>, on a method that runs for every weapon every frame, so
    /// this re-evaluates that line for the holder rather than borrowing the singleton around the
    /// whole call. The weapon knows its holder through <c>ItemBehaviour.playerPickup</c>, which
    /// lives on the pawn and is therefore tagged.
    /// </summary>
    [HarmonyPatch(typeof(Weapon), nameof(Weapon.WeaponUpdate))]
    internal static class Weapon_WeaponUpdate_SettingsPatch
    {
        private static void Postfix(Weapon __instance)
        {
            if (!SettingsRouting.Enabled || !LocalPlayers.SplitActive)
                return;
            if (__instance.gameObject.layer != 8 || !__instance.IsOwner)
                return;

            var behaviour = __instance.behaviour;
            var pickup = behaviour != null ? behaviour.playerPickup : null;
            if (pickup == null)
                return;

            var player = LocalPlayers.Owning(pickup);
            if (player == null || player.Index == 0)
                return;

            // sync___get_value_*, not the SyncAccessor_* property: FishNet's generated property
            // has no legal C# spelling, so it can only be reached through the accessor method.
            __instance.invertFire = pickup.sync___get_value_hasObjectInHand()
                && pickup.sync___get_value_hasObjectInLeftHand()
                && player.Settings.GetBool(Setting.InverseFireBinding);
        }
    }
}
