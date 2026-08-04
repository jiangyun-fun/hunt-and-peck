using System;
using System.Windows.Forms;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services.Macro;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class InputSynthesisTest
    {
        // NOTE: we avoid Assert.Throws here. xUnit 2.2.0's xunit.assert.dll targets
        // netstandard1.1, and its Throws overload surface pulls in Task from the
        // System.Threading.Tasks facade, which the net452 test project does not
        // reference (CS0012). A plain try/catch over an Action avoids that entirely.

        [Theory]
        [InlineData("Q", (int)Keys.Q)]
        [InlineData("q", (int)Keys.Q)]            // case-insensitive
        [InlineData("N", (int)Keys.N)]
        [InlineData("F1", (int)Keys.F1)]
        [InlineData("Oemcomma", (int)Keys.Oemcomma)]
        public void ParseKey_AcceptsKeysNames(string name, int expected)
        {
            Assert.Equal(expected, InputSynthesis.ParseKey(name));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("   ")]
        public void ParseKey_ThrowsOnBlank(string key)
        {
            AssertThrowsArgument(() => InputSynthesis.ParseKey(key));
        }

        [Fact]
        public void ParseKey_ThrowsOnUnknown()
        {
            AssertThrowsArgument(() => InputSynthesis.ParseKey("NotAKey"));
        }

        [Fact]
        public void ParseModifiers_MapsCtrlShiftAltWin()
        {
            var vks = InputSynthesis.ParseModifiers(new[] { "Ctrl", "Shift", "Alt", "Win" });
            Assert.Equal(
                new[] { User32.VK_CONTROL, User32.VK_SHIFT, User32.VK_MENU, User32.VK_LWIN },
                vks);
        }

        [Fact]
        public void ParseModifiers_IgnoresBlankAndTrims()
        {
            var vks = InputSynthesis.ParseModifiers(new[] { " ctrl ", "", "  " });
            Assert.Equal(new[] { User32.VK_CONTROL }, vks);
        }

        [Fact]
        public void ParseModifiers_ThrowsOnUnknown()
        {
            AssertThrowsArgument(() => InputSynthesis.ParseModifiers(new[] { "Foo" }));
        }

        private static void AssertThrowsArgument(Action act)
        {
            try
            {
                act();
            }
            catch (ArgumentException)
            {
                return; // expected
            }
            Assert.True(false, "expected an ArgumentException");
        }
    }
}
