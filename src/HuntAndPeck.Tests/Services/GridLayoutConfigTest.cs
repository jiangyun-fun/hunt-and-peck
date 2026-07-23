using HuntAndPeck.Services;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class GridLayoutConfigTest
    {
        // ParseGridLayouts is the pure decode of the GridLayouts appSetting. It must not
        // depend on a config file or a window, so the full mapping is unit-testable here.

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_ReturnsEmpty(string raw)
        {
            // No GridLayouts configured -> the caller falls back to the flat keys.
            Assert.Empty(GridLayoutConfig.ParseGridLayouts(raw));
        }

        [Fact]
        public void Null_ReturnsEmpty()
        {
            Assert.Empty(GridLayoutConfig.ParseGridLayouts(null));
        }

        [Fact]
        public void TwoLayouts_ParsedInOrder()
        {
            var raw = "30|50|10|15|Left,Top,TR,BR,Center || 40|40|10|0|Center";
            var layouts = GridLayoutConfig.ParseGridLayouts(raw);

            Assert.Equal(2, layouts.Count);

            Assert.Equal(30, layouts[0].EdgeStep);
            Assert.Equal(50, layouts[0].CenterStep);
            Assert.Equal(10, layouts[0].Inset);
            Assert.Equal(15, layouts[0].BandPercent);
            Assert.Equal("Left,Top,TR,BR,Center", layouts[0].DenseRegions);

            Assert.Equal(40, layouts[1].EdgeStep);
            Assert.Equal(40, layouts[1].CenterStep);
            Assert.Equal(0, layouts[1].BandPercent); // 0 allowed (uniform / no-edge layout)
            Assert.Equal("Center", layouts[1].DenseRegions);
        }

        [Fact]
        public void Regions_WithCommas_Preserved()
        {
            // The regions field legitimately contains commas; the field separator is '|'.
            var layouts = GridLayoutConfig.ParseGridLayouts("30|50|10|15|Left,Top,TR,BR,Center");
            Assert.Single(layouts);
            Assert.Equal("Left,Top,TR,BR,Center", layouts[0].DenseRegions);
        }

        [Fact]
        public void MissingFields_Defaulted()
        {
            // Only edgeStep supplied; the rest fall back to the documented defaults.
            var layouts = GridLayoutConfig.ParseGridLayouts("25");
            Assert.Single(layouts);
            Assert.Equal(25, layouts[0].EdgeStep);
            Assert.Equal(GridLayout.DefaultCenterStep, layouts[0].CenterStep);
            Assert.Equal(GridLayout.DefaultInset, layouts[0].Inset);
            Assert.Equal(GridLayout.DefaultBandPercent, layouts[0].BandPercent);
            Assert.Equal(GridLayout.DefaultDenseRegions, layouts[0].DenseRegions);
        }

        [Fact]
        public void BadNumeric_Defaulted()
        {
            // Non-numeric and non-positive numerics fall back to defaults; bandPercent 15
            // is valid; regions "Center" is preserved.
            var layouts = GridLayoutConfig.ParseGridLayouts("abc|0|xyz|15|Center");
            Assert.Single(layouts);
            Assert.Equal(GridLayout.DefaultEdgeStep, layouts[0].EdgeStep);    // non-numeric
            Assert.Equal(GridLayout.DefaultCenterStep, layouts[0].CenterStep); // 0 is non-positive
            Assert.Equal(GridLayout.DefaultInset, layouts[0].Inset);           // non-numeric
            Assert.Equal(15, layouts[0].BandPercent);
            Assert.Equal("Center", layouts[0].DenseRegions);
        }

        [Fact]
        public void BandPercent_ZeroAllowed()
        {
            // bandPercent uses ParsePercent (0-100), so 0 is honored (uniform layout).
            var layouts = GridLayoutConfig.ParseGridLayouts("40|40|10|0|Center");
            Assert.Equal(0, layouts[0].BandPercent);
        }

        [Fact]
        public void TrailingSeparator_NotAnExtraLayout()
        {
            var layouts = GridLayoutConfig.ParseGridLayouts("30|50|10|15|Center || ");
            Assert.Single(layouts);
        }

        [Theory]
        [InlineData(-1, 3, 2)]
        [InlineData(0, 3, 0)]
        [InlineData(2, 3, 2)]
        [InlineData(3, 3, 0)]
        [InlineData(5, 3, 2)]
        [InlineData(7, 2, 1)]
        [InlineData(7, 1, 0)]
        [InlineData(7, 0, 0)]
        public void ClampActiveLayout_WrapsAndClamps(int index, int count, int expected)
        {
            Assert.Equal(expected, GridLayoutConfig.ClampActiveLayout(index, count));
        }
    }
}
