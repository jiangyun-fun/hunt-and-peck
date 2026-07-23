using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class OverlayKeyboardHookTest
    {
        // Classify is the pure vk-code -> action decode used by the global keyboard
        // hook. It must not depend on a window or a real hook, so we can unit-test
        // the full mapping here.

        [Fact]
        public void Escape_MapsToEscape()
        {
            var act = OverlayKeyboardHook.Classify(User32.VK_ESCAPE, false, false);
            Assert.Equal(OverlayKeyActionKind.Escape, act.Kind);
        }

        [Fact]
        public void Space_MapsToCycleMode()
        {
            var act = OverlayKeyboardHook.Classify(User32.VK_SPACE, false, false);
            Assert.Equal(OverlayKeyActionKind.CycleMode, act.Kind);
        }

        [Fact]
        public void Tab_NoShift_CyclesNextMonitor()
        {
            var act = OverlayKeyboardHook.Classify(User32.VK_TAB, false, false);
            Assert.Equal(OverlayKeyActionKind.CycleMonitorNext, act.Kind);
        }

        [Fact]
        public void Tab_WithShift_CyclesPrevMonitor()
        {
            var act = OverlayKeyboardHook.Classify(User32.VK_TAB, true, false);
            Assert.Equal(OverlayKeyActionKind.CycleMonitorPrev, act.Kind);
        }

        [Theory]
        [InlineData(User32.VK_LEFT, -1, 0)]
        [InlineData(User32.VK_UP, 0, -1)]
        [InlineData(User32.VK_RIGHT, 1, 0)]
        [InlineData(User32.VK_DOWN, 0, 1)]
        public void Arrows_NudgeInDirection(int vk, int dx, int dy)
        {
            var act = OverlayKeyboardHook.Classify(vk, false, false);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.Equal(dx, act.Dx);
            Assert.Equal(dy, act.Dy);
            Assert.False(act.Fast);
        }

        [Fact]
        public void ShiftArrow_IsFastNudge()
        {
            var act = OverlayKeyboardHook.Classify(User32.VK_UP, true, false);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.True(act.Fast);
        }

        [Theory]
        [InlineData(User32.VK_LEFT)]
        [InlineData(User32.VK_UP)]
        [InlineData(User32.VK_RIGHT)]
        [InlineData(User32.VK_DOWN)]
        public void NumpadArrows_NotExtended_PassThrough(int vk)
        {
            // Numpad nav keys (NumLock off) reuse the arrow VK codes but do NOT set the
            // extended flag, so they must pass through (None) -- letting a numpad-mouse
            // AutoHotkey script work while the overlay is up.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, false, false, extended: false).Kind);
        }

        [Theory]
        [InlineData(User32.VK_A, 'A')]
        [InlineData(User32.VK_Z, 'Z')]
        [InlineData(User32.VK_0, '0')]
        [InlineData(User32.VK_9, '9')]
        public void LettersAndDigits_AppendChar(int vk, char expected)
        {
            var act = OverlayKeyboardHook.Classify(vk, false, false);
            Assert.Equal(OverlayKeyActionKind.AppendChar, act.Kind);
            Assert.Equal(expected, act.Char);
        }

        [Fact]
        public void ShiftLetter_StillAppendsChar()
        {
            // Shift alone must NOT block label typing (Shift+A is still 'A').
            var act = OverlayKeyboardHook.Classify(User32.VK_A, true, false);
            Assert.Equal(OverlayKeyActionKind.AppendChar, act.Kind);
            Assert.Equal('A', act.Char);
        }

        [Theory]
        [InlineData(User32.VK_A)]
        [InlineData(User32.VK_0)]
        public void CtrlAltWin_Held_BlocksLabelChar(int vk)
        {
            // With Ctrl/Alt/Win held, a letter/digit is a shortcut, not a label.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, false, true).Kind);
        }

        [Fact]
        public void UnhandledKey_PassesThrough()
        {
            // e.g. F1 (0x70) is neither a label nor an action key.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(0x70, false, false).Kind);
        }

        [Fact]
        public void Backtick_TogglesDimmed()
        {
            var act = OverlayKeyboardHook.Classify(User32.VK_OEM_3, false, false);
            Assert.Equal(OverlayKeyActionKind.ToggleDimmed, act.Kind);
        }

        [Fact]
        public void Backslash_EntersSuspend()
        {
            var act = OverlayKeyboardHook.Classify(User32.VK_OEM_5, false, false);
            Assert.Equal(OverlayKeyActionKind.SuspendNow, act.Kind);
        }

        [Theory]
        [InlineData(User32.VK_OEM_3)]
        [InlineData(User32.VK_OEM_5)]
        public void CtrlModifier_LetsToggleKeysPassThrough(int vk)
        {
            // Ctrl+` or Ctrl+\ is an app shortcut, not an overlay toggle.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, false, true).Kind);
        }

        // ---- hjkl label-pan (h=left, j=down, k=up, l=right) ----

        [Theory]
        [InlineData(User32.VK_H, -1, 0)]
        [InlineData(User32.VK_J, 0, 1)]
        [InlineData(User32.VK_K, 0, -1)]
        [InlineData(User32.VK_L, 1, 0)]
        public void ShiftHjkl_IsLargeNudge(int vk, int dx, int dy)
        {
            // Shift+hjkl pans all labels by the large step (NudgeStepFast).
            var act = OverlayKeyboardHook.Classify(vk, true, false);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.Equal(dx, act.Dx);
            Assert.Equal(dy, act.Dy);
            Assert.True(act.Fast);
        }

        [Theory]
        [InlineData(User32.VK_H, -1, 0)]
        [InlineData(User32.VK_J, 0, 1)]
        [InlineData(User32.VK_K, 0, -1)]
        [InlineData(User32.VK_L, 1, 0)]
        public void CtrlShiftHjkl_IsSmallNudge(int vk, int dx, int dy)
        {
            // Ctrl+Shift+hjkl pans by the small step (NudgeStep) -- still captured even
            // though Ctrl is held, because the hjkl branch runs before the ctrl gate.
            var act = OverlayKeyboardHook.Classify(vk, true, true);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.Equal(dx, act.Dx);
            Assert.Equal(dy, act.Dy);
            Assert.False(act.Fast);
        }

        [Fact]
        public void PlainHjkl_StillAppendsChar()
        {
            // Plain h (no Shift) must still type a hint char, not pan.
            var act = OverlayKeyboardHook.Classify(User32.VK_H, false, false);
            Assert.Equal(OverlayKeyActionKind.AppendChar, act.Kind);
            Assert.Equal('H', act.Char);
        }

        [Fact]
        public void WinShiftHjkl_PassesThrough()
        {
            // Win+Shift+hjkl is an OS shortcut, not a pan -- Win must not be captured.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_H, true, false, true).Kind);
        }

        [Theory]
        [InlineData(User32.VK_LEFT)]
        [InlineData(User32.VK_UP)]
        [InlineData(User32.VK_RIGHT)]
        [InlineData(User32.VK_DOWN)]
        public void Arrows_Passthrough_WhenConfigured(int vk)
        {
            // ArrowKeyBehavior=Passthrough (default): dedicated arrows reach the app.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, shift: false, ctrl: false, win: false,
                    extended: true, arrowPan: false).Kind);
        }

        [Theory]
        [InlineData(User32.VK_C)]
        [InlineData(User32.VK_V)]
        public void CtrlPlusLetter_StillPassthrough(int vk)
        {
            // Regression guard: Ctrl+C / Ctrl+V (no Shift) must reach the app. The hjkl
            // pan capture requires Shift, so plain Ctrl+letters are untouched.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, false, true).Kind);
        }
    }
}
