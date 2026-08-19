using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using System.Windows.Forms;
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
        public void Space_MapsToLeader()
        {
            // <Space> opens the leader dispatcher (no longer cycles modes).
            var act = OverlayKeyboardHook.Classify(User32.VK_SPACE, false, false);
            Assert.Equal(OverlayKeyActionKind.Leader, act.Kind);
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

        [Fact]
        public void CtrlTab_PassesThrough()
        {
            // Ctrl+Tab switches the app's/browser's own tabs; it must reach the app.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_TAB, false, true).Kind);
        }

        [Fact]
        public void CtrlShiftTab_PassesThrough()
        {
            // Ctrl+Shift+Tab (reverse tab switch) must also reach the app.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_TAB, true, true).Kind);
        }

        [Fact]
        public void WinTab_PassesThrough()
        {
            // Win+Tab is Task View / virtual desktops; leave it for the OS.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_TAB, false, false, win: true).Kind);
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
            Assert.Equal(NudgeTier.Medium, act.Tier);
        }

        [Fact]
        public void ShiftArrow_IsLargeNudge()
        {
            var act = OverlayKeyboardHook.Classify(User32.VK_UP, true, false);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.Equal(NudgeTier.Large, act.Tier);
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
        public void Letters_AppendChar(int vk, char expected)
        {
            // Letters are always typeable label chars (case-normalized to upper).
            var act = OverlayKeyboardHook.Classify(vk, false, false);
            Assert.Equal(OverlayKeyActionKind.AppendChar, act.Kind);
            Assert.Equal(expected, act.Char);
        }

        [Fact]
        public void LetterQ_MapsToEscape()
        {
            // `Q` is the close alias (Esc) -- reserved, so the default HintCharacters
            // excludes it (a Q-label could never be typed).
            var act = OverlayKeyboardHook.Classify(User32.VK_Q, false, false);
            Assert.Equal(OverlayKeyActionKind.Escape, act.Kind);
        }

        [Fact]
        public void LetterI_MapsToInsertToggle()
        {
            // Plain `i` enters insert mode (vim-style suspend) -- reserved like Q, so
            // the default HintCharacters excludes I (an I-label could never be typed).
            var act = OverlayKeyboardHook.Classify(User32.VK_I, false, false);
            Assert.Equal(OverlayKeyActionKind.InsertToggle, act.Kind);
        }

        [Fact]
        public void ShiftI_IsStillLargeNudgeDown()
        {
            // Shift+I is the Large nudge-DOWN chord (NudgeKeysLarge = U,I,O,P maps
            // positional L,D,U,R: u=left, i=down, o=up, p=right) and must keep working
            // even though plain I is now insert mode.
            var act = OverlayKeyboardHook.Classify(User32.VK_I, true, false);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.Equal(NudgeTier.Large, act.Tier);
            Assert.Equal(0, act.Dx);
            Assert.Equal(1, act.Dy);
        }

        [Fact]
        public void CtrlI_PassesThrough()
        {
            // The insert-mode key must not eat the Ctrl+I app shortcut.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_I, false, true).Kind);
        }

        [Fact]
        public void WinI_PassesThrough()
        {
            // Win+I opens Windows Settings; it must pass through.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_I, false, false, win: true).Kind);
        }

        [Fact]
        public void Digit1_PassesThrough()
        {
            // `1` was unaliased (Q is the close alias now); it reaches the app.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_1, false, false).Kind);
        }

        [Fact]
        public void Digit2_PassesThrough()
        {
            // `2` was freed: suspend moved under the leader (<leader>z); `2` reaches the app.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_2, false, false).Kind);
        }

        [Fact]
        public void Digit3_PassesThrough()
        {
            // `3` was freed: cycle-layout moved under the leader (<leader>g); `3` reaches
            // the app regardless of multi-layout config.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_3, false, false).Kind);
        }

        [Theory]
        [InlineData(User32.VK_0)]
        [InlineData(User32.VK_9)]
        public void NonFunctionDigits_PassThrough(int vk)
        {
            // Digits that are not 1/2/3 are neither labels nor functions -> app gets them.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, false, false).Kind);
        }

        [Theory]
        [InlineData(User32.VK_1)]
        [InlineData(User32.VK_2)]
        [InlineData(User32.VK_3)]
        public void CtrlDigit_PassesThrough(int vk)
        {
            // Ctrl+digit is an app shortcut, not an overlay function.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, false, true).Kind);
        }

        [Fact]
        public void CtrlQ_PassesThrough()
        {
            // The Q close-alias must not eat the Ctrl+Q app shortcut.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_Q, false, true).Kind);
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
        [InlineData(User32.VK_OEM_1)]   // `;`
        public void CtrlModifier_BlocksLabelChar(int vk)
        {
            // With Ctrl held, a letter or configured punctuation is a shortcut, not a label.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, false, true, labelChars: ";").Kind);
        }

        [Fact]
        public void UnhandledKey_PassesThrough()
        {
            // e.g. F1 (0x70) is neither a label nor an action key.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(0x70, false, false).Kind);
        }

        [Fact]
        public void Backtick_PassesThrough()
        {
            // Backtick was freed: toggle-dim moved under the leader (<leader>i); it is not
            // a label char by default, so it reaches the app.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_OEM_3, false, false).Kind);
        }

        [Fact]
        public void Backslash_AppendsChar_WhenLabel()
        {
            // `\` is now a label char (suspend moved to `2`), captured only when configured.
            var act = OverlayKeyboardHook.Classify(User32.VK_OEM_5, false, false, labelChars: "\\");
            Assert.Equal(OverlayKeyActionKind.AppendChar, act.Kind);
            Assert.Equal('\\', act.Char);
        }

        [Fact]
        public void Backslash_PassesThrough_WhenNotLabel()
        {
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_OEM_5, false, false).Kind);
        }

        [Theory]
        [InlineData(User32.VK_OEM_3)]
        [InlineData(User32.VK_OEM_5)]
        public void CtrlModifier_LetsOemKeysPassThrough(int vk)
        {
            // Ctrl+` (dim) or Ctrl+\ (label char) is an app shortcut -- passes through.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, false, true).Kind);
        }

        // ---- nudge tiers (Shift+row). Plain row keys still type labels. ----
        // Medium = hjkl, Large = uiop, Small = m , . /  (positional L,D,U,R).

        [Theory]
        [InlineData(User32.VK_H, -1, 0)]
        [InlineData(User32.VK_J, 0, 1)]
        [InlineData(User32.VK_K, 0, -1)]
        [InlineData(User32.VK_L, 1, 0)]
        public void ShiftHjkl_IsMediumNudge(int vk, int dx, int dy)
        {
            // Shift+hjkl pans by the Medium step.
            var act = OverlayKeyboardHook.Classify(vk, true, false);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.Equal(dx, act.Dx);
            Assert.Equal(dy, act.Dy);
            Assert.Equal(NudgeTier.Medium, act.Tier);
        }

        [Theory]
        [InlineData((int)Keys.U, -1, 0)]
        [InlineData((int)Keys.I, 0, 1)]
        [InlineData((int)Keys.O, 0, -1)]
        [InlineData((int)Keys.P, 1, 0)]
        public void ShiftUiop_IsLargeNudge(int vk, int dx, int dy)
        {
            // Shift+uiop pans by the Large step (default "auto" = one zone cell).
            var act = OverlayKeyboardHook.Classify(vk, true, false);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.Equal(dx, act.Dx);
            Assert.Equal(dy, act.Dy);
            Assert.Equal(NudgeTier.Large, act.Tier);
        }

        [Theory]
        [InlineData((int)Keys.M, -1, 0)]
        [InlineData((int)Keys.Oemcomma, 0, 1)]
        [InlineData((int)Keys.OemPeriod, 0, -1)]
        [InlineData((int)Keys.Oem2, 1, 0)]
        public void ShiftMOem_IsSmallNudge(int vk, int dx, int dy)
        {
            // Shift+m , . / pans by the Small step.
            var act = OverlayKeyboardHook.Classify(vk, true, false);
            Assert.Equal(OverlayKeyActionKind.Nudge, act.Kind);
            Assert.Equal(dx, act.Dx);
            Assert.Equal(dy, act.Dy);
            Assert.Equal(NudgeTier.Small, act.Tier);
        }

        [Theory]
        [InlineData(User32.VK_H)]
        [InlineData((int)Keys.U)]
        [InlineData((int)Keys.M)]
        public void CtrlShiftNudgeKey_PassesThrough(int vk)
        {
            // Ctrl+Shift+<nudge row> is retired; Ctrl makes it an app shortcut -> None.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(vk, true, true).Kind);
        }

        [Theory]
        [InlineData((int)Keys.U, 'U')]
        [InlineData((int)Keys.M, 'M')]
        public void PlainNudgeKey_StillAppendsChar(int vk, char expected)
        {
            // Plain (no Shift) nudge-row keys still type label chars.
            var act = OverlayKeyboardHook.Classify(vk, false, false);
            Assert.Equal(OverlayKeyActionKind.AppendChar, act.Kind);
            Assert.Equal(expected, act.Char);
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

        // ---- punctuation as label chars (`,./;'[]\`, captured when configured) ----

        [Theory]
        [InlineData(User32.VK_OEM_1, ';')]      // ;
        [InlineData(User32.VK_OEM_COMMA, ',')]  // ,
        [InlineData(User32.VK_OEM_2, '/')]      // /
        [InlineData(User32.VK_OEM_4, '[')]      // [
        public void Punctuation_AppendsChar_WhenLabel(int vk, char expected)
        {
            var act = OverlayKeyboardHook.Classify(vk, false, false, labelChars: ",/;[]\\");
            Assert.Equal(OverlayKeyActionKind.AppendChar, act.Kind);
            Assert.Equal(expected, act.Char);
        }

        [Fact]
        public void Semicolon_PassesThrough_WhenNotLabel()
        {
            // `;` is no longer a hotkey; it reaches the app unless configured as a label.
            Assert.Equal(OverlayKeyActionKind.None,
                OverlayKeyboardHook.Classify(User32.VK_OEM_1, false, false).Kind);
        }

        [Fact]
        public void ShiftSemicolon_StillAppendsChar()
        {
            // Shift does not block label typing (Shift+; is still the `;` label).
            var act = OverlayKeyboardHook.Classify(User32.VK_OEM_1, true, false, labelChars: ";");
            Assert.Equal(OverlayKeyActionKind.AppendChar, act.Kind);
            Assert.Equal(';', act.Char);
        }
    }
}
