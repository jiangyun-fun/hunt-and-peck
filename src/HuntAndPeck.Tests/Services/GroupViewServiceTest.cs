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

        // ---- Zone-grid labeling (TryParseZoneSpec / TryDeriveZoneGrid / TryAssignZoneLabels) ----

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
        public void TryAssignZoneLabels_ZonesTileThePointExtent()
        {
            // 2x1 zones over the EXTENT of the points (0,0)-(28,8): cellW = 14.
            // Zone A: x=0,10 -> labels AA, AB (emission order); zone B: x=20 single
            // -> 1-char "B" (instant fire).
            var hints = new List<Hint> { P(0, 0), P(10, 0), P(20, 0) };

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, 2, 1, "AB".ToCharArray(),
                out labels, out boxes);

            Assert.True(ok);
            Assert.Equal(new[] { "AA", "AB", "B" }, labels);
            Assert.Equal(2, boxes.Count);
            Assert.Equal('A', boxes[0].Key);
            Assert.Equal(new Rect(0, 0, 14, 8), boxes[0].Bounds);
            Assert.Equal('B', boxes[1].Key);
            Assert.Equal(new Rect(14, 0, 14, 8), boxes[1].Bounds);
        }

        [Fact]
        public void TryAssignZoneLabels_ClusterAwayFromOrigin_ZonesTileTheCluster()
        {
            // Regression (on-box 2026-08-14, quadrant hotkeys): quadrant sessions set
            // OwningWindowBounds to the FULL monitor while their points cluster in one
            // quarter, so bounds-based slicing overflowed every occupied monitor-zone.
            // Zones must tile the cluster's extent (960,540)-(1048,548): cellW = 44.
            var hints = new List<Hint> { P(960, 540), P(1000, 540), P(1040, 540) };

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, 2, 1, "AB".ToCharArray(),
                out labels, out boxes);

            Assert.True(ok);
            Assert.Equal(new[] { "AA", "AB", "B" }, labels);
            Assert.Equal(2, boxes.Count);
            Assert.Equal(new Rect(960, 540, 44, 8), boxes[0].Bounds);   // at the cluster
            Assert.Equal(new Rect(1004, 540, 44, 8), boxes[1].Bounds);
        }

        [Fact]
        public void TryAssignZoneLabels_BoundaryPoint_LandsInOneDeterminateZone()
        {
            // A point exactly on the A|B boundary goes to zone B (index math with
            // clamping -- not Rect.Contains, which would match both cells).
            var hints = new List<Hint> { P(0, 0), P(14, 0) };   // extent (0,0,22,8), cellW 11

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, 2, 1, "AB".ToCharArray(),
                out labels, out boxes);

            Assert.True(ok);
            Assert.Equal(new[] { "A", "B" }, labels);   // both single-point zones
        }

        [Fact]
        public void TryAssignZoneLabels_ZoneOverflow_ReturnsFalse()
        {
            // chars "AB" gives a 2-point budget per zone; 3 points in zone A overflows
            // (extent (0,0,12,8), cellW 6 -> x=0,2,4 all in zone A).
            var hints = new List<Hint> { P(0, 0), P(2, 0), P(4, 0) };

            List<string> labels;
            List<GroupHintBox> boxes;
            bool ok = GroupViewService.TryAssignZoneLabels(hints, 2, 1, "AB".ToCharArray(),
                out labels, out boxes);

            Assert.False(ok);
            Assert.Null(labels);
            Assert.Null(boxes);
        }

        [Fact]
        public void TryAssignZoneLabels_DegenerateInputs_ReturnFalse()
        {
            List<string> labels;
            List<GroupHintBox> boxes;
            Assert.False(GroupViewService.TryAssignZoneLabels(null, 5, 5,
                "AB".ToCharArray(), out labels, out boxes));
            Assert.False(GroupViewService.TryAssignZoneLabels(new List<Hint>(), 5, 5,
                "AB".ToCharArray(), out labels, out boxes));
            Assert.False(GroupViewService.TryAssignZoneLabels(
                new List<Hint> { P(10, 5) }, 5, 5, "A".ToCharArray(), out labels, out boxes));
        }

        // ---- HintExtent ----

        [Fact]
        public void HintExtent_UnionOfRects_FirstMemberInitializes()
        {
            // Union must not be seeded with default(Rect) -- (0,0,0,0) is a degenerate
            // rect at the ORIGIN, not Rect.Empty, so seeding with it would stretch the
            // extent to the top-left corner.
            var hints = new List<Hint> { P(300, 400), P(340, 420) };
            Assert.Equal(new Rect(300, 400, 48, 28), GroupViewService.HintExtent(hints));

            Assert.Equal(new Rect(300, 400, 8, 8),
                GroupViewService.HintExtent(new List<Hint> { P(300, 400) }));
        }

        [Fact]
        public void HintExtent_NullOrEmpty_IsEmpty()
        {
            Assert.True(GroupViewService.HintExtent(null).IsEmpty);
            Assert.True(GroupViewService.HintExtent(new List<Hint>()).IsEmpty);
        }

        // ---- TryGridZoneSpec (spec fits the char set) ----

        // ---- TryDeriveZoneGrid (zone-aligned per-zone dimensions) ----

        [Fact]
        public void TryDeriveZoneGrid_Landscape16x9_25Chars_Is6x4()
        {
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 1920, 1080, 5, 5, out c, out r);
            Assert.Equal(6, c);   // floor(sqrt(25 * 16/9)) = 6
            Assert.Equal(4, r);   // 25 / 6 = 4 -> 24 points per zone, <= 25 budget
        }

        [Fact]
        public void TryDeriveZoneGrid_Quadrant960x540_Is6x4()
        {
            // Regression (on-box 2026-08-14): a path-dependent density floor (the
            // quadrant grid's ZoneGridStep=30) clamped the quadrant's rows 4->3 while
            // the main hotkey kept 6x4. The floor is now the constant
            // MinZonePointSpacing (20px), so every 16:9 bounds gets the same shape.
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 960, 540, 5, 5, out c, out r);
            Assert.Equal(6, c);   // maxCols = floor(960/100) = 9 >= 6: no clamp
            Assert.Equal(4, r);   // maxRows = floor(540/100) = 5 >= 4: no clamp
        }

        [Fact]
        public void TryDeriveZoneGrid_Square_Is5x5()
        {
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 1000, 1000, 5, 5, out c, out r);
            Assert.Equal(5, c);
            Assert.Equal(5, r);
        }

        [Fact]
        public void TryDeriveZoneGrid_Portrait_TracksAspect()
        {
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 1080, 1920, 5, 5, out c, out r);
            Assert.Equal(3, c);
            Assert.Equal(8, r);
        }

        [Fact]
        public void TryDeriveZoneGrid_SmallBounds_LegibilityFloorClamps()
        {
            // A 400x300 window must not get a 30x20 lattice; the constant 20px floor
            // clamps each axis (still uniform: every zone gets the same 4x3).
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 400, 300, 5, 5, out c, out r);
            Assert.Equal(4, c);   // floor(400 / (5 * 20)) = 4
            Assert.Equal(3, r);   // floor(300 / (5 * 20)) = 3
        }

        [Fact]
        public void TryDeriveZoneGrid_SmallCharCount_ClampsAtOne()
        {
            int c, r;
            GroupViewService.TryDeriveZoneGrid(2, 1920, 1080, 5, 5, out c, out r);
            Assert.Equal(1, c);
            Assert.Equal(2, r);
        }

        // ---- TryGridZoneSpec (spec fits the char set) ----

        // ---- TryDeriveZoneGrid (zone-aligned per-zone dimensions) ----

        [Fact]
        public void TryDeriveZoneGrid_Landscape16x9_25Chars_Is6x4()
        {
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 1920, 1080, 40, 5, 5, out c, out r);
            Assert.Equal(6, c);   // floor(sqrt(25 * 16/9)) = 6
            Assert.Equal(4, r);   // 25 / 6 = 4 -> 24 points per zone, <= 25 budget
        }

        [Fact]
        public void TryDeriveZoneGrid_Square_Is5x5()
        {
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 1000, 1000, 40, 5, 5, out c, out r);
            Assert.Equal(5, c);
            Assert.Equal(5, r);
        }

        [Fact]
        public void TryDeriveZoneGrid_Portrait_TracksAspect()
        {
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 1080, 1920, 40, 5, 5, out c, out r);
            Assert.Equal(3, c);
            Assert.Equal(8, r);
        }

        [Fact]
        public void TryDeriveZoneGrid_SmallBounds_DensityFloorClamps()
        {
            // A 400x300 window must not get a 30x20 lattice; the minStep floor clamps
            // each axis to at most width/(zoneCols*step) point columns per zone.
            int c, r;
            GroupViewService.TryDeriveZoneGrid(25, 400, 300, 40, 5, 5, out c, out r);
            Assert.Equal(2, c);
            Assert.Equal(1, r);
        }

        [Fact]
        public void TryDeriveZoneGrid_SmallCharCount_ClampsAtOne()
        {
            int c, r;
            GroupViewService.TryDeriveZoneGrid(2, 1920, 1080, 40, 5, 5, out c, out r);
            Assert.Equal(1, c);
            Assert.Equal(2, r);
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
