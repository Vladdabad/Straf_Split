using System;
using UnityEngine;

namespace StraftatSplitscreen
{
    /// <summary>
    /// Every option that belongs to a person rather than to the machine.
    ///
    /// The dividing line is "would two people sharing one screen want different answers?".
    /// Sensitivity, invert, the hold/toggle binds, FOV, brightness and the crosshair all pass
    /// that test. Resolution, fullscreen, vsync, quality, frame cap, the volumes and voice chat
    /// do not — there is one screen and one pair of speakers — so they are left alone entirely
    /// and keep writing the game's own singleton.
    /// </summary>
    internal enum Setting
    {
        ToggleTwoAxis,
        InvertMouseX,
        InvertMouseY,
        MouseSensitivity,
        MouseAimScopeSensitivity,
        MouseAimSensitivity,
        HorizontalSensitivity,
        VerticalSensitivity,
        SprintToggle,
        AimToggle,
        LeanToggle,
        CrouchToggle,
        ReverseSprintBind,
        InverseFireBinding,
        Fov,
        Brightness,
        DamageIntensity,
        DisableCrosshair,
        EnableFixedCrosshair,
        ShowSpeedometer,
        MinimalistUi,
        MotionBlur,
    }

    /// <summary>
    /// One row of the table that drives all of this: how a setting is stored, how it is read off
    /// the game's <see cref="Settings"/> singleton, and how it is written back.
    ///
    /// <see cref="Key"/> does triple duty, and that is not a coincidence — it is the game's own
    /// spelling in all three places. It is the PlayerPrefs key <see cref="Settings"/> saves
    /// under, and it is also the name of the private bool on <see cref="DisplayLoadValues"/>
    /// that marks which widget shows this setting. So one string is enough to load a value,
    /// save it, and find the slider that displays it, and adding a setting to the per-player set
    /// is one row here rather than an edit in four files.
    /// </summary>
    internal sealed class SettingDef
    {
        internal readonly Setting Id;
        internal readonly string Key;

        /// <summary>Stored by the game as an int 0/1 rather than a float. Matters for PlayerPrefs and for which widget type shows it.</summary>
        internal readonly bool IsBool;

        internal readonly Func<Settings, float> Read;
        internal readonly Action<Settings, float> Write;

        internal SettingDef(Setting id, string key, bool isBool, Func<Settings, float> read, Action<Settings, float> write)
        {
            Id = id;
            Key = key;
            IsBool = isBool;
            Read = read;
            Write = write;
        }

        /// <summary>
        /// Where this setting lives for the given roster slot.
        ///
        /// Slot 0 keeps the game's own key, untouched and un-suffixed. That is what makes the
        /// mod invisible to a vanilla launch: player 1's settings are still the settings, in the
        /// same prefs the game has always written, so uninstalling loses nothing and installing
        /// changes nothing until a second player actually joins.
        /// </summary>
        internal string KeyFor(int slot)
        {
            return slot == 0 ? Key : Key + ".p" + (slot + 1);
        }
    }

    /// <summary>
    /// The per-player setting table. Order must match <see cref="Setting"/> — verified once at
    /// load by <see cref="Verify"/> rather than trusted.
    /// </summary>
    internal static class SettingTable
    {
        internal static readonly SettingDef[] All =
        {
            new SettingDef(Setting.ToggleTwoAxis, "toggleTwoAxis", true,
                s => s.toggleTwoAxis ? 1f : 0f, (s, v) => s.toggleTwoAxis = v > 0.5f),
            new SettingDef(Setting.InvertMouseX, "invertMouseX", true,
                s => s.invertMouseX ? 1f : 0f, (s, v) => s.invertMouseX = v > 0.5f),
            new SettingDef(Setting.InvertMouseY, "invertMouseY", true,
                s => s.invertMouseY ? 1f : 0f, (s, v) => s.invertMouseY = v > 0.5f),
            new SettingDef(Setting.MouseSensitivity, "mouseSensitivity", false,
                s => s.mouseSensitivity, (s, v) => s.mouseSensitivity = v),
            new SettingDef(Setting.MouseAimScopeSensitivity, "mouseAimScopeSensitivity", false,
                s => s.mouseAimScopeSensitivity, (s, v) => s.mouseAimScopeSensitivity = v),
            new SettingDef(Setting.MouseAimSensitivity, "mouseAimSensitivity", false,
                s => s.mouseAimSensitivity, (s, v) => s.mouseAimSensitivity = v),
            // The game's field is horizontalSensitivity but its key is mouseXSensitivity.
            new SettingDef(Setting.HorizontalSensitivity, "mouseXSensitivity", false,
                s => s.horizontalSensitivity, (s, v) => s.horizontalSensitivity = v),
            new SettingDef(Setting.VerticalSensitivity, "mouseYSensitivity", false,
                s => s.verticalSensitivity, (s, v) => s.verticalSensitivity = v),
            new SettingDef(Setting.SprintToggle, "sprintToggle", true,
                s => s.sprintToggle ? 1f : 0f, (s, v) => s.sprintToggle = v > 0.5f),
            new SettingDef(Setting.AimToggle, "aimToggle", true,
                s => s.aimToggle ? 1f : 0f, (s, v) => s.aimToggle = v > 0.5f),
            new SettingDef(Setting.LeanToggle, "leanToggle", true,
                s => s.leanToggle ? 1f : 0f, (s, v) => s.leanToggle = v > 0.5f),
            new SettingDef(Setting.CrouchToggle, "crouchToggle", true,
                s => s.crouchToggle ? 1f : 0f, (s, v) => s.crouchToggle = v > 0.5f),
            new SettingDef(Setting.ReverseSprintBind, "reverseSprintBind", true,
                s => s.reverseSprintBind ? 1f : 0f, (s, v) => s.reverseSprintBind = v > 0.5f),
            new SettingDef(Setting.InverseFireBinding, "inverseFireBinding", true,
                s => s.inverseFireBinding ? 1f : 0f, (s, v) => s.inverseFireBinding = v > 0.5f),
            // The game's field is normalFovValue but its key is fovValue.
            new SettingDef(Setting.Fov, "fovValue", false,
                s => s.normalFovValue, (s, v) => s.normalFovValue = v),
            new SettingDef(Setting.Brightness, "brightness", false,
                s => s.brightness, (s, v) => s.brightness = v),
            new SettingDef(Setting.DamageIntensity, "damageIntensity", false,
                s => s.damageIntensity, (s, v) => s.damageIntensity = v),
            new SettingDef(Setting.DisableCrosshair, "disableCrosshair", true,
                s => s.disableCrosshair ? 1f : 0f, (s, v) => s.disableCrosshair = v > 0.5f),
            new SettingDef(Setting.EnableFixedCrosshair, "enableFixedCrosshair", true,
                s => s.enableFixedCrosshair ? 1f : 0f, (s, v) => s.enableFixedCrosshair = v > 0.5f),
            new SettingDef(Setting.ShowSpeedometer, "showSpeedometer", true,
                s => s.showSpeedometer ? 1f : 0f, (s, v) => s.showSpeedometer = v > 0.5f),
            new SettingDef(Setting.MinimalistUi, "minimalistUi", true,
                s => s.minimalistUi ? 1f : 0f, (s, v) => s.minimalistUi = v > 0.5f),
            new SettingDef(Setting.MotionBlur, "motionBlur", true,
                s => s.motionBlur ? 1f : 0f, (s, v) => s.motionBlur = v > 0.5f),
        };

        internal static int Count => All.Length;

        internal static SettingDef Of(Setting id)
        {
            return All[(int)id];
        }

        /// <summary>The setting a <see cref="DisplayLoadValues"/> flag names, or null if it marks a global one.</summary>
        internal static SettingDef ByKey(string key)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Key == key)
                    return All[i];
            }
            return null;
        }

        /// <summary>
        /// The array is positional and the enum indexes into it, so a row inserted in the wrong
        /// place would silently make every setting after it the wrong one — the kind of bug that
        /// reads as "sensitivity now controls brightness". Cheap to check once, so check.
        /// </summary>
        internal static void Verify()
        {
            int expected = Enum.GetValues(typeof(Setting)).Length;
            if (All.Length != expected)
            {
                Plugin.Log.LogError($"[settings] the setting table has {All.Length} rows for {expected} settings — per-player settings are disabled.");
                Ok = false;
                return;
            }
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Id != (Setting)i)
                {
                    Plugin.Log.LogError($"[settings] setting table row {i} is {All[i].Id}, not {(Setting)i} — per-player settings are disabled.");
                    Ok = false;
                    return;
                }
            }
            Ok = true;
        }

        /// <summary>False if <see cref="Verify"/> found the table inconsistent. Everything here no-ops when it is.</summary>
        internal static bool Ok { get; private set; }
    }

    /// <summary>
    /// One player's copy of the per-player settings.
    ///
    /// Values live in a flat array indexed by <see cref="Setting"/> rather than as named fields,
    /// because every operation on them — load, save, copy onto the singleton, copy back off it,
    /// show in the options menu — is then a loop over <see cref="SettingTable.All"/> instead of
    /// twenty-two lines that have to be kept in step with each other.
    ///
    /// Player 1 has one of these too. Theirs is a mirror of the game's <see cref="Settings"/>
    /// singleton, kept fresh every tick, which is what lets the rest of the mod treat "the
    /// singleton's current values" and "some player's values" as the same kind of thing.
    /// </summary>
    internal sealed class PlayerSettings
    {
        private readonly float[] _values = new float[SettingTable.Count];

        /// <summary>The roster slot these prefs belong to. See the note on slots in <see cref="Load"/>.</summary>
        internal int Slot;

        internal PlayerSettings(int slot)
        {
            Slot = slot;
        }

        internal float Get(Setting id)
        {
            return _values[(int)id];
        }

        internal bool GetBool(Setting id)
        {
            return _values[(int)id] > 0.5f;
        }

        internal void Set(Setting id, float value)
        {
            _values[(int)id] = value;
        }

        /// <summary>Copy these values onto the game's singleton — the "borrow" half of <see cref="SettingsRouting"/>.</summary>
        internal void Push(Settings settings)
        {
            if (settings == null)
                return;
            var all = SettingTable.All;
            for (int i = 0; i < all.Length; i++)
                all[i].Write(settings, _values[i]);
        }

        /// <summary>Copy the singleton's current values into these — how an edit made through the game's own handler is captured.</summary>
        internal void Pull(Settings settings)
        {
            if (settings == null)
                return;
            var all = SettingTable.All;
            for (int i = 0; i < all.Length; i++)
                _values[i] = all[i].Read(settings);
        }

        /// <summary>
        /// Read this slot's saved values, falling back to <paramref name="fallback"/> — player
        /// 1's — for anything never saved. A player joining for the first time therefore starts
        /// on the host's settings rather than on the prefab's defaults, which is both the least
        /// surprising thing and the only one that gives a usable sensitivity out of the box.
        /// </summary>
        internal void Load(PlayerSettings fallback)
        {
            var all = SettingTable.All;
            var instance = Settings.Instance;
            for (int i = 0; i < all.Length; i++)
            {
                var def = all[i];
                float fall = fallback != null
                    ? fallback._values[i]
                    : (instance != null ? def.Read(instance) : 0f);
                string key = def.KeyFor(Slot);
                if (!PlayerPrefs.HasKey(key))
                {
                    _values[i] = fall;
                    continue;
                }
                _values[i] = def.IsBool
                    ? (PlayerPrefs.GetInt(key, fall > 0.5f ? 1 : 0) == 1 ? 1f : 0f)
                    : PlayerPrefs.GetFloat(key, fall);
            }
        }

        /// <summary>
        /// Write this slot's values back, in exactly the form the game writes them — ints for
        /// bools, floats for the rest — so that slot 0's keys stay readable by a vanilla launch.
        /// </summary>
        internal void Save()
        {
            var all = SettingTable.All;
            for (int i = 0; i < all.Length; i++)
            {
                var def = all[i];
                string key = def.KeyFor(Slot);
                if (def.IsBool)
                    PlayerPrefs.SetInt(key, _values[i] > 0.5f ? 1 : 0);
                else
                    PlayerPrefs.SetFloat(key, _values[i]);
            }
        }

        /// <summary>
        /// Push the look settings onto one pawn, the way <see cref="Settings.Update"/> pushes
        /// them onto <c>Settings.localPlayer</c>.
        ///
        /// This is a transcription of that method's tail, quirks included: in two-axis mode the
        /// game does not touch the two aim sensitivities, so neither do we. The game only ever
        /// runs it for the one pawn the singleton points at, which is player 1's — every other
        /// local player was left on the prefab's stock 2.0 with no way to change it. That is the
        /// bug this whole file exists to fix, and this method is where the fix lands.
        /// </summary>
        internal void ApplyTo(FirstPersonController controller)
        {
            if (controller == null)
                return;

            if (GetBool(Setting.ToggleTwoAxis))
            {
                controller.lookSpeedX = Get(Setting.HorizontalSensitivity);
                controller.lookSpeedY = Get(Setting.VerticalSensitivity);
            }
            else
            {
                controller.lookSpeedX = Get(Setting.MouseSensitivity);
                controller.lookSpeedY = Get(Setting.MouseSensitivity);
                controller.lookSpeedAim = Get(Setting.MouseAimScopeSensitivity);
                controller.lookSpeedAimNoScope = Get(Setting.MouseAimSensitivity);
            }

            controller.sprintToggle = GetBool(Setting.SprintToggle);
            controller.aimToggle = GetBool(Setting.AimToggle);
            controller.leanToggle = GetBool(Setting.LeanToggle);
            controller.invertX = GetBool(Setting.InvertMouseX);
            controller.invertY = GetBool(Setting.InvertMouseY);
            controller.crouchToggle = GetBool(Setting.CrouchToggle);
            controller.reverseSprintBind = GetBool(Setting.ReverseSprintBind);
        }

        internal string Describe()
        {
            return $"sens={Get(Setting.MouseSensitivity):0.##} " +
                   $"aim={Get(Setting.MouseAimSensitivity):0.##} " +
                   $"scope={Get(Setting.MouseAimScopeSensitivity):0.##} " +
                   $"fov={Get(Setting.Fov):0.#} " +
                   $"invX={GetBool(Setting.InvertMouseX)} invY={GetBool(Setting.InvertMouseY)} " +
                   $"twoAxis={GetBool(Setting.ToggleTwoAxis)}";
        }
    }
}
