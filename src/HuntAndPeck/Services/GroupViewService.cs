using System;
using System.Collections.Generic;
using System.Windows;
using HuntAndPeck.Models;
using HuntAndPeck.ViewModels;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// Group-view helpers (opt-out via GroupViewEnabled; grid-like sessions only).
    /// The overlay opens showing ONE dotted box per first-char label group instead of
    /// every pill; typing the group's char reveals only that group's points, labeled
    /// by their second char alone.
    /// <para>
    /// Why groups are spatially coherent: <see cref="HintLabelService"/> ordinal-sorts
    /// hint strings so consecutive labels share their first char, and labels are
    /// assigned to hints in emission order — grid points are emitted region-by-region,
    /// column-major within a region (<c>UiAutomationHintProviderService.GenerateGridPoints</c>
    /// / <c>FillRegion</c>). So each first char names a contiguous chunk of the grid and
    /// its bounding box is a meaningful region.
    /// </para>
    /// <para>
    /// Pure (unit-tested); mirrors <see cref="ZoneService"/>.
    /// </para>
    /// </summary>
    internal static class GroupViewService
    {
        /// <summary>
        /// True when the session can use the group view: every hint is a
        /// <see cref="PointHint"/> (a grid-like session — Automation and
        /// taskbar-merged sessions mix in UI Automation hints, whose tree-order
        /// labels are spatially scattered, so their group boxes would be huge and
        /// meaningless), and at least one label is 2 chars (an all-1-char session
        /// would draw a box per point — noise, not a drill-down).
        /// Pure.
        /// </summary>
        public static bool IsGroupable(IList<Hint> hints, IList<string> labels)
        {
            if (hints == null || labels == null || hints.Count == 0 || labels.Count != hints.Count)
            {
                return false;
            }

            bool anyTwoChar = false;
            for (int i = 0; i < hints.Count; i++)
            {
                if (!(hints[i] is PointHint))
                {
                    return false;
                }
                var label = labels[i];
                if (label != null && label.Length >= 2)
                {
                    anyTwoChar = true;
                }
            }
            return anyTwoChar;
        }

        /// <summary>
        /// Builds one <see cref="GroupHintBox"/> per first-char label group: the union of
        /// the member hints' bounding rectangles, ordered by key char (ordinal). Hints
        /// with a null/empty label are skipped. Pure. This is the v1/fallback shape
        /// (irregular boxes around label groups); the zone-grid shape is
        /// <see cref="TryAssignZoneLabels"/>.
        /// </summary>
        public static List<GroupHintBox> BuildGroupBoxes(IList<HintViewModel> hints)
        {
            var boxes = new List<GroupHintBox>();
            if (hints == null || hints.Count == 0)
            {
                return boxes;
            }

            // Keyed by group char; insertion order is emission order, but the final
            // list is sorted by char so the result is deterministic regardless of the
            // session's point order.
            // NOTE: the first member initializes the union. default(Rect) is (0,0,0,0)
            // -- a valid degenerate rect at the ORIGIN, not Rect.Empty -- so seeding
            // with it would stretch every box to the top-left corner.
            var byChar = new Dictionary<char, Rect>();
            foreach (var h in hints)
            {
                var label = h.Label;
                if (string.IsNullOrEmpty(label) || h.Hint.BoundingRectangle.IsEmpty)
                {
                    continue;
                }
                var key = char.ToUpperInvariant(label[0]);
                Rect found;
                if (byChar.TryGetValue(key, out found))
                {
                    byChar[key] = Rect.Union(found, h.Hint.BoundingRectangle);
                }
                else
                {
                    byChar[key] = h.Hint.BoundingRectangle;
                }
            }

            foreach (var kv in byChar)
            {
                boxes.Add(new GroupHintBox(kv.Key, kv.Value));
            }
            boxes.Sort((a, b) => a.Key.CompareTo(b.Key));
            return boxes;
        }

        // -------- Zone-grid labeling (v2: fixed cols x rows grid, all-letter labels) --------

        /// <summary>
        /// Parses a <c>GroupZones</c> spec: "<c>cols x rows</c>" (separator <c>x</c>/
        /// <c>X</c>/<c>*</c>, e.g. "5x5"). Both dims must be &gt;= 1 and their product
        /// &gt;= 2 (a 1x1 spec is meaningless). Pure.
        /// </summary>
        public static bool TryParseZoneSpec(string raw, out int cols, out int rows)
        {
            cols = 0;
            rows = 0;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }
            var parts = raw.Trim().Split(new[] { 'x', 'X', '*' });
            if (parts.Length != 2)
            {
                return false;
            }
            int c, r;
            if (!int.TryParse(parts[0].Trim(), out c) || !int.TryParse(parts[1].Trim(), out r))
            {
                return false;
            }
            if (c < 1 || r < 1 || c * r < 2)
            {
                return false;
            }
            cols = c;
            rows = r;
            return true;
        }

        /// <summary>
        /// Parses a GroupZones spec AND checks it fits the char set (every zone needs
        /// its own key char): true with cols/rows out when valid and
        /// cols*rows &lt;= charCount. Pure; the provider and the view-model both gate
        /// zone-grid labeling on this.
        /// </summary>
        public static bool TryGridZoneSpec(string raw, int charCount, out int cols, out int rows)
        {
            return TryParseZoneSpec(raw, out cols, out rows) && cols * rows <= charCount;
        }

        /// <summary>
        /// Derives the PER-ZONE point-grid dimensions for zone-aligned generation: the
        /// largest inCols x inRows with near-square cells (inCols/inRows tracks the
        /// bounds' aspect) whose product stays within the second-char budget. 16:9 with
        /// 25 letters gives 6x4 = 24. A density floor clamps each axis so small bounds
        /// (e.g. a 400x300 window) do not get an absurd point lattice: never denser
        /// than <paramref name="minStep"/> px between points in either axis. Pure.
        /// </summary>
        public static void TryDeriveZoneGrid(
            int charCount, double width, double height, double minStep,
            int zoneCols, int zoneRows, out int inCols, out int inRows)
        {
            if (charCount < 1) charCount = 1;
            if (minStep < 1) minStep = 1;
            if (width < 1) width = 1;
            if (height < 1) height = 1;
            double aspect = width / height;
            inCols = (int)Math.Floor(Math.Sqrt(charCount * aspect));
            if (inCols < 1) inCols = 1;
            inRows = charCount / inCols;
            if (inRows < 1) inRows = 1;
            // Density floor: at most one point per minStep px within a zone cell.
            int maxCols = Math.Max(1, (int)(width / (zoneCols * minStep)));
            int maxRows = Math.Max(1, (int)(height / (zoneRows * minStep)));
            if (inCols > maxCols) inCols = maxCols;
            if (inRows > maxRows) inRows = maxRows;
        }

        /// <summary>
        /// The union of the hints' bounding rectangles -- the region the points actually
        /// OCCUPY, in the same (session-relative) coordinate space they render in.
        /// Zones tile this extent, not the session bounds: quadrant sessions set their
        /// bounds to the FULL monitor (for the full-screen overlay) while their points
        /// cluster in one quarter, so bounds-based slicing put ~4x the points in the
        /// few occupied monitor-zones and overflowed every quadrant session into the
        /// v1 fallback. The first hint initializes the union (never default(Rect),
        /// which is a degenerate rect at the ORIGIN, not Rect.Empty). Returns
        /// <see cref="Rect.Empty"/> when there are no hints. Pure.
        /// </summary>
        public static Rect HintExtent(IList<Hint> hints)
        {
            if (hints == null || hints.Count == 0)
            {
                return Rect.Empty;
            }
            var extent = hints[0].BoundingRectangle;
            for (int i = 1; i < hints.Count; i++)
            {
                var br = hints[i].BoundingRectangle;
                if (!br.IsEmpty)
                {
                    extent = extent.IsEmpty ? br : Rect.Union(extent, br);
                }
            }
            return extent;
        }

        /// <summary>
        /// Zone-grid label assignment: slices the hints' EXTENT
        /// (<see cref="HintExtent"/> -- the region the points occupy, in their own
        /// render space) into cols x rows cells (scan order), keys zone i with
        /// <paramref name="chars"/>[i], and labels each point
        /// <c>zoneChar + chars[j]</c> where j cycles through the char set in emission
        /// order within the zone. A zone holding exactly ONE point gets a 1-char label
        /// (typing the zone char fires it immediately). Labels are unique and
        /// prefix-free.
        /// <para>
        /// WHY THE EXTENT, NOT THE SESSION BOUNDS: PointHint rects are relative to the
        /// session and HintCanvas renders in that space, so the extent is the exact
        /// region the labels live in -- and quadrant sessions set their bounds to the
        /// FULL monitor while their points cluster in one quarter, so bounds-based
        /// slicing overflowed every quadrant session into the fallback.
        /// </para>
        /// <para>Returns false (null outputs) when any zone would need more second
        /// chars than the set provides (overflow; dense layouts concentrate points) --
        /// the caller falls back to scan-order labels. Point-to-zone lookup uses cell
        /// INDEX math with clamping, not Rect.Contains, so a point exactly on a zone
        /// boundary lands in one determinate zone.</para>
        /// Pure. <paramref name="boxes"/> receives one regular box per NON-EMPTY zone
        /// (the fixed grid look), in the points' own coordinate space.
        /// </summary>
        public static bool TryAssignZoneLabels(
            IList<Hint> hints, int cols, int rows, char[] chars,
            out List<string> labels, out List<GroupHintBox> boxes)
        {
            labels = null;
            boxes = null;
            var extent = HintExtent(hints);
            if (extent.IsEmpty || chars == null || chars.Length < 2 || cols < 1 || rows < 1)
            {
                return false;
            }

            double cellW = extent.Width / cols;
            double cellH = extent.Height / rows;
            int zoneCount = cols * rows;

            // Pass 1: zone index per hint + per-zone occupancy.
            var zoneOf = new int[hints.Count];
            var counts = new int[zoneCount];
            for (int i = 0; i < hints.Count; i++)
            {
                var br = hints[i].BoundingRectangle;
                int c = (int)((br.Left - extent.Left) / cellW);
                int r = (int)((br.Top - extent.Top) / cellH);
                if (c < 0) c = 0; else if (c > cols - 1) c = cols - 1;
                if (r < 0) r = 0; else if (r > rows - 1) r = rows - 1;
                int z = r * cols + c;
                zoneOf[i] = z;
                counts[z]++;
            }
            for (int z = 0; z < zoneCount; z++)
            {
                if (counts[z] > chars.Length)
                {
                    return false;   // overflow: not enough second chars for this zone
                }
            }

            // Pass 2: assign labels (emission order within each zone).
            var next = new int[zoneCount];
            labels = new List<string>(new string[hints.Count]);
            for (int i = 0; i < hints.Count; i++)
            {
                int z = zoneOf[i];
                if (counts[z] == 1)
                {
                    labels[i] = chars[z].ToString();                    // instant fire
                }
                else
                {
                    labels[i] = string.Concat(chars[z].ToString(), chars[next[z]].ToString());
                    next[z]++;
                }
            }

            // Boxes: one regular cell per non-empty zone (scan order, in the points' space).
            boxes = new List<GroupHintBox>();
            for (int z = 0; z < zoneCount; z++)
            {
                if (counts[z] > 0)
                {
                    int c = z % cols;
                    int r = z / cols;
                    boxes.Add(new GroupHintBox(chars[z], new Rect(
                        extent.Left + c * cellW, extent.Top + r * cellH, cellW, cellH)));
                }
            }
            return true;
        }
    }
}
