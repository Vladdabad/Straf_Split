using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace StraftatSplitscreen
{
    /// <summary>
    /// The options screen, repainted for whoever owns it.
    ///
    /// The game fills its sliders and toggles once, from a coroutine in
    /// <c>DisplayLoadValues.Start</c>, and never again — there is only ever one player, so the
    /// values on screen cannot go stale. With four of them the screen has to be re-read every
    /// time it changes hands, or player 3 opens the menu and sees player 1's numbers, drags a
    /// slider a millimetre, and inherits them.
    ///
    /// Which widget shows which setting is not guessed: <see cref="DisplayLoadValues"/> already
    /// tags itself with a private bool per setting, named exactly like the PlayerPrefs key, and
    /// that name is <see cref="SettingDef.Key"/>. So the game's own tagging is the mapping, and
    /// a widget whose tag names a setting the mod does not treat as per-player — volume,
    /// resolution, frame cap — is simply left alone.
    ///
    /// Values are written with <c>SetValueWithoutNotify</c>. The ordinary setter would fire the
    /// widget's onValueChanged, which is wired to the very <c>Settings.Change*</c> handler this
    /// routing intercepts: repainting the screen would register as twenty-two edits by the new
    /// owner.
    /// </summary>
    internal static class SettingsUi
    {
        private static readonly AccessTools.FieldRef<PauseManager, GameObject> OptionsMenuRef =
            AccessTools.FieldRefAccess<PauseManager, GameObject>("optionsMenu");
        private static readonly AccessTools.FieldRef<PauseManager, GameObject> PauseMenuRef =
            AccessTools.FieldRefAccess<PauseManager, GameObject>("pauseMenu");

        /// <summary>One FieldInfo per per-player setting, for the flag of the same name on DisplayLoadValues.</summary>
        private static FieldInfo[] _flags;

        /// <summary>Which setting a given widget shows, resolved once per widget.</summary>
        private static readonly Dictionary<DisplayLoadValues, SettingDef> _resolved =
            new Dictionary<DisplayLoadValues, SettingDef>();

        /// <summary>
        /// Is a settings screen up?
        ///
        /// The options menu needs both halves of the test. Its buttons toggle it with plain
        /// <c>GameObject.SetActive</c>, but <c>PauseManager.Awake</c> leaves it active with
        /// <c>localScale</c> zero and a tween scales it back up on open — so activeInHierarchy
        /// alone reads as open from the first frame of the process. Scale alone would miss the
        /// in-match pause menu, which is SetActive and nothing else.
        ///
        /// Stated as a physical fact rather than a guess about the scene: a menu that is on
        /// screen is active and has a non-zero scale. Anything else is not on screen, whatever
        /// it is doing internally.
        /// </summary>
        internal static bool IsOpen
        {
            get
            {
                var pause = PauseManager.Instance;
                if (pause == null)
                    return false;

                var options = OptionsMenuRef(pause);
                if (options != null && options.activeInHierarchy && options.transform.localScale.x > 0.001f)
                    return true;

                var menu = PauseMenuRef(pause);
                return menu != null && menu.activeInHierarchy;
            }
        }

        /// <summary>Show <paramref name="player"/>'s values on every per-player widget in the options screen.</summary>
        internal static void Refresh(LocalPlayer player)
        {
            if (player == null || !SettingTable.Ok)
                return;

            var settings = player.Settings;
            int painted = 0;

            foreach (var widget in WidgetsFor(player))
            {
                // FindObjectsOfTypeAll reaches loaded assets as well as live objects; only
                // things that are actually in a scene are on anybody's screen.
                if (widget == null || !widget.gameObject.scene.IsValid())
                    continue;

                var def = Resolve(widget);
                if (def == null)
                    continue;

                float value = settings.Get(def.Id);

                var toggle = widget.GetComponent<Toggle>();
                if (toggle != null)
                {
                    toggle.SetIsOnWithoutNotify(value > 0.5f);
                    painted++;
                    continue;
                }

                var slider = widget.GetComponent<Slider>();
                if (slider != null)
                {
                    slider.SetValueWithoutNotify(value);
                    painted++;
                }
            }

            if (Plugin.VerboseLogging.Value)
                Plugin.Log.LogInfo($"[settings] repainted {painted} option widget(s) for {player.Label}.");
        }

        /// <summary>
        /// The option widgets that belong to this player.
        ///
        /// Once every player has their own menu there are four copies of every slider in the
        /// scene, and a scene-wide sweep would paint one player's numbers onto all four screens —
        /// the very bug this class exists to prevent, arriving from the other direction. Inside a
        /// rig the answer is that player's subtree and nothing else.
        ///
        /// The scene-wide scan stays as the fallback for the single shared menu: the main menu,
        /// and any run with <c>SplitMenus</c> turned off.
        /// </summary>
        private static IEnumerable<DisplayLoadValues> WidgetsFor(LocalPlayer player)
        {
            var root = MenuSplit.RootFor(player);
            if (root != null)
                return root.GetComponentsInChildren<DisplayLoadValues>(true);
            return Resources.FindObjectsOfTypeAll<DisplayLoadValues>();
        }

        /// <summary>
        /// Which setting this widget is tagged for, or null for one the mod leaves global.
        /// Cached — the scan is thirty-odd reflective reads per widget and the answer never
        /// changes for a given component.
        /// </summary>
        private static SettingDef Resolve(DisplayLoadValues widget)
        {
            SettingDef known;
            if (_resolved.TryGetValue(widget, out known))
                return known;

            EnsureFlags();

            SettingDef found = null;
            for (int i = 0; i < _flags.Length; i++)
            {
                var field = _flags[i];
                if (field == null)
                    continue;
                if ((bool)field.GetValue(widget))
                {
                    found = SettingTable.All[i];
                    break;
                }
            }

            // Live components are destroyed on scene changes and Unity's null is not the
            // dictionary's, so sweep the dead entries rather than growing forever.
            if (_resolved.Count > 256)
                Prune();

            _resolved[widget] = found;
            return found;
        }

        private static void EnsureFlags()
        {
            if (_flags != null)
                return;

            _flags = new FieldInfo[SettingTable.Count];
            for (int i = 0; i < SettingTable.Count; i++)
            {
                var def = SettingTable.All[i];
                var field = AccessTools.Field(typeof(DisplayLoadValues), def.Key);
                if (field == null || field.FieldType != typeof(bool))
                {
                    // Not fatal: the setting simply has no widget we can find, so it keeps
                    // working everywhere except on screen. Worth a line, because "the slider
                    // shows the wrong player's value" is the symptom.
                    Plugin.Log.LogWarning(
                        $"[settings] DisplayLoadValues has no bool '{def.Key}' — that widget cannot be " +
                        "repainted when the options screen changes hands.");
                    continue;
                }
                _flags[i] = field;
            }
        }

        private static void Prune()
        {
            var dead = new List<DisplayLoadValues>();
            foreach (var kv in _resolved)
            {
                if (kv.Key == null)
                    dead.Add(kv.Key);
            }
            foreach (var key in dead)
                _resolved.Remove(key);
        }
    }
}
