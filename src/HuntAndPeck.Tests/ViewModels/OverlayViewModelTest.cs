using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using HuntAndPeck.Models;
using HuntAndPeck.Services;
using HuntAndPeck.ViewModels;
using Xunit;

namespace HuntAndPeck.Tests.ViewModels
{
    public class OverlayViewModelTest
    {
        [Fact]
        public void GroupView_GridSession_DefaultsOn_AndToggles()
        {
            // 40 PointHints in a row: labels are 1-2 chars (capacity > 40), so the
            // session is groupable and GroupViewEnabled defaults true.
            var hints = new List<Hint>();
            for (int i = 0; i < 40; i++)
            {
                hints.Add(new PointHint(IntPtr.Zero, new Rect(i * 10, 0, 8, 8), new Point(i * 10, 4)));
            }
            var session = new HintSession
            {
                Hints = hints,
                OwningWindow = IntPtr.Zero,
                OwningWindowBounds = new Rect(0, 0, 400, 100)
            };
            var vm = new OverlayViewModel(session, new HintLabelService());

            // On by default: one box per distinct label first char, no prefix typed.
            Assert.True(vm.GroupView);
            Assert.NotNull(vm.GroupBoxes);
            Assert.Equal(vm.Hints.Select(h => h.Label[0]).Distinct().Count(), vm.GroupBoxes.Count);
            Assert.Equal(0, vm.MatchLength);

            // <leader>p semantics: off -> boxes null; back on -> boxes return.
            vm.ToggleGroupView();
            Assert.False(vm.GroupView);
            Assert.Null(vm.GroupBoxes);

            vm.ToggleGroupView();
            Assert.True(vm.GroupView);
            Assert.NotNull(vm.GroupBoxes);
        }

        [Fact]
        public void NormalizeRegion_CornersInOrder_YieldsPositiveRect()
        {
            var r = OverlayViewModel.NormalizeRegion(new Point(10, 20), new Point(110, 220));
            Assert.Equal(10, r.X);
            Assert.Equal(20, r.Y);
            Assert.Equal(100, r.Width);
            Assert.Equal(200, r.Height);
        }

        [Fact]
        public void NormalizeRegion_CornersReversed_NormalizesToSameRect()
        {
            // Corner-entry order must not matter.
            var r = OverlayViewModel.NormalizeRegion(new Point(110, 220), new Point(10, 20));
            Assert.Equal(10, r.X);
            Assert.Equal(20, r.Y);
            Assert.Equal(100, r.Width);
            Assert.Equal(200, r.Height);
        }

        [Fact]
        public void NormalizeRegion_NegativeCoords_Normalized()
        {
            // A monitor left of the primary has negative coords; both axes must normalize.
            var r = OverlayViewModel.NormalizeRegion(new Point(-200, -100), new Point(-50, 0));
            Assert.Equal(-200, r.X);
            Assert.Equal(-100, r.Y);
            Assert.Equal(150, r.Width);
            Assert.Equal(100, r.Height);
        }

        [Fact]
        public void NormalizeRegion_SamePoint_IsDegenerateZero()
        {
            var r = OverlayViewModel.NormalizeRegion(new Point(50, 50), new Point(50, 50));
            Assert.Equal(0, r.Width);
            Assert.Equal(0, r.Height);
        }
    }
}
