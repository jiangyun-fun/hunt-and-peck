using System.Windows;
using HuntAndPeck.ViewModels;
using Xunit;

namespace HuntAndPeck.Tests.ViewModels
{
    public class OverlayViewModelTest
    {
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
