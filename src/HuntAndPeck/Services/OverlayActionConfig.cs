using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
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
        Double,
        /// <summary>Three rapid left clicks -- selects a whole line (a sentence in Word).</summary>
        Triple
    }

    /// <summary>
    /// How two-pick text-span selection synthesizes the selection (TextSelectMethod key).
    /// ShiftClick: pick-1 plain click (anchor), pick-2 Shift+click (extend).
    /// Drag: pick-1 button-down (held), pick-2 button-up.
    /// </summary>
    public enum TextSelectMethod
    {
        ShiftClick,
        Drag
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
    /// What the dedicated arrow cluster does while the overlay is up. Passthrough
    /// (default) sends arrows to the app beneath (Excel/list/editor focus nav); Pan
    /// is the legacy behavior (arrows pan all labels). Shift+hjkl / Ctrl+Shift+hjkl
    /// pan the labels regardless of this setting.
    /// </summary>
    public enum ArrowKeyBehavior
    {
        Pan,
        Passthrough
    }

    /// <summary>
    /// A label-pan step for one nudge tier. <see cref="IsAuto"/> means "match the current
    /// zone cell" (cellW for horizontal, cellH for vertical) so a Large nudge traverses
    /// exactly one zone; otherwise <see cref="X"/> (h/l) and <see cref="Y"/> (j/k) are
    /// independent pixel amounts (per-axis so horizontal and vertical can differ).
    /// </summary>
    public struct NudgeStep
    {
        public bool IsAuto;
        public int X;
        public int Y;
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
        /// Parses a boolean (case-insensitive "true"/"false", or "1"/"0"). Returns
        /// defaultValue when blank, unrecognized, or null so a bad config never
        /// breaks the app. Pure + unit-tested.
        /// </summary>
        public static bool ParseBool(string raw, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }
            var t = raw.Trim();
            if (string.Equals(t, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(t, "false", StringComparison.OrdinalIgnoreCase)) return false;
            if (t == "1") return true;
            if (t == "0") return false;
            return defaultValue;
        }

        /// <summary>
        /// Parses a per-axis nudge step: "<c>x,y</c>" (two positive px values), the literal
        /// "<c>auto</c>" (match the current zone cell), or <paramref name="defaultValue"/>
        /// when blank/malformed. Pure + unit-tested.
        /// </summary>
        public static NudgeStep ParseNudgeStep(string raw, NudgeStep defaultValue)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var t = raw.Trim();
                if (string.Equals(t, "auto", StringComparison.OrdinalIgnoreCase))
                {
                    return new NudgeStep { IsAuto = true };
                }
                var parts = t.Split(',');
                int x, y;
                if (parts.Length == 2
                    && int.TryParse(parts[0].Trim(), out x)
                    && int.TryParse(parts[1].Trim(), out y)
                    && x > 0 && y > 0)
                {
                    return new NudgeStep { X = x, Y = y };
                }
            }
            return defaultValue;
        }

        /// <summary>
        /// Parses 4 virtual-key codes (the L,D,U,R nudge keys for one tier) from a
        /// comma-separated list of <see cref="Keys"/> names. Returns <paramref name="fallback"/>
        /// when blank, not exactly 4 names, or any name is unrecognized. Pure + unit-tested.
        /// (<see cref="Keys"/> values are virtual-key codes, so the int cast is the VK code.)
        /// </summary>
        public static int[] ParseNudgeKeys(string raw, int[] fallback)
        {
            if (string.IsNullOrWhiteSpace(raw) || fallback == null || fallback.Length != 4)
            {
                return fallback;
            }
            var parts = raw.Split(',');
            if (parts.Length != 4)
            {
                return fallback;
            }
            var result = new int[4];
            for (int i = 0; i < 4; i++)
            {
                Keys k;
                if (!Enum.TryParse(parts[i].Trim(), true, out k))
                {
                    return fallback;
                }
                result[i] = (int)k;
            }
            return result;
        }

        /// <summary>
        /// Parses the overlay auto-close idle timeout in seconds (≥0; locale-tolerant
        /// decimal). Returns defaultValue when blank/negative/unparseable. 0 = off.
        /// Pure + unit-tested.
        /// </summary>
        public static double ParseAutoCloseSec(string raw, double defaultValue)
        {
            if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
            double v;
            if (double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0) return v;
            return defaultValue;
        }

        /// <summary>
        /// Parses a comma/semicolon/pipe-separated list of <see cref="Keys"/> names into a
        /// fixed-size array (expected count = fallback.Length). Returns fallback when blank,
        /// wrong count, or any name is unrecognized. Pure + unit-tested.
        /// </summary>
        public static Keys[] ParseKeyList(string raw, Keys[] fallback)
        {
            if (string.IsNullOrWhiteSpace(raw) || fallback == null) return fallback;
            var parts = raw.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != fallback.Length) return fallback;
            var result = new Keys[fallback.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                Keys k;
                if (!Enum.TryParse(parts[i].Trim(), true, out k)) return fallback;
                result[i] = k;
            }
            return result;
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
        /// Parses an ArrowKeyBehavior name (case-insensitive). Returns defaultValue when
        /// blank or unrecognized so a bad config never breaks the app. Pure + unit-tested.
        /// </summary>
        public static ArrowKeyBehavior ParseArrowKeyBehavior(string raw, ArrowKeyBehavior defaultValue)
        {
            ArrowKeyBehavior v;
            if (!string.IsNullOrWhiteSpace(raw) && Enum.TryParse(raw.Trim(), true, out v))
            {
                return v;
            }
            return defaultValue;
        }

        /// <summary>
        /// Whether a freshly opened overlay should start in continuous mode. Pure + unit-tested.
        /// Continuous requires Grid (Automation labels go stale on navigation) AND the configured
        /// default Continuous. <paramref name="gridSource"/> is whether the hint source is Grid;
        /// <paramref name="configMode"/> is the <see cref="ReadTriggerMode"/> default.
        /// </summary>
        public static bool ComputeIsContinuous(bool gridSource, TriggerMode configMode)
        {
            if (!gridSource) return false;
            return configMode == TriggerMode.Continuous;
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

        /// <summary>
        /// Parses the TextSelectMethod key ("ShiftClick" | "Drag", case-insensitive);
        /// returns the caller's fallback when blank or unrecognized.
        /// </summary>
        public static TextSelectMethod ParseTextSelectMethod(string raw, TextSelectMethod fallback)
        {
            TextSelectMethod m;
            return Enum.TryParse(raw, true, out m) ? m : fallback;
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

        // Default nudge key sets (VK codes), in L,D,U,R order. Keys values ARE VK codes,
        // so the int cast is the VK code Classify compares against.
        private static readonly int[] DefaultSmallNudgeKeys =
            { (int)Keys.M, (int)Keys.Oemcomma, (int)Keys.OemPeriod, (int)Keys.Oem2 };
        private static readonly int[] DefaultMediumNudgeKeys =
            { (int)Keys.H, (int)Keys.J, (int)Keys.K, (int)Keys.L };
        private static readonly int[] DefaultLargeNudgeKeys =
            { (int)Keys.U, (int)Keys.I, (int)Keys.O, (int)Keys.P };

        /// <summary>
        /// Small-tier pan step (hot-reload) for Shift+m , . / : "x,y" px or "auto".
        /// Default "3,3".
        /// </summary>
        public static NudgeStep ReadNudgeStepSmall()
        {
            try
            {
                EnsureFresh();
                return ParseNudgeStep(ConfigurationManager.AppSettings["NudgeStepSmall"], new NudgeStep { X = 3, Y = 3 });
            }
            catch (Exception)
            {
                return new NudgeStep { X = 3, Y = 3 };
            }
        }

        /// <summary>
        /// Medium-tier pan step (hot-reload) for Shift+hjkl: "x,y" px or "auto".
        /// Default "15,15".
        /// </summary>
        public static NudgeStep ReadNudgeStepMedium()
        {
            try
            {
                EnsureFresh();
                return ParseNudgeStep(ConfigurationManager.AppSettings["NudgeStepMedium"], new NudgeStep { X = 15, Y = 15 });
            }
            catch (Exception)
            {
                return new NudgeStep { X = 15, Y = 15 };
            }
        }

        /// <summary>
        /// Large-tier pan step (hot-reload) for Shift+uiop: "x,y" px or "auto" (auto = the
        /// current zone's cellW/cellH, so one Large nudge traverses exactly one zone).
        /// Default "auto".
        /// </summary>
        public static NudgeStep ReadNudgeStepLarge()
        {
            try
            {
                EnsureFresh();
                return ParseNudgeStep(ConfigurationManager.AppSettings["NudgeStepLarge"], new NudgeStep { IsAuto = true });
            }
            catch (Exception)
            {
                return new NudgeStep { IsAuto = true };
            }
        }

        /// <summary>The 4 VK codes (L,D,U,R) for the small tier (hot-reload). Default m , . /.</summary>
        public static int[] ReadNudgeKeysSmall()
        {
            try { EnsureFresh(); return ParseNudgeKeys(ConfigurationManager.AppSettings["NudgeKeysSmall"], DefaultSmallNudgeKeys); }
            catch (Exception) { return DefaultSmallNudgeKeys; }
        }

        /// <summary>The 4 VK codes (L,D,U,R) for the medium tier (hot-reload). Default h j k l.</summary>
        public static int[] ReadNudgeKeysMedium()
        {
            try { EnsureFresh(); return ParseNudgeKeys(ConfigurationManager.AppSettings["NudgeKeysMedium"], DefaultMediumNudgeKeys); }
            catch (Exception) { return DefaultMediumNudgeKeys; }
        }

        /// <summary>The 4 VK codes (L,D,U,R) for the large tier (hot-reload). Default u i o p.</summary>
        public static int[] ReadNudgeKeysLarge()
        {
            try { EnsureFresh(); return ParseNudgeKeys(ConfigurationManager.AppSettings["NudgeKeysLarge"], DefaultLargeNudgeKeys); }
            catch (Exception) { return DefaultLargeNudgeKeys; }
        }

        private static readonly Keys[] DefaultQuadrantKeys = { Keys.F1, Keys.F2, Keys.F3, Keys.F4 };

        /// <summary>
        /// Quadrant hotkey keys (read once at startup): 4 Keys names (F1..F4) for
        /// TL/TR/BL/BR (Ctrl+Shift+F1..F4 opens the overlay scoped to that quadrant).
        /// Default F1,F2,F3,F4.
        /// </summary>
        public static Keys[] ReadQuadrantHotkeyKeys()
        {
            try { EnsureFresh(); return ParseKeyList(ConfigurationManager.AppSettings["QuadrantHotkeyKeys"], DefaultQuadrantKeys); }
            catch (Exception) { return DefaultQuadrantKeys; }
        }

        /// <summary>Quadrant hotkey modifier (read once at startup). Default Control|Shift.</summary>
        public static KeyModifier ReadQuadrantHotkeyModifier()
        {
            try { EnsureFresh(); return ParseKeyModifiers(ConfigurationManager.AppSettings["QuadrantHotkeyModifier"], KeyModifier.Control | KeyModifier.Shift); }
            catch (Exception) { return KeyModifier.Control | KeyModifier.Shift; }
        }

        /// <summary>
        /// Overlay idle auto-close timeout in seconds (hot-reload, read when the overlay
        /// arms). 0 = off (never auto-close). Default 0.
        /// </summary>
        public static double ReadAutoCloseSec()
        {
            try { EnsureFresh(); return ParseAutoCloseSec(ConfigurationManager.AppSettings["OverlayAutoCloseSec"], 0.0); }
            catch (Exception) { return 0.0; }
        }

        /// <summary>
        /// Whether non-matching labels are hidden (not just dimmed) after the first typed
        /// char of a label (hot-reload). Default true.
        /// </summary>
        public static bool ReadHideNonMatchingLabels()
        {
            try { EnsureFresh(); return ParseBool(ConfigurationManager.AppSettings["HideNonMatchingLabels"], true); }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// Whether the overlay opens in group view (hot-reload): one dotted box per
        /// first-char label group instead of every pill; typing the group char reveals
        /// only that group's points, labeled by their second char alone. Grid-like
        /// sessions only (all-PointHint); &lt;leader&gt;p toggles it per-session.
        /// Default true.
        /// </summary>
        public static bool ReadGroupViewEnabled()
        {
            try { EnsureFresh(); return ParseBool(ConfigurationManager.AppSettings["GroupViewEnabled"], true); }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// Group key-char font size in px (hot-reload). Returns null when unset or not a
        /// non-negative number so the caller can fall back to "14". "0" means follow the
        /// label font size (HintCanvas treats non-positive as the label size).
        /// </summary>
        public static string ReadGroupFontSize()
        {
            try
            {
                EnsureFresh();
                var raw = ConfigurationManager.AppSettings["GroupFontSize"];
                int v;
                return int.TryParse(raw, out v) && v >= 0 ? raw.Trim() : null;
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return null;
            }
        }

        /// <summary>
        /// The group-view zone-grid spec, "cols x rows" e.g. "5x5" (hot-reload). The
        /// level-1 boxes become a regular cols x rows grid over the session bounds,
        /// keyed by the first cols*rows HintCharacters in scan order, and grid-session
        /// labels are reassigned zone-based (first char = zone key). Blank/invalid (or
        /// cols*rows above the char count) falls back to the derived label-group boxes
        /// and scan-order labels. Default "5x5".
        /// </summary>
        public static string ReadGroupZones()
        {
            const string DefaultZones = "5x5";
            try
            {
                EnsureFresh();
                var raw = ConfigurationManager.AppSettings["GroupZones"];
                return string.IsNullOrWhiteSpace(raw) ? DefaultZones : raw.Trim();
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return DefaultZones;
            }
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
        /// Reads the hint label font family (hot-reload). Returns the bundled default
        /// ("JetBrains Mono NL") when unset/blank, so HintCanvas always gets a value to
        /// resolve. Unlike ReadHintFontSize there is no Options-dialog fallback: the
        /// family is App.config-only.
        /// </summary>
        public static string ReadHintFontFamily()
        {
            const string DefaultFontFamily = "JetBrains Mono NL";
            try
            {
                EnsureFresh();
                var raw = ConfigurationManager.AppSettings["HintFontFamily"];
                return string.IsNullOrWhiteSpace(raw) ? DefaultFontFamily : raw.Trim();
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return DefaultFontFamily;
            }
        }

        /// <summary>
        /// Hint pill fill opacity as 0.0-1.0 (hot-reload). Configured as a percent
        /// (0-100, default 80): softens the vivid yellow so background peeks through,
        /// while the label text stays fully opaque. Bad/missing values fall back to 0.8.
        /// </summary>
        public static double ReadHintPillOpacity()
        {
            try
            {
                EnsureFresh();
                return ParsePercent(ConfigurationManager.AppSettings["HintPillOpacity"], 80) / 100.0;
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return 0.8;
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

        /// <summary>
        /// Uniform in-zone grid step in px (hot-reload). The zone-filled grid is a uniform
        /// grid at this step (so translating it by one zone cell coincides with the next
        /// zone). Default 30.
        /// </summary>
        public static int ReadZoneGridStep()
        {
            return ReadIntSetting("ZoneGridStep", 30);
        }

        /// <summary>
        /// Zone-filled lens width in px (hot-reload); 0 = auto (= the monitor's auto cell
        /// width). The fine grid fills a ZoneWidth x ZoneHeight window centered on the zone.
        /// </summary>
        public static int ReadZoneWidth()
        {
            try { EnsureFresh(); var raw = ConfigurationManager.AppSettings["ZoneWidth"]; int v; return int.TryParse(raw, out v) && v >= 0 ? v : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>Zone-filled lens height in px (hot-reload); 0 = auto. See <see cref="ReadZoneWidth"/>.</summary>
        public static int ReadZoneHeight()
        {
            try { EnsureFresh(); var raw = ConfigurationManager.AppSettings["ZoneHeight"]; int v; return int.TryParse(raw, out v) && v >= 0 ? v : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// Whether type-to-zoom zones are enabled (hot-reload). When true AND the hint
        /// source is Grid + Screen, the overlay opens with a cols×rows grid of large
        /// zone labels; typing one drills into that zone's sub-rectangle (dense + short
        /// labels, escaping the per-monitor HintCharacters² cap). Default false.
        /// </summary>
        public static bool ReadZoneZoomEnabled()
        {
            try
            {
                EnsureFresh();
                return ParseBool(ConfigurationManager.AppSettings["ZoneZoomEnabled"], false);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return false;
            }
        }

        /// <summary>Zone grid column count (hot-reload). Default 3.</summary>
        public static int ReadZoneCols()
        {
            return ReadIntSetting("ZoneCols", 3);
        }

        /// <summary>Zone grid row count (hot-reload). Default 3.</summary>
        public static int ReadZoneRows()
        {
            return ReadIntSetting("ZoneRows", 3);
        }

        /// <summary>
        /// Zone-PICK label font size in px, as a string (hot-reload) -- flows into
        /// HintViewModel.FontSizeReadValue like the normal HintFontSize, and is read
        /// once per overlay. Default "20" (larger than the fine-grid size so the 9 zone
        /// labels are prominent). Returns "20" when unset/invalid.
        /// </summary>
        public static string ReadZoneFontSize()
        {
            try
            {
                EnsureFresh();
                var raw = ConfigurationManager.AppSettings["ZoneFontSize"];
                int v;
                return int.TryParse(raw, out v) && v > 0 ? raw : "20";
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return "20";
            }
        }

        /// <summary>
        /// Whether firing a label in a zone returns to the zone-pick view (hot-reload).
        /// Default false = stay in the zone for repeated nearby clicks in Continuous
        /// mode; set true to re-zoom from the 9-label pick view after every click.
        /// </summary>
        public static bool ReadZoneReturnToPickOnFire()
        {
            try
            {
                EnsureFresh();
                return ParseBool(ConfigurationManager.AppSettings["ZoneZoomReturnToPickOnFire"], false);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return false;
            }
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
        /// How two-pick text-span selection synthesizes the selection. Hot-reload;
        /// default ShiftClick (no button held while typing the second label). Drag is
        /// the fallback where Shift+click is remapped by the target app.
        /// </summary>
        public static TextSelectMethod ReadTextSelectMethod()
        {
            const TextSelectMethod DefaultMethod = TextSelectMethod.ShiftClick;
            try
            {
                EnsureFresh();
                return ParseTextSelectMethod(ConfigurationManager.AppSettings["TextSelectMethod"], DefaultMethod);
            }
            catch (Exception)
            {
                return DefaultMethod;
            }
        }

        /// <summary>
        /// Whether selection actions (Double/Triple/span-select) close the overlay even in
        /// Continuous mode (default true -- the working fix: staying up clears the
        /// selection). Set false with TopmostReassertEnabled=false to test whether the
        /// re-assert timer is what clears a staying-up selection. Hot-reload.
        /// </summary>
        public static bool ReadSelectionActionsClose()
        {
            const bool Default = true;
            try
            {
                EnsureFresh();
                return ParseBool(ConfigurationManager.AppSettings["SelectionActionsClose"], Default);
            }
            catch (Exception)
            {
                return Default;
            }
        }

        /// <summary>
        /// Whether the overlay re-asserts HWND_TOPMOST on a 100ms timer (default true) so
        /// labels stay above a mid-session popup (context menu/dropdown). Set false to test
        /// whether that periodic SetWindowPos is what clears a Continuous-mode selection.
        /// Hot-reload.
        /// </summary>
        public static bool ReadTopmostReassertEnabled()
        {
            const bool Default = true;
            try
            {
                EnsureFresh();
                return ParseBool(ConfigurationManager.AppSettings["TopmostReassertEnabled"], Default);
            }
            catch (Exception)
            {
                return Default;
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
        /// What the dedicated arrow cluster does while the overlay is up (hot-reload):
        /// Passthrough (default) sends arrows to the app beneath (Excel/list/editor focus
        /// nav); Pan pans the labels (legacy). The hjkl chords pan regardless. Default
        /// Passthrough.
        /// </summary>
        public static ArrowKeyBehavior ReadArrowKeyBehavior()
        {
            try
            {
                EnsureFresh();
                return ParseArrowKeyBehavior(ConfigurationManager.AppSettings["ArrowKeyBehavior"], ArrowKeyBehavior.Passthrough);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return ArrowKeyBehavior.Passthrough;
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

        /// <summary>The macro-picker hotkey key (read once at startup). Default OemSemicolon (the ";" key).</summary>
        public static Keys ReadMacroHotkeyKey(Keys fallback)
        {
            try
            {
                EnsureFresh();
                return ParseKeys(ConfigurationManager.AppSettings["MacroHotkeyKey"], fallback);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>The macro-picker hotkey modifiers (read once at startup). Fallback when missing/invalid.</summary>
        public static KeyModifier ReadMacroHotkeyModifier(KeyModifier fallback)
        {
            try
            {
                EnsureFresh();
                return ParseKeyModifiers(ConfigurationManager.AppSettings["MacroHotkeyModifier"], fallback);
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
