using System.Windows.Forms;
using HuntAndPeck.NativeMethods;
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

        [Theory]
        [InlineData("Left,Right,Double,Move", 4)]
        [InlineData("", 4)]          // empty -> default order (4)
        [InlineData(null, 4)]
        [InlineData("Right,Left", 2)]
        [InlineData("junk,stuff", 4)]// all invalid -> default order
        [InlineData("left,double", 2)] // case-insensitive
        [InlineData("Move|Right", 2)] // pipe separator
        public void ParseClickActionOrder_ParsesOrDefaults(string raw, int expectedCount)
        {
            var order = OverlayActionConfig.ParseClickActionOrder(raw);
            Assert.Equal(expectedCount, order.Count);
        }

        [Fact]
        public void ParseClickActionOrder_DefaultOrderStartsWithLeft()
        {
            var order = OverlayActionConfig.ParseClickActionOrder(null);
            Assert.Equal(ClickAction.Left, order[0]);
        }

        [Fact]
        public void ParseClickActionOrder_DropsDuplicates()
        {
            var order = OverlayActionConfig.ParseClickActionOrder("Left,Left,Right");
            Assert.Equal(2, order.Count);
        }

        [Theory]
        [InlineData("F", Keys.F)]
        [InlineData("space", Keys.Space)] // case-insensitive
        [InlineData("OemSemicolon", Keys.OemSemicolon)]
        [InlineData("junk", Keys.F)]       // invalid -> fallback
        [InlineData("", Keys.F)]
        [InlineData(null, Keys.F)]
        public void ParseKeys_FallsBackWhenInvalid(string raw, Keys expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseKeys(raw, Keys.F));
        }

        [Theory]
        [InlineData("Control,Alt,Shift", KeyModifier.Control | KeyModifier.Alt | KeyModifier.Shift)]
        [InlineData("Alt", KeyModifier.Alt)]
        [InlineData("control|shift", KeyModifier.Control | KeyModifier.Shift)]
        [InlineData("", KeyModifier.Alt)]     // empty -> fallback
        [InlineData("junk", KeyModifier.Alt)] // invalid -> fallback
        [InlineData(null, KeyModifier.Alt)]
        public void ParseKeyModifiers_FallsBackWhenEmptyOrInvalid(string raw, KeyModifier expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseKeyModifiers(raw, KeyModifier.Alt));
        }
    }
}
