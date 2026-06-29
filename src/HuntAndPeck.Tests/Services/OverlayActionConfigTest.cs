using HuntAndPeck.Services;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class OverlayActionConfigTest
    {
        [Theory]
        [InlineData("3", 7, 3)]
        [InlineData("15", 7, 15)]
        [InlineData("0", 7, 7)]      // non-positive falls back to default
        [InlineData("-5", 7, 7)]
        [InlineData("not-a-number", 7, 7)]
        [InlineData(null, 7, 7)]
        public void ParseInt_UsesDefaultWhenInvalidOrNonPositive(string raw, int defaultValue, int expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseInt(raw, defaultValue));
        }
    }
}
