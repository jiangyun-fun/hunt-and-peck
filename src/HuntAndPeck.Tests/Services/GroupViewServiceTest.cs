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
