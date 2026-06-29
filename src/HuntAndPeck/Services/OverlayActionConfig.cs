using System;
using System.Configuration;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// What happens when the user completes a hint (types its full label).
    /// </summary>
    public enum ClickMode
    {
        /// <summary>Move the cursor onto the target and fire a real left click.</summary>
        RealClick,

        /// <summary>Call the UI Automation Invoke pattern on the target.</summary>
        Invoke
    }

    /// <summary>
    /// Reads the click-action and nudge settings from hap.exe.config. Parsing is
    /// split into pure methods (unit-tested) and ConfigurationManager wrappers.
    /// Unknown or missing values fall back to safe defaults so a bad config never
    /// breaks the overlay.
    /// </summary>
    public static class OverlayActionConfig
    {
        /// <summary>Indicator text shown in move-only mode.</summary>
        public const string MoveOnlyHint =
            "move-only  |  arrows = nudge (shift = fast)  |  type a label = jump  |  click or Esc = done";

        /// <summary>Parses the ClickMode setting; only "Invoke" yields Invoke, everything else RealClick.</summary>
        public static ClickMode ParseClickMode(string raw)
        {
            if (string.Equals(raw, "Invoke", StringComparison.OrdinalIgnoreCase))
            {
                return ClickMode.Invoke;
            }
            return ClickMode.RealClick;
        }

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

        /// <summary>Reads ClickMode from appSettings (hot-reload). Defaults to RealClick.</summary>
        public static ClickMode ReadClickMode()
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
                return ParseClickMode(ConfigurationManager.AppSettings["ClickMode"]);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return ClickMode.RealClick;
            }
        }

        /// <summary>Reads the plain-arrow nudge step (px). Default 3.</summary>
        public static int ReadNudgeStep()
        {
            return ReadIntSetting("NudgeStep", 3);
        }

        /// <summary>Reads the Shift+arrow nudge step (px). Default 15.</summary>
        public static int ReadNudgeStepFast()
        {
            return ReadIntSetting("NudgeStepFast", 15);
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
