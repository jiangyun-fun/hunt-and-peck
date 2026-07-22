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
        [InlineData("80", 50, 80)]
        [InlineData("0", 50, 0)]       // 0 is a valid percent (fully transparent pill)
        [InlineData("100", 50, 100)]
        [InlineData("150", 50, 100)]   // clamped to 100
        [InlineData("-5", 50, 0)]      // clamped to 0
        [InlineData("", 50, 50)]       // blank -> default
        [InlineData("not-a-number", 80, 80)]
        [InlineData(null, 80, 80)]
        public void ParsePercent_ParsesOrClampsOrDefault(string raw, int defaultValue, int expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParsePercent(raw, defaultValue));
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

        [Theory]
        [InlineData("Screen", HintBounds.Screen)]
        [InlineData("screen", HintBounds.Screen)]   // case-insensitive
        [InlineData("WINDOW", HintBounds.Window)]
        [InlineData("junk", HintBounds.Screen)]     // invalid -> default (Screen)
        [InlineData("", HintBounds.Screen)]         // blank -> default
        [InlineData(null, HintBounds.Screen)]
        public void ParseHintBounds_ParsesOrDefaultsToScreen(string raw, HintBounds expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseHintBounds(raw, HintBounds.Screen));
        }

        [Fact]
        public void ParseHintBounds_UsesProvidedDefaultWhenUnrecognized()
        {
            // The caller picks the default; here Window is the fallback for junk input.
            Assert.Equal(HintBounds.Window, OverlayActionConfig.ParseHintBounds("???", HintBounds.Window));
            Assert.Equal(HintBounds.Window, OverlayActionConfig.ParseHintBounds(null, HintBounds.Window));
        }

        [Theory]
        [InlineData("Continuous", TriggerMode.Continuous)]
        [InlineData("continuous", TriggerMode.Continuous)]   // case-insensitive
        [InlineData("OneClick", TriggerMode.OneClick)]
        [InlineData("ONECLICK", TriggerMode.OneClick)]
        [InlineData("", TriggerMode.OneClick)]               // blank -> default
        [InlineData(null, TriggerMode.OneClick)]
        [InlineData("junk", TriggerMode.OneClick)]           // invalid -> default
        public void ParseTriggerMode_ParsesOrDefaultsToOneClick(string raw, TriggerMode expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseTriggerMode(raw, TriggerMode.OneClick));
        }

        [Fact]
        public void ParseTriggerMode_UsesProvidedDefaultWhenUnrecognized()
        {
            Assert.Equal(TriggerMode.Continuous, OverlayActionConfig.ParseTriggerMode("???", TriggerMode.Continuous));
            Assert.Equal(TriggerMode.Continuous, OverlayActionConfig.ParseTriggerMode(null, TriggerMode.Continuous));
        }

        [Theory]
        [InlineData(false, true, TriggerMode.Continuous, true)]   // Grid + config Continuous -> continuous
        [InlineData(true, true, TriggerMode.Continuous, false)]   // forced one-shot overrides Continuous
        [InlineData(false, false, TriggerMode.Continuous, false)] // Automation stays one-shot
        [InlineData(false, true, TriggerMode.OneClick, false)]    // config OneClick
        [InlineData(true, false, TriggerMode.Continuous, false)]  // forced + Automation
        [InlineData(true, true, TriggerMode.OneClick, false)]     // forced + config OneClick
        public void ComputeIsContinuous_RespectsForceOneShotAndGrid(bool forceOneShot, bool gridSource, TriggerMode configMode, bool expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ComputeIsContinuous(forceOneShot, gridSource, configMode));
        }

        [Theory]
        [InlineData("Grid", HintBounds.Screen, false)]       // duplicating combo -> skip
        [InlineData(null, HintBounds.Screen, false)]         // Grid default + Screen -> skip
        [InlineData("", HintBounds.Screen, false)]
        [InlineData("grid", HintBounds.Screen, false)]       // case-insensitive Grid
        [InlineData("Grid", HintBounds.Window, true)]        // window grid doesn't reach taskbar
        [InlineData("Automation", HintBounds.Screen, true)]  // taskbar's own real controls
        [InlineData("Automation", HintBounds.Window, true)]
        public void ShouldMergeTaskbar_SkipsOnlyForGridPlusScreen(string hintSource, HintBounds bounds, bool expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ShouldMergeTaskbar(hintSource, bounds));
        }

        [Theory]
        [InlineData("Grid", true)]
        [InlineData(null, true)]      // default source is Grid
        [InlineData("", true)]
        [InlineData("grid", true)]    // case-insensitive
        [InlineData("Automation", false)]
        public void IsGridHintSource_DetectsGridOrDefault(string raw, bool expected)
        {
            Assert.Equal(expected, OverlayActionConfig.IsGridHintSource(raw));
        }

        [Fact]
        public void EnsureFresh_IsSafeToCallRepeatedly()
        {
            // Stat-and-cache path must not throw whether or not a real config file is
            // present (test host usually has none). Idempotent across calls.
            OverlayActionConfig.EnsureFresh();
            OverlayActionConfig.EnsureFresh();
            OverlayActionConfig.EnsureFresh();
        }
    }
}
