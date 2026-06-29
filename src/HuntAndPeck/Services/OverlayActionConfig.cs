using System;
using System.Collections.Generic;
using System.Configuration;
using System.Windows.Forms;
using HuntAndPeck.NativeMethods;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// What typing a label's 2 chars does at the cursor position.
    /// </summary>
    public enum ClickAction
    {
        Left,
        Move,
        Right,
        Double
    }

    /// <summary>
    /// Reads overlay and hotkey settings from hap.exe.config. Parsing is split
    /// into pure methods (unit-tested) and ConfigurationManager wrappers.
    /// Unknown or missing values fall back to safe defaults so a bad config
    /// never breaks the app.
    /// </summary>
    public static class OverlayActionConfig
    {
        private static readonly IList<ClickAction> DefaultClickOrder =
            new[] { ClickAction.Left, ClickAction.Right, ClickAction.Double, ClickAction.Move };

        /// <summary>Legend shown on the overlay so the gestures are discoverable.</summary>
        public const string OverlayLegend =
            "arrows = move all labels  |  space = cycle click mode  |  type 2 chars = fire  |  Esc = cancel";

        /// <summary>Parses an integer; returns defaultValue when blank, non-numeric, or non-positive.</summary>
        public static int ParseInt(string raw, int defaultValue)
        {
            int v;
            if (int.TryParse(raw, out v) && v > 0)
            {
                return v;
            }
            return defaultValue;
        }

        /// <summary>
        /// Parses a comma/semicolon/pipe separated list of ClickActions (case-insensitive,
        /// duplicates dropped). Falls back to the default order when empty or all-invalid.
        /// </summary>
        public static IList<ClickAction> ParseClickActionOrder(string raw)
        {
            var result = new List<ClickAction>();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var part in raw.Split(',', ';', '|'))
                {
                    ClickAction a;
                    if (Enum.TryParse(part.Trim(), true, out a) && !result.Contains(a))
                    {
                        result.Add(a);
                    }
                }
            }
            return result.Count > 0 ? result : new List<ClickAction>(DefaultClickOrder);
        }

        /// <summary>Parses a System.Windows.Forms.Keys name (case-insensitive); fallback otherwise.</summary>
        public static Keys ParseKeys(string raw, Keys fallback)
        {
            Keys k;
            return Enum.TryParse(raw, true, out k) ? k : fallback;
        }

        /// <summary>Parses a comma/semicolon/pipe separated list of KeyModifier flags; fallback when empty/all-invalid.</summary>
        public static KeyModifier ParseKeyModifiers(string raw, KeyModifier fallback)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }
            KeyModifier result = 0;
            bool any = false;
            foreach (var part in raw.Split(',', ';', '|'))
            {
                KeyModifier mod;
                if (Enum.TryParse(part.Trim(), true, out mod))
                {
                    result |= mod;
                    any = true;
                }
            }
            return any ? result : fallback;
        }

        /// <summary>Plain-arrow pan step (px). Default 3.</summary>
        public static int ReadNudgeStep()
        {
            return ReadIntSetting("NudgeStep", 3);
        }

        /// <summary>Shift+arrow pan step (px). Default 15.</summary>
        public static int ReadNudgeStepFast()
        {
            return ReadIntSetting("NudgeStepFast", 15);
        }

        /// <summary>The Space-cycle order of click modes (hot-reload).</summary>
        public static IList<ClickAction> ReadClickActionOrder()
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
                return ParseClickActionOrder(ConfigurationManager.AppSettings["ClickModeOrder"]);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return new List<ClickAction>(DefaultClickOrder);
            }
        }

        /// <summary>The main overlay hotkey key (read once at startup). Fallback when missing/invalid.</summary>
        public static Keys ReadHotkeyKey(Keys fallback)
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
                return ParseKeys(ConfigurationManager.AppSettings["HotkeyKey"], fallback);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>The main overlay hotkey modifiers (read once at startup). Fallback when missing/invalid.</summary>
        public static KeyModifier ReadHotkeyModifier(KeyModifier fallback)
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
                return ParseKeyModifiers(ConfigurationManager.AppSettings["HotkeyModifier"], fallback);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static int ReadIntSetting(string key, int defaultValue)
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
                return ParseInt(ConfigurationManager.AppSettings[key], defaultValue);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return defaultValue;
            }
        }
    }
}
