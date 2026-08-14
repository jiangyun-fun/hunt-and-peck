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
        /// with a null/empty label are skipped. Pure.
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
    }
}
