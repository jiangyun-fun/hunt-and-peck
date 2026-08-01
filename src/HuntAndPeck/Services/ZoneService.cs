using System;
using System.Collections.Generic;
using System.Windows;
using HuntAndPeck.Models;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// Type-to-zoom zone helpers for the Grid + Screen overlay (opt-in via
    /// ZoneZoomEnabled). The overlay opens showing one large label per zone (a
    /// cols×rows grid over the monitor); typing a zone label drills into that zone's
    /// sub-rectangle, which is then filled with the fine grid by
    /// <see cref="IHintProviderService.EnumGridHintsForBounds(IntPtr, Rect, GridLayout)"/>.
    /// <para>
    /// Why: a single zone holds ~1/(cols*rows) of the monitor's points, so it sits
    /// far under the HintCharacters² cap and the configured fine step survives
    /// uncapped — dense labels AND more one-char labels than a full-screen grid.
    /// </para>
    /// <para>
    /// Pure where possible (unit-tested). <see cref="BuildPickSession"/> constructs
    /// synthetic <see cref="PointHint"/>s mirroring
    /// <see cref="UiAutomationHintProviderService"/>'s grid-point construction.
    /// </para>
    /// </summary>
    internal static class ZoneService
    {
        /// <summary>
        /// Slices a monitor rectangle into cols*rows equal cells, scan order
        /// left-to-right then top-to-bottom (zone 0 = top-left cell). Non-positive
        /// cols/rows are clamped to 1 so a degenerate config never yields an empty
        /// array. Pure.
        /// </summary>
        public static Rect[] SliceIntoZones(Rect monitor, int cols, int rows)
        {
            if (cols <= 0) cols = 1;
            if (rows <= 0) rows = 1;
            double cellW = monitor.Width / cols;
            double cellH = monitor.Height / rows;
            var zones = new Rect[cols * rows];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    zones[r * cols + c] = new Rect(
                        monitor.Left + c * cellW,
                        monitor.Top + r * cellH,
                        cellW, cellH);
                }
            }
            return zones;
        }

        /// <summary>
        /// A rectangle of size <paramref name="width"/>×<paramref name="height"/> centered
        /// on <paramref name="center"/>. Clamps a negative size to 0. Pure. This is the
        /// zone-filled fine-grid lens: a uniform grid generated inside it can be panned by
        /// one cell-width to coincide with the next zone.
        /// </summary>
        public static Rect CenteredLens(Point center, double width, double height)
        {
            if (width < 0) width = 0;
            if (height < 0) height = 0;
            return new Rect(center.X - width / 2.0, center.Y - height / 2.0, width, height);
        }

        /// <summary>
        /// Builds a char → zone-index map from the zone-pick label list
        /// (<c>label[i][0] → i</c>, uppercased). The labels are the single-char zone
        /// labels from <see cref="HintLabelService.GetHintStrings"/>, so each first
        /// char is unique. Pure.
        /// </summary>
        public static Dictionary<char, int> LabelToIndexMap(IList<string> labels)
        {
            var map = new Dictionary<char, int>(labels.Count);
            for (int i = 0; i < labels.Count; i++)
            {
                var s = labels[i];
                if (!string.IsNullOrEmpty(s))
                {
                    map[char.ToUpperInvariant(s[0])] = i;
                }
            }
            return map;
        }

        /// <summary>
        /// Builds the zone-PICK <see cref="HintSession"/>: one synthetic
        /// <see cref="PointHint"/> at each zone's center (absolute screen coords),
        /// <see cref="HintSession.OwningWindowBounds"/> = the full monitor. Mirrors
        /// <see cref="UiAutomationHintProviderService"/>'s grid-point construction: the
        /// PointHint's BoundingRectangle is relative to the monitor (its Left/Top IS
        /// the cursor target; HintCanvas centers the pill on it), and the screen point
        /// is absolute so <c>SetCursorPos</c> lands correctly. <paramref name="box"/>
        /// is the (functionally irrelevant for PointHint rendering) bounds size;
        /// rendering positions the pill from the text size, not the bounds.
        /// </summary>
        public static HintSession BuildPickSession(IntPtr hWnd, Rect monitor, int cols, int rows, double box)
        {
            if (box <= 0) box = 1;
            var zones = SliceIntoZones(monitor, cols, rows);
            var hints = new List<Hint>(zones.Length);
            foreach (var z in zones)
            {
                double cx = z.Left + z.Width / 2.0;
                double cy = z.Top + z.Height / 2.0;
                double relX = cx - monitor.Left;
                double relY = cy - monitor.Top;
                hints.Add(new PointHint(hWnd, new Rect(relX, relY, box, box), new Point(cx, cy)));
            }
            return new HintSession
            {
                Hints = hints,
                OwningWindow = hWnd,
                OwningWindowBounds = monitor
            };
        }
    }
}
