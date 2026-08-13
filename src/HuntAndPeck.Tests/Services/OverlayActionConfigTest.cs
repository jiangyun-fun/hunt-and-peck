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
        [InlineData("true", false, true)]
        [InlineData("False", false, false)]      // case-insensitive
        [InlineData("TRUE", false, true)]
        [InlineData("1", false, true)]
        [InlineData("0", true, false)]
        [InlineData("yes", true, true)]          // unrecognized -> default (true)
        [InlineData("junk", false, false)]       // unrecognized -> default (false)
        [InlineData("", true, true)]             // blank -> default
        [InlineData(null, false, false)]
        public void ParseBool_ParsesOrDefaults(string raw, bool defaultValue, bool expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseBool(raw, defaultValue));
        }

        [Theory]
        [InlineData("15,15", 3, 3, 15, 15, false)]   // basic
        [InlineData("450,500", 3, 3, 450, 500, false)] // per-axis (x != y)
        [InlineData("auto", 3, 3, 0, 0, true)]        // auto
        [InlineData("AUTO", 3, 3, 0, 0, true)]        // case-insensitive
        [InlineData("", 7, 9, 7, 9, false)]           // blank -> default
        [InlineData("1", 7, 9, 7, 9, false)]          // not x,y -> default
        [InlineData("0,5", 7, 9, 7, 9, false)]        // non-positive -> default
        [InlineData("-3,5", 7, 9, 7, 9, false)]
        [InlineData(null, 7, 9, 7, 9, false)]
        public void ParseNudgeStep_ParsesOrAutoOrDefault(string raw, int defX, int defY, int expX, int expY, bool expAuto)
        {
            var s = OverlayActionConfig.ParseNudgeStep(raw, new NudgeStep { X = defX, Y = defY });
            Assert.Equal(expAuto, s.IsAuto);
            Assert.Equal(expX, s.X);
            Assert.Equal(expY, s.Y);
        }

        [Fact]
        public void ParseNudgeKeys_FourKeys_ReturnsVkCodes()
        {
            var k = OverlayActionConfig.ParseNudgeKeys("H,J,K,L", new[] { 0, 0, 0, 0 });
            Assert.Equal(new[] { (int)Keys.H, (int)Keys.J, (int)Keys.K, (int)Keys.L }, k);
        }

        [Fact]
        public void ParseNudgeKeys_WrongCount_ReturnsFallback()
        {
            var fb = new[] { 1, 2, 3, 4 };
            Assert.Same(fb, OverlayActionConfig.ParseNudgeKeys("H,J", fb));
            Assert.Same(fb, OverlayActionConfig.ParseNudgeKeys("H,J,K,L,M", fb));
        }

        [Fact]
        public void ParseNudgeKeys_UnknownName_ReturnsFallback()
        {
            var fb = new[] { 1, 2, 3, 4 };
            Assert.Same(fb, OverlayActionConfig.ParseNudgeKeys("H,J,K,NotAKey", fb));
        }

        [Fact]
        public void ParseNudgeKeys_BlankOrBadFallback_ReturnsFallback()
        {
            var fb = new[] { 1, 2, 3, 4 };
            Assert.Same(fb, OverlayActionConfig.ParseNudgeKeys(null, fb));
            Assert.Same(fb, OverlayActionConfig.ParseNudgeKeys("", fb));
        }

        [Theory]
        [InlineData("3", 0.0, 3.0)]
        [InlineData("3.5", 0.0, 3.5)]   // locale-tolerant decimal
        [InlineData("0", 5.0, 0.0)]     // 0 is valid (off)
        [InlineData("-1", 7.0, 7.0)]    // negative -> default
        [InlineData("", 7.0, 7.0)]
        [InlineData("junk", 7.0, 7.0)]
        [InlineData(null, 7.0, 7.0)]
        public void ParseAutoCloseSec_ParsesOrDefault(string raw, double defaultValue, double expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseAutoCloseSec(raw, defaultValue));
        }

        [Fact]
        public void ParseKeyList_FourKeys_ReturnsKeysArray()
        {
            var fb = new[] { Keys.None, Keys.None, Keys.None, Keys.None };
            Assert.Equal(new[] { Keys.F1, Keys.F2, Keys.F3, Keys.F4 },
                OverlayActionConfig.ParseKeyList("F1,F2,F3,F4", fb));
        }

        [Fact]
        public void ParseKeyList_WrongCount_ReturnsFallback()
        {
            var fb = new[] { Keys.A, Keys.B, Keys.C, Keys.D };
            Assert.Same(fb, OverlayActionConfig.ParseKeyList("F1,F2,F3", fb));
            Assert.Same(fb, OverlayActionConfig.ParseKeyList("F1,F2,F3,F4,F5", fb));
        }

        [Fact]
        public void ParseKeyList_UnknownName_ReturnsFallback()
        {
            var fb = new[] { Keys.A, Keys.B, Keys.C, Keys.D };
            Assert.Same(fb, OverlayActionConfig.ParseKeyList("F1,F2,F3,NotAKey", fb));
        }

        [Fact]
        public void ParseKeyList_Blank_ReturnsFallback()
        {
            var fb = new[] { Keys.A, Keys.B, Keys.C, Keys.D };
            Assert.Same(fb, OverlayActionConfig.ParseKeyList(null, fb));
            Assert.Same(fb, OverlayActionConfig.ParseKeyList("", fb));
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

        [Fact]
        public void ParseClickActionOrder_ParsesTriple()
        {
            // Triple is a ClickAction; it must round-trip through the parser so a user
            // can list it in ClickModeOrder (and <leader>t sets it via SetMode).
            var order = OverlayActionConfig.ParseClickActionOrder("Triple");
            Assert.Equal(ClickAction.Triple, Assert.Single(order));
        }

        [Theory]
        [InlineData("ShiftClick", TextSelectMethod.ShiftClick)]
        [InlineData("shiftclick", TextSelectMethod.ShiftClick)]
        [InlineData("Drag", TextSelectMethod.Drag)]
        [InlineData("DRAG", TextSelectMethod.Drag)]
        public void ParseTextSelectMethod_ParsesKnownValues(string raw, TextSelectMethod expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ParseTextSelectMethod(raw, TextSelectMethod.ShiftClick));
        }

        [Theory]
        [InlineData("", TextSelectMethod.ShiftClick)]
        [InlineData("junk", TextSelectMethod.ShiftClick)]
        [InlineData(null, TextSelectMethod.ShiftClick)]
        [InlineData("junk", TextSelectMethod.Drag)]
        public void ParseTextSelectMethod_BlankOrUnknown_ReturnsFallback(string raw, TextSelectMethod fallback)
        {
            Assert.Equal(fallback, OverlayActionConfig.ParseTextSelectMethod(raw, fallback));
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
        [InlineData(true, TriggerMode.Continuous, true)]    // Grid + config Continuous -> continuous
        [InlineData(false, TriggerMode.Continuous, false)]  // Automation stays one-shot
        [InlineData(true, TriggerMode.OneClick, false)]     // config OneClick
        public void ComputeIsContinuous_RespectsGridAndConfig(bool gridSource, TriggerMode configMode, bool expected)
        {
            Assert.Equal(expected, OverlayActionConfig.ComputeIsContinuous(gridSource, configMode));
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
