using System;
using System.Configuration;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// Reads the arrow-slide nudge step from hap.exe.config. Parsing is split
    /// into a pure method (unit-tested) and a ConfigurationManager wrapper.
    /// Unknown or missing values fall back to a safe default so a bad config
    /// never breaks the overlay.
    /// </summary>
    public static class OverlayActionConfig
    {
        /// <summary>Legend shown on the overlay so the gestures are discoverable.</summary>
        public const string OverlayLegend =
            "type a label = jump  |  arrows = slide (shift = fast)  |  click or Esc = done";

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

        /// <summary>Reads the plain-arrow slide step (px). Default 3.</summary>
        public static int ReadNudgeStep()
        {
            return ReadIntSetting("NudgeStep", 3);
        }

        /// <summary>Reads the Shift+arrow slide step (px). Default 15.</summary>
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
