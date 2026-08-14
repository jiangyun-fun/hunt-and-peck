using System;
using System.Collections.Generic;
using System.Windows;
using HuntAndPeck.Models;
using HuntAndPeck.Services;
using HuntAndPeck.ViewModels;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class GroupViewServiceTest
    {
        // ---- IsGroupable ----

        [Fact]
        public void IsGroupable_PointHintsWithTwoCharLabels_True()
        {
            var hints = new List<Hint> { P(0, 0), P(0, 30), P(0, 60) };
            var labels = new List<string> { "AA", "AB", "BA" };

            Assert.True(GroupViewService.IsGroupable(hints, labels));
        }

        [Fact]
        public void IsGroupable_AnyNonPointHint_False()
        {
            // Automation / taskbar-merged sessions mix in UIA hints whose tree-order
            // labels are spatially scattered, so group boxes would be meaningless.
            var hints = new List<Hint> { P(0, 0), new NonPointHint(), P(0, 60) };
            var labels = new List<string> { "AA", "AB", "BA" };

            Assert.False(GroupViewService.IsGroupable(hints, labels));
        }

        [Fact]
        public void IsGroupable_AllOneCharLabels_False()
        {
            // An all-1-char session would draw a box per point: noise, not a drill-down.
            var hints = new List<Hint> { P(0, 0), P(0, 30) };
            var labels = new List<string> { "A", "B" };

            Assert.False(GroupViewService.IsGroupable(hints, labels));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void IsGroupable_MissingLabels_False(string empty)
        {
            var hints = new List<Hint> { P(0, 0), P(0, 30) };
            var labels = empty == null ? null : new List<string>();

            Assert.False(GroupViewService.IsGroupable(hints, labels));
        }

        [Fact]
        public void IsGroupable_CountMismatch_False()
        {
            var hints = new List<Hint> { P(0, 0), P(0, 30) };
            var labels = new List<string> { "AA" };

            Assert.False(GroupViewService.IsGroupable(hints, labels));
        }

        // ---- BuildGroupBoxes ----

        [Fact]
        public void BuildGroupBoxes_GroupsByFirstChar_UnionsMemberRects_SortsByKey()
        {
            // Two groups: A = two stacked points (a column chunk), B = one point.
            var hints = new List<HintViewModel>
            {
                Vm("AA", new Rect(100, 10, 8, 8)),
                Vm("AB", new Rect(100, 40, 8, 8)),
                Vm("BA", new Rect(200, 10, 8, 8)),
            };

            var boxes = GroupViewService.BuildGroupBoxes(hints);

            Assert.Equal(2, boxes.Count);
            Assert.Equal('A', boxes[0].Key);
            // A's box is the union of its two member rects: x 100..108, y 10..48.
            Assert.Equal(new Rect(100, 10, 8, 38), boxes[0].Bounds);
            Assert.Equal('B', boxes[1].Key);
            Assert.Equal(new Rect(200, 10, 8, 8), boxes[1].Bounds);
        }

        [Fact]
        public void BuildGroupBoxes_SingleMember_BoxIsExactlyItsRect()
        {
            // Regression guard: the union must NOT be seeded with default(Rect) --
            // (0,0,0,0) is a valid degenerate rect at the ORIGIN, not Rect.Empty, so
            // seeding with it would stretch every box to the top-left corner.
            var hints = new List<HintViewModel> { Vm("KA", new Rect(300, 400, 8, 8)) };

            var boxes = GroupViewService.BuildGroupBoxes(hints);

            var b = Assert.Single(boxes);
            Assert.Equal('K', b.Key);
            Assert.Equal(new Rect(300, 400, 8, 8), b.Bounds);
        }

        [Fact]
        public void BuildGroupBoxes_NullOrEmpty_ReturnsEmptyList()
        {
            Assert.Empty(GroupViewService.BuildGroupBoxes(null));
            Assert.Empty(GroupViewService.BuildGroupBoxes(new List<HintViewModel>()));
        }

        [Fact]
        public void BuildGroupBoxes_SkipsEmptyLabels()
        {
            var hints = new List<HintViewModel>
            {
                Vm(null, new Rect(0, 0, 8, 8)),
                Vm("", new Rect(10, 10, 8, 8)),
                Vm("AA", new Rect(20, 20, 8, 8)),
            };

            var boxes = GroupViewService.BuildGroupBoxes(hints);

            var b = Assert.Single(boxes);
            Assert.Equal('A', b.Key);
        }

        // ---- Zone-grid labeling (TryParseZoneSpec / EffectiveGridCap / TryAssignZoneLabels) ----

        [Theory]
        [InlineData("5x5", 5, 5, true)]
        [InlineData("4X6", 4, 6, true)]
        [InlineData("6*4", 6, 4, true)]
        [InlineData(" 3x2 ", 3, 2, true)]
        [InlineData("", 0, 0, false)]
        [InlineData(null, 0, 0, false)]
        [InlineData("abc", 0, 0, false)]
        [InlineData("5", 0, 0, false)]
        [InlineData("5x", 0, 0, false)]
        [InlineData("1x1", 0, 0, false)]     // a 1x1 spec is meaningless
        [InlineData("0x5", 0, 0, false)]
        [InlineData("-2x5", 0, 0, false)]
        public void TryParseZoneSpec_Theory(string raw, int cols, int rows, bool ok)
        {
            int c, r;
            Assert.Equal(ok, GroupViewService.TryParseZoneSpec(raw, out c, out r));
            if (ok)
            {
                Assert.Equal(cols, c);
                Assert.Equal(rows, r);
            }
        }

        [Fact]
        public void EffectiveGridCap_ValidSpec_ZonesTimesChars()
        {
            int zoneCount;
            // 25 chars x 5x5 zones = 625 (fits: 25 zones <= 25 chars).
            Assert.Equal(625, GroupViewService.EffectiveGridCap(25, "5x5", out zoneCount));
            Assert.Equal(25, zoneCount);

            // 29 chars (punctuation still configured) x 5x5 = 725 < the legacy 841.
            Assert.Equal(725, GroupViewService.EffectiveGridCap(29, "5x5", out zoneCount));
            Assert.Equal(25, zoneCount);
        }

        [Fact]
        public void EffectiveGridCap_InvalidOrOversizedSpec_LegacyCharsSquared()
        {
            int zoneCount;
            Assert.Equal(625, GroupViewService.EffectiveGridCap(25, "", out zoneCount));
            Assert.Equal(0, zoneCount);
            Assert.Equal(841, GroupViewService.EffectiveGridCap(29, "", out zoneCount));
            // 26 zones do not fit a 10-char set (every zone needs its own key char).
            Assert.Equal(100, GroupViewService.EffectiveGridCap(10, "2x13", out zoneCount));
            Assert.Equal(0, zoneCount);
        }

        [Fact]
        public void TryAssignZoneLabels_ZoneKeyFirstChar_SecondCharCyclesInEmissionOrder()
        {
            // 5x5 over a 500x100 bounds: cells 100x20. Zone A (x<100, y<20) gets two
            // points -> AA, AB; zone B -> BA, BB; zone index 5 ('F', row 1 col 0) one
            // point -> "F" alone.
            string chars = "ABCDEFGHIJKLMNOPRSTUVWXYZ";
            var hints = new List<Hint>
            {
                P(10, 5), P(50, 5),      // zone A
                P(110, 5), P(150, 5),    // zone B
                P(10, 25),               // y=25 -> row 1 -> zone index 5 = 'F'
            };

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, new Rect(0, 0, 500, 100),
                5, 5, chars.ToCharArray(), out labels, out boxes);

            Assert.True(ok);
            Assert.Equal(new[] { "AA", "AB", "BA", "BB", "F" }, labels);
            // Boxes: only the occupied zones, in scan order, regular cell rects.
            Assert.Equal(3, boxes.Count);
            Assert.Equal('A', boxes[0].Key);
            Assert.Equal(new Rect(0, 0, 100, 20), boxes[0].Bounds);
            Assert.Equal('B', boxes[1].Key);
            Assert.Equal(new Rect(100, 0, 100, 20), boxes[1].Bounds);
            Assert.Equal('F', boxes[2].Key);
            Assert.Equal(new Rect(0, 20, 100, 20), boxes[2].Bounds);
        }

        [Fact]
        public void TryAssignZoneLabels_BoundaryPoint_LandsInOneDeterminateZone()
        {
            // A point exactly on the A|B boundary (x=100) goes to zone B (index math,
            // clamped -- not Rect.Contains, which would match both cells).
            string chars = "ABCDEFGHIJKLMNOPRSTUVWXYZ";
            var hints = new List<Hint> { P(100, 5) };

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, new Rect(0, 0, 500, 100),
                5, 5, chars.ToCharArray(), out labels, out boxes);

            Assert.True(ok);
            Assert.Equal(new[] { "B" }, labels);    // single point in zone B -> 1-char
            Assert.Single(boxes);
            Assert.Equal('B', boxes[0].Key);
        }

        [Fact]
        public void TryAssignZoneLabels_PointBeyondEdge_ClampsToLastZone()
        {
            string chars = "ABCDEFGHIJKLMNOPRSTUVWXYZ";
            var hints = new List<Hint> { P(490, 95) };

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, new Rect(0, 0, 500, 100),
                5, 5, chars.ToCharArray(), out labels, out boxes);

            Assert.True(ok);
            Assert.Equal(new[] { "Z" }, labels);    // last zone (row 4, col 4) = index 24 = 'Z'
            Assert.Equal(new Rect(400, 80, 100, 20), boxes[0].Bounds);
        }

        [Fact]
        public void TryAssignZoneLabels_ZoneOverflow_ReturnsFalse()
        {
            // 25 chars means a zone can hold at most 25 points; 26 in one zone overflows.
            string chars = "ABCDEFGHIJKLMNOPRSTUVWXYZ";
            var hints = new List<Hint>();
            for (int i = 0; i < 26; i++)
            {
                hints.Add(P(10 + (i % 5) * 10, 2 + (i / 5) * 3));   // all inside zone A
            }

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, new Rect(0, 0, 500, 100),
                5, 5, chars.ToCharArray(), out labels, out boxes);

            Assert.False(ok);
            Assert.Null(labels);
            Assert.Null(boxes);
        }

        [Fact]
        public void TryAssignZoneLabels_DegenerateInputs_ReturnFalse()
        {
            List<string> labels;
            List<GroupHintBox> boxes;
            var bounds = new Rect(0, 0, 500, 100);
            var chars = "ABCDEF".ToCharArray();

            Assert.False(GroupViewService.TryAssignZoneLabels(null, bounds, 5, 5, chars, out labels, out boxes));
            Assert.False(GroupViewService.TryAssignZoneLabels(new List<Hint>(), bounds, 5, 5, chars, out labels, out boxes));
            Assert.False(GroupViewService.TryAssignZoneLabels(
                new List<Hint> { P(10, 10) }, new Rect(0, 0, 0, 0), 5, 5, chars, out labels, out boxes));
        }

        [Fact]
        public void TryAssignZoneLabels_NonZeroOriginBounds_CoordsAreRelative()
        {
            // Regression (on-box 2026-08-14): PointHint rects are RELATIVE to the
            // session bounds (a secondary monitor at Left=1920 stores 0..1920), and
            // boxes must be relative too. Using absolute bounds coords clamped every
            // secondary-monitor point into zone 0 and drew boxes off-canvas.
            string chars = "ABCDEFGHIJKLMNOPRSTUVWXYZ";
            var hints = new List<Hint> { P(10, 5), P(110, 5) };

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, new Rect(1920, 0, 500, 100),
                5, 5, chars.ToCharArray(), out labels, out boxes);

            Assert.True(ok);
            // Relative x=10 -> zone A, x=110 -> zone B; each is a single-point zone,
            // so each is labeled by its key alone (1-char, instant fire).
            Assert.Equal(new[] { "A", "B" }, labels);
            Assert.Equal(2, boxes.Count);
            Assert.Equal(new Rect(0, 0, 100, 20), boxes[0].Bounds);    // relative!
            Assert.Equal(new Rect(100, 0, 100, 20), boxes[1].Bounds);
        }

        // ---- MaxZoneCount / TryGridZoneSpec (grid cap loop support) ----

        [Fact]
        public void MaxZoneCount_ReturnsLargestZone()
        {
            // 3 points in zone A, 1 in zone B -> max 3.
            var hints = new List<Hint> { P(10, 5), P(50, 5), P(90, 5), P(110, 5) };
            Assert.Equal(3, GroupViewService.MaxZoneCount(hints, new Rect(0, 0, 500, 100), 5, 5));
        }

        [Fact]
        public void MaxZoneCount_PointBeyondEdge_ClampsIntoLastZone()
        {
            var hints = new List<Hint> { P(10, 5), P(490, 95) };
            Assert.Equal(1, GroupViewService.MaxZoneCount(hints, new Rect(0, 0, 500, 100), 5, 5));
        }

        [Fact]
        public void MaxZoneCount_Degenerate_ReturnsZero()
        {
            Assert.Equal(0, GroupViewService.MaxZoneCount(null, new Rect(0, 0, 500, 100), 5, 5));
            Assert.Equal(0, GroupViewService.MaxZoneCount(
                new List<Hint>(), new Rect(0, 0, 500, 100), 5, 5));
            Assert.Equal(0, GroupViewService.MaxZoneCount(
                new List<Hint> { P(10, 5) }, new Rect(0, 0, 0, 0), 5, 5));
        }

        [Fact]
        public void TryGridZoneSpec_FitsCharSet()
        {
            int cols, rows;
            Assert.True(GroupViewService.TryGridZoneSpec("5x5", 25, out cols, out rows));
            Assert.Equal(5, cols);
            Assert.Equal(5, rows);
            // 26 zones do not fit a 10-char set (every zone needs its own key char).
            Assert.False(GroupViewService.TryGridZoneSpec("2x13", 10, out cols, out rows));
            Assert.False(GroupViewService.TryGridZoneSpec("bogus", 25, out cols, out rows));
        }

        // ---- helpers ----

        private static PointHint P(double x, double y)
        {
            return new PointHint(IntPtr.Zero, new Rect(x, y, 8, 8), new Point(x + 4, y + 4));
        }

        private static HintViewModel Vm(string label, Rect bounds)
        {
            return new HintViewModel(new PointHint(IntPtr.Zero, bounds, new Point(bounds.X, bounds.Y)), "14", null)
            {
                Label = label
            };
        }

        /// <summary>A minimal non-PointHint stand-in (like the UI Automation hints).</summary>
        private sealed class NonPointHint : Hint
        {
            public NonPointHint() : base(IntPtr.Zero, new Rect(0, 0, 10, 10)) { }

            public override void Invoke() { }
        }
    }
}
