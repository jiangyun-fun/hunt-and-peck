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
    }
}
