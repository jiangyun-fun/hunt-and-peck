using System;
using System.Collections.Generic;
using System.Configuration;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// One grid geometry preset: the five knobs <see cref="UiAutomationHintProviderService"/>
    /// reads to build a Grid-mode session. A list of these (the <c>GridLayouts</c>
    /// appSetting) lets the user keep several grid shapes and cycle them live with the
    /// <c>;</c> key while the overlay is up. Value-like: fields are set once at parse
    /// time and not mutated afterward.
    /// </summary>
    public sealed class GridLayout
    {
        // Defaults mirror the grid builder's absent-key behavior (see the private
        // ReadIntSetting calls the builder used before layouts existed), so a preset
        // that omits a field matches the legacy "key absent" outcome.
        internal const int DefaultEdgeStep = 60;
        internal const int DefaultCenterStep = 160;
        internal const int DefaultInset = 10;
        internal const int DefaultBandPercent = 15;
        internal const string DefaultDenseRegions = "Left,Top,TR,BR,Center";

        /// <summary>Point spacing (px) in dense regions.</summary>
        public int EdgeStep;
        /// <summary>Point spacing (px) in the sparse center.</summary>
        public int CenterStep;
        /// <summary>Margin (px) between the grid and each edge.</summary>
        public int Inset;
        /// <summary>Edge band thickness as a percent (0-100) of the window per edge.</summary>
        public int BandPercent;
        /// <summary>Comma-separated dense regions (Left,Top,Right,Bottom,TL,TR,BL,BR,Center).</summary>
        public string DenseRegions;

        /// <summary>
        /// Reads the five legacy flat keys (GridEdgeStep, GridCenterStep, GridInset,
        /// GridEdgeBandPercent, GridDenseRegions). Used when no GridLayouts preset list
        /// is configured, so geometry is identical to the pre-layout behavior. BandPercent
        /// uses the positive-only parse to match the legacy builder (a 0 band fell back to
        /// the default then).
        /// </summary>
        public static GridLayout FromFlatConfig()
        {
            OverlayActionConfig.EnsureFresh();
            return new GridLayout
            {
                EdgeStep = OverlayActionConfig.ParseInt(ConfigurationManager.AppSettings["GridEdgeStep"], DefaultEdgeStep),
                CenterStep = OverlayActionConfig.ParseInt(ConfigurationManager.AppSettings["GridCenterStep"], DefaultCenterStep),
                Inset = OverlayActionConfig.ParseInt(ConfigurationManager.AppSettings["GridInset"], DefaultInset),
                BandPercent = OverlayActionConfig.ParseInt(ConfigurationManager.AppSettings["GridEdgeBandPercent"], DefaultBandPercent),
                DenseRegions = ConfigurationManager.AppSettings["GridDenseRegions"] ?? DefaultDenseRegions,
            };
        }
    }

    /// <summary>
    /// Parses and persists the <c>GridLayouts</c> / <c>ActiveLayout</c> appSettings.
    /// Format: layouts separated by <c>||</c>, each layout's fields by <c>|</c>:
    /// <c>edgeStep|centerStep|inset|bandPercent|denseRegions</c>. The regions field may
    /// contain commas. Mirrors the codebase's split-parse idiom (see
    /// <see cref="OverlayActionConfig.ParseClickActionOrder"/>). Parsing is split into a
    /// pure method (unit-tested) and ConfigurationManager wrappers that fall back to safe
    /// defaults so a malformed config never breaks the app.
    /// </summary>
    public static class GridLayoutConfig
    {
        /// <summary>
        /// The shipped two-preset default: layout 1 is the current dense grid; layout 2
        /// is a uniform grid (Center only, equal steps) that fills the screen.
        /// </summary>
        public const string DefaultGridLayouts =
            "30|50|10|15|Left,Top,TR,BR,Center || 40|40|10|0|Center";

        /// <summary>
        /// Pure parse of a GridLayouts string. Layouts are separated by <c>||</c>;
        /// each layout's fields by <c>|</c>. Numeric fields fall back to defaults when
        /// blank/non-positive (bandPercent also allows 0 for a uniform/no-edge layout);
        /// the regions field is everything from field 5 onward (rejoined by <c>|</c>) so a
        /// stray pipe does not truncate it. A blank/whitespace <paramref name="raw"/>
        /// returns an empty list so the caller can fall back to
        /// <see cref="GridLayout.FromFlatConfig"/> (today's behavior).
        /// </summary>
        public static IList<GridLayout> ParseGridLayouts(string raw)
        {
            var result = new List<GridLayout>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return result;
            }

            var parts = raw.Split(new[] { "||" }, StringSplitOptions.None);
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue; // skip empty layout slots (e.g. a trailing "||")
                }

                var fields = part.Split('|');

                // Regions = field 5 onward (index 4+), rejoined so a stray '|' inside it
                // does not truncate the region list. Blank/missing -> default.
                string regions = GridLayout.DefaultDenseRegions;
                if (fields.Length > 4)
                {
                    var joined = string.Join("|", fields, 4, fields.Length - 4);
                    if (!string.IsNullOrWhiteSpace(joined))
                    {
                        regions = joined;
                    }
                }

                result.Add(new GridLayout
                {
                    EdgeStep = FieldInt(fields, 0, GridLayout.DefaultEdgeStep),
                    CenterStep = FieldInt(fields, 1, GridLayout.DefaultCenterStep),
                    Inset = FieldInt(fields, 2, GridLayout.DefaultInset),
                    // bandPercent allows 0 (a uniform / no-edge layout), so use ParsePercent
                    // (0-100) rather than the positive-only ParseInt used for the steps.
                    BandPercent = FieldPercent(fields, 3, GridLayout.DefaultBandPercent),
                    DenseRegions = regions,
                });
            }

            return result;
        }

        /// <summary>
        /// Wraps/clamps an active-layout index into [0, count). Pure + unit-tested.
        /// count &lt;= 1 always yields 0 (no cycling).
        /// </summary>
        public static int ClampActiveLayout(int index, int count)
        {
            if (count <= 1)
            {
                return 0;
            }
            int m = index % count;
            return m < 0 ? m + count : m;
        }

        /// <summary>
        /// Reads GridLayouts from hap.exe.config (hot-reload via EnsureFresh). Returns an
        /// empty list when unset/blank so the caller falls back to the flat keys.
        /// </summary>
        public static IList<GridLayout> ReadGridLayouts()
        {
            try
            {
                OverlayActionConfig.EnsureFresh();
                return ParseGridLayouts(ConfigurationManager.AppSettings["GridLayouts"]);
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return new List<GridLayout>();
            }
        }

        /// <summary>
        /// Reads the persisted active-layout index and wraps/clamps it into [0, count).
        /// Returns 0 when count &lt;= 1, unset, or invalid.
        /// </summary>
        public static int ReadActiveLayout(int count)
        {
            if (count <= 1)
            {
                return 0;
            }
            try
            {
                OverlayActionConfig.EnsureFresh();
                var raw = ConfigurationManager.AppSettings["ActiveLayout"];
                int idx;
                if (int.TryParse(raw, out idx))
                {
                    return ClampActiveLayout(idx, count);
                }
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
            }
            return 0;
        }

        /// <summary>
        /// Persists the active-layout index so the choice survives a restart. Uses the
        /// same write path as the Options dialog (WriteSetting bumps EnsureFresh's mtime
        /// so the next read sees it).
        /// </summary>
        public static void WriteActiveLayout(int index)
        {
            OverlayActionConfig.WriteSetting("ActiveLayout", index.ToString());
        }

        private static int FieldInt(string[] fields, int index, int defaultValue)
        {
            if (index < fields.Length)
            {
                return OverlayActionConfig.ParseInt(fields[index], defaultValue);
            }
            return defaultValue;
        }

        private static int FieldPercent(string[] fields, int index, int defaultValue)
        {
            if (index < fields.Length)
            {
                return OverlayActionConfig.ParsePercent(fields[index], defaultValue);
            }
            return defaultValue;
        }
    }
}
