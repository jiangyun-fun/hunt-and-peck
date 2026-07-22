using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
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
    /// What rectangle the overlay and its hint grid cover.
    /// </summary>
    public enum HintBounds
    {
        /// <summary>Full monitor the foreground window is on; labels fill the screen.</summary>
        Screen,
        /// <summary>The foreground window rect (the previous, per-window behavior).</summary>
        Window
    }

    /// <summary>
    /// What the hotkey opens: a one-shot overlay that closes after one click, or a
    /// persistent overlay that stays up for repeated clicks until Esc / a mouse click.
    /// Pressing the hotkey again while the overlay is up toggles between the two
    /// (Grid only; Automation stays one-shot because its labels go stale on navigation).
    /// </summary>
    public enum TriggerMode
    {
        OneClick,
        Continuous
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

        // --- config freshness: avoid re-parsing hap.exe.config on every read ---
        private static DateTime _configMtimeUtc = DateTime.MinValue;
        private static readonly object _configRefreshLock = new object();

        /// <summary>
        /// Keeps the appSettings section fresh without re-parsing the file on every read.
        /// ConfigurationManager.RefreshSection forces a full disk re-parse; the overlay
        /// path reads many settings per trigger, which used to mean one re-parse per read
        /// (the same anti-pattern as the old per-hint refresh, ~0.85ms each). Instead, stat
        /// the config file's last-write time and only re-parse when it actually changed
        /// (i.e. the user edited hap.exe.config for hot-reload). Within a trigger, and
        /// across triggers with no edit, reads are served from memory. Best-effort: any
        /// stat failure falls through to a refresh so settings are never served stale.
        /// </summary>
        public static void EnsureFresh()
        {
            DateTime mtime;
            try
            {
                var path = AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
                if (string.IsNullOrEmpty(path))
                {
                    RefreshAppSettings();
                    return;
                }
                mtime = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                // Stat failed (locked/missing/unauthorized): refresh rather than risk stale.
                RefreshAppSettings();
                return;
            }

            lock (_configRefreshLock)
            {
                if (mtime == _configMtimeUtc)
                {
                    return; // cached section is still current
                }
                _configMtimeUtc = mtime;
            }
            RefreshAppSettings();
        }

        /// <summary>Re-parses appSettings from disk; best-effort (never throws).</summary>
        private static void RefreshAppSettings()
        {
            try
            {
                ConfigurationManager.RefreshSection("appSettings");
            }
            catch (Exception)
            {
                // A failed refresh leaves the prior values; reads still work.
            }
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

        /// <summary>
        /// Parses a percent (0-100, clamped). Returns defaultValue when blank or non-numeric.
        /// </summary>
        public static int ParsePercent(string raw, int defaultValue)
        {
            int v;
            if (int.TryParse(raw, out v))
            {
                if (v < 0) return 0;
                if (v > 100) return 100;
                return v;
            }
            return defaultValue;
        }

        /// <summary>
        /// Parses a HintBounds name (case-insensitive). Returns defaultValue when blank
        /// or unrecognized so a bad config never breaks the app.
        /// </summary>
        public static HintBounds ParseHintBounds(string raw, HintBounds defaultValue)
        {
            HintBounds v;
            if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse(raw.Trim(), true, out v))
            {
                return v;
            }
            return defaultValue;
        }

        /// <summary>
        /// Parses a TriggerMode name (case-insensitive). Returns defaultValue when blank
        /// or unrecognized so a bad config never breaks the app.
        /// </summary>
        public static TriggerMode ParseTriggerMode(string raw, TriggerMode defaultValue)
        {
            TriggerMode v;
            if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse(raw.Trim(), true, out v))
            {
                return v;
            }
            return defaultValue;
        }

        /// <summary>
        /// True when the hint source is Grid (case-insensitive), or blank/unset (Grid is
        /// the default).
        /// </summary>
        public static bool IsGridHintSource(string hintSource)
        {
            return string.IsNullOrWhiteSpace(hintSource) ||
                   string.Equals(hintSource, "Grid", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether MergeWithTaskbar should merge the taskbar session into the foreground
        /// session. In Grid + Screen mode the foreground grid already spans the full
        /// monitor (taskbar strip included), so a second full-screen taskbar grid would
        /// stack two labels at every cell; skip it. Window mode (grid is window-sized,
        /// does not reach the taskbar) and Automation mode (taskbar contributes its own
        /// real controls) still merge.
        /// </summary>
        public static bool ShouldMergeTaskbar(string hintSource, HintBounds bounds)
        {
            return !(IsGridHintSource(hintSource) && bounds == HintBounds.Screen);
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

        /// <summary>
        /// Reads the hint label font size (hot-reload). Returns null when unset or
        /// invalid so the caller can fall back to the Options-dialog default.
        /// </summary>
        public static string ReadHintFontSize()
        {
            try
            {
                EnsureFresh();
                var raw = ConfigurationManager.AppSettings["HintFontSize"];
                int v;
                return int.TryParse(raw, out v) && v > 0 ? raw : null;
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return null;
            }
        }

        /// <summary>
        /// Hint pill fill opacity as 0.0-1.0 (hot-reload). Configured as a percent
        /// (0-100, default 40): softens the vivid yellow so background peeks through,
        /// while the label text stays fully opaque. Bad/missing values fall back to 0.4.
        /// </summary>
        public static double ReadHintPillOpacity()
        {
            try
            {
                EnsureFresh();
                return ParsePercent(ConfigurationManager.AppSettings["HintPillOpacity"], 40) / 100.0;
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return 0.4;
            }
        }

        /// <summary>
        /// Dimmed-label opacity as 0.0-1.0 (hot-reload). Configured as a percent
        /// (0-100, default 20): the canvas-wide opacity used when you press backtick to
        /// dim labels so the text behind is readable. Bad/missing values fall back to 0.2.
        /// </summary>
        public static double ReadHintDimOpacity()
        {
            try
            {
                EnsureFresh();
                return ParsePercent(ConfigurationManager.AppSettings["HintDimOpacity"], 20) / 100.0;
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return 0.2;
            }
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
                EnsureFresh();
                return ParseClickActionOrder(ConfigurationManager.AppSettings["ClickModeOrder"]);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return new List<ClickAction>(DefaultClickOrder);
            }
        }

        /// <summary>
        /// What the overlay covers (hot-reload): Screen = full monitor the foreground
        /// window is on (labels fill the screen); Window = the foreground window rect.
        /// Default Screen.
        /// </summary>
        public static HintBounds ReadHintBounds()
        {
            try
            {
                EnsureFresh();
                return ParseHintBounds(ConfigurationManager.AppSettings["HintBoundsSource"], HintBounds.Screen);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return HintBounds.Screen;
            }
        }

        /// <summary>
        /// The default trigger mode (hot-reload): OneClick closes the overlay after one
        /// click; Continuous keeps it up for repeated clicks until Esc / a mouse click.
        /// At runtime Continuous applies to Grid only (Automation stays one-shot). Default
        /// Continuous.
        /// </summary>
        public static TriggerMode ReadTriggerMode()
        {
            try
            {
                EnsureFresh();
                return ParseTriggerMode(ConfigurationManager.AppSettings["OverlayTriggerMode"], TriggerMode.Continuous);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return TriggerMode.Continuous;
            }
        }

        /// <summary>The hint source name (hot-reload): "Grid" or "Automation".</summary>
        public static string ReadHintSource()
        {
            try
            {
                EnsureFresh();
                return ConfigurationManager.AppSettings["HintSource"];
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return null;
            }
        }

        /// <summary>
        /// Reads a raw appSetting string (no parsing, no default). Used by the Options
        /// dialog to display the current value.
        /// </summary>
        public static string ReadRawString(string key)
        {
            try
            {
                EnsureFresh();
                return ConfigurationManager.AppSettings[key];
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Writes an appSetting to hap.exe.config and saves, so the change hot-reloads on
        /// the next trigger (EnsureFresh sees the updated file mtime). Best-effort: a
        /// failed write leaves the prior value rather than crashing the caller.
        /// </summary>
        public static void WriteSetting(string key, string value)
        {
            try
            {
                var cfg = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
                cfg.AppSettings.Settings.Remove(key);
                cfg.AppSettings.Settings.Add(key, value ?? string.Empty);
                cfg.Save(ConfigurationSaveMode.Modified);
                ConfigurationManager.RefreshSection("appSettings");
                _configMtimeUtc = DateTime.MinValue; // force the next EnsureFresh to re-stat
            }
            catch (Exception)
            {
                // Best-effort: leave the prior value so the app stays usable.
            }
        }

        /// <summary>The main overlay hotkey key (read once at startup). Fallback when missing/invalid.</summary>
        public static Keys ReadHotkeyKey(Keys fallback)
        {
            try
            {
                EnsureFresh();
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
                EnsureFresh();
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
                EnsureFresh();
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
