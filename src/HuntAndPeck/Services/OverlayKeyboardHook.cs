using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.ViewModels;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// The kind of action a key performs while the overlay is up.
    /// </summary>
    internal enum OverlayKeyActionKind
    {
        None,
        AppendChar,
        Escape,
        CycleMode,
        CycleMonitorNext,
        CycleMonitorPrev,
        Nudge,
        ToggleDimmed,
        SuspendNow
    }

    /// <summary>
    /// A decoded overlay key action. <see cref="Classify"/> is a pure function of
    /// the virtual-key code and modifier state, so it is unit-testable without a
    /// window or a real hook.
    /// </summary>
    internal struct OverlayKeyAction
    {
        public OverlayKeyActionKind Kind;
        public char Char;       // AppendChar
        public int Dx;          // Nudge unit direction (-1/0/1)
        public int Dy;          // Nudge unit direction (-1/0/1)
        public bool Fast;       // Nudge with Shift held
    }

    /// <summary>
    /// Captures keyboard (and dismiss-on-click) input for the overlay via global
    /// low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) so the overlay does NOT need
    /// keyboard focus.
    /// <para>
    /// Why this exists: previously the overlay forced itself to the foreground
    /// (ForegroundWindow.OnRender) to capture the typed label characters via WPF
    /// focus. But taking foreground dismisses any open popup / context menu (right
    /// -click menu in File Manager or Edge vanishes the instant the hotkey fires).
    /// By reading keys through a low-level hook instead, the overlay can show
    /// non-activated (ShowActivated=false) on top of an open context menu, and the
    /// menu stays open -- you can even label-click items inside it.
    /// </para>
    /// <para>
    /// Lifecycle: <see cref="Arm"/> installs both hooks (must be called on the UI
    /// thread, which owns the message pump the callbacks are delivered on);
    /// <see cref="Disarm"/> removes them. The callbacks do the minimum work inline
    /// (classify + swallow) and defer the rest to the Dispatcher, so they stay well
    /// under the LowLevelHooksTimeout (300ms) -- Windows silently removes hooks
    /// that block longer than that. A mouse button-down anywhere requests close
    /// (click-to-dismiss) and is passed through so the app beneath still receives
    /// the click, matching the old deactivate-on-click behavior.
    /// </para>
    /// </summary>
    internal sealed class OverlayKeyboardHook : IDisposable
    {
        private OverlayViewModel _vm;
        private Action _close;
        private readonly Dispatcher _dispatcher;

        // Physical held-state for Alt / Capslock, tracked from raw key events (not
        // GetAsyncKeyState, which misses a Capslock AutoHotkey has neutralized).
        private bool _altHeld;
        private bool _capsHeld;

        // The delegates MUST be kept in fields: if they are garbage-collected
        // while Windows still holds the callback pointer, the process crashes.
        private readonly User32.HookProc _kbProc;
        private readonly User32.HookProc _msProc;
        private IntPtr _kbHook = IntPtr.Zero;
        private IntPtr _msHook = IntPtr.Zero;

        public OverlayKeyboardHook()
        {
            // Captured on the (UI) thread that constructs us; callbacks arrive here.
            _dispatcher = Dispatcher.CurrentDispatcher;
            _kbProc = KeyboardProc;
            _msProc = MouseProc;
        }

        /// <summary>
        /// Installs the keyboard + mouse hooks. Call on the UI thread. Captured key
        /// actions are routed to <paramref name="vm"/>; Esc and any mouse click
        /// invoke <paramref name="close"/>.
        /// </summary>
        public void Arm(OverlayViewModel vm, Action close)
        {
            _vm = vm;
            _close = close;
            // Seed held-state in case a modifier is already down when the overlay opens.
            _altHeld = IsDown(User32.VK_MENU);
            _capsHeld = IsDown(User32.VK_CAPITAL);

            var hMod = Kernel32.GetModuleHandle(null);
            _kbHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _msHook = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _msProc, hMod, 0);
        }

        /// <summary>Removes both hooks. Safe to call more than once.</summary>
        public void Disarm()
        {
            if (_kbHook != IntPtr.Zero)
            {
                User32.UnhookWindowsHookEx(_kbHook);
                _kbHook = IntPtr.Zero;
            }
            if (_msHook != IntPtr.Zero)
            {
                User32.UnhookWindowsHookEx(_msHook);
                _msHook = IntPtr.Zero;
            }
        }

        public void Dispose() => Disarm();

        // ---- Keyboard ----

        private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code != User32.HC_ACTION)
            {
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }

            int msg = wParam.ToInt32();
            bool down = msg == User32.WM_KEYDOWN || msg == User32.WM_SYSKEYDOWN;
            bool up = msg == User32.WM_KEYUP || msg == User32.WM_SYSKEYUP;
            if (!down && !up)
            {
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }

            var k = (User32.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(User32.KBDLLHOOKSTRUCT));
            int vk = (int)k.vkCode;

            // Track physical held-state for Alt and Capslock from the raw key events.
            // Our hook sits ABOVE AutoHotkey's in the chain (installed later), so we
            // see the physical keydown before AHK suppresses it -- GetAsyncKeyState
            // misses a Capslock AHK has neutralized for a custom combo (Capslock & f),
            // but the raw event still reaches us. Update on both down and up.
            if (vk == User32.VK_MENU || vk == User32.VK_LMENU || vk == User32.VK_RMENU)
            {
                _altHeld = down;
            }
            else if (vk == User32.VK_CAPITAL)
            {
                _capsHeld = down;
            }

            // Suspend overlay key-capture while Alt or Capslock is held, OR while the
            // user has toggled persistent suspend: pass everything straight through so
            // system / AHK combos and normal app typing reach the foreground app.
            //
            // Alt is also checked via GetAsyncKeyState(VK_MENU) as a backstop. The LL
            // hook delivers Alt as VK_LMENU/VK_RMENU (tracked above), but Tab is always
            // classified as cycle-monitor BEFORE the Ctrl/Alt/Win check below -- so
            // without this Alt gate the Tab in Alt+Tab would be swallowed and never reach
            // the OS window switcher. GetAsyncKeyState(VK_MENU) reads Alt reliably here.
            // (Capslock stays event-only: GetAsyncKeyState(VK_CAPITAL) misses a Capslock
            // AutoHotkey has neutralized for a custom combo.)
            bool altHeld = _altHeld || IsDown(User32.VK_MENU);
            if (altHeld || _capsHeld || (_vm != null && _vm.Suspended))
            {
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }

            if (!down)
            {
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }

            bool shift = IsDown(User32.VK_SHIFT);
            // Shift alone is allowed (Shift+letter is still that letter, Shift+arrow
            // is the fast nudge). Ctrl/Alt/Win means the key is a shortcut, not a
            // label char, so let it pass through to the app.
            bool ctrlAltWin = IsDown(User32.VK_CONTROL) || IsDown(User32.VK_MENU)
                              || IsDown(User32.VK_LWIN) || IsDown(User32.VK_RWIN);

            var act = Classify(vk, shift, ctrlAltWin);
            if (act.Kind == OverlayKeyActionKind.None)
            {
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }

            // Defer the real work off the hook callback, but swallow the key now so
            // it never reaches the app beneath.
            _dispatcher.BeginInvoke(Dispatch(act));
            return new IntPtr(1);
        }

        private static bool IsDown(int vKey) => (User32.GetAsyncKeyState(vKey) & 0x8000) != 0;

        /// <summary>
        /// Pure decode of a virtual-key code into an overlay action. Public surface
        /// for unit testing.
        /// </summary>
        internal static OverlayKeyAction Classify(int vkCode, bool shift, bool ctrlAltWin)
        {
            if (vkCode == User32.VK_ESCAPE) return Action(OverlayKeyActionKind.Escape);
            if (vkCode == User32.VK_SPACE) return Action(OverlayKeyActionKind.CycleMode);
            if (vkCode == User32.VK_TAB)
            {
                return Action(shift ? OverlayKeyActionKind.CycleMonitorPrev
                                     : OverlayKeyActionKind.CycleMonitorNext);
            }
            if (vkCode == User32.VK_LEFT) return Nudge(-1, 0, shift);
            if (vkCode == User32.VK_UP) return Nudge(0, -1, shift);
            if (vkCode == User32.VK_RIGHT) return Nudge(1, 0, shift);
            if (vkCode == User32.VK_DOWN) return Nudge(0, 1, shift);

            if (!ctrlAltWin)
            {
                if (vkCode == User32.VK_OEM_3) return Action(OverlayKeyActionKind.ToggleDimmed);
                if (vkCode == User32.VK_OEM_5) return Action(OverlayKeyActionKind.SuspendNow);
                if (vkCode >= User32.VK_0 && vkCode <= User32.VK_9)
                {
                    return Char((char)('0' + (vkCode - User32.VK_0)));
                }
                if (vkCode >= User32.VK_A && vkCode <= User32.VK_Z)
                {
                    return Char((char)('A' + (vkCode - User32.VK_A)));
                }
            }
            return Action(OverlayKeyActionKind.None);
        }

        private Action Dispatch(OverlayKeyAction act)
        {
            switch (act.Kind)
            {
                case OverlayKeyActionKind.Escape:
                    return _close;
                case OverlayKeyActionKind.CycleMode:
                    return () => _vm.CycleMode();
                case OverlayKeyActionKind.CycleMonitorNext:
                    return () => _vm.CycleMonitor(1);
                case OverlayKeyActionKind.CycleMonitorPrev:
                    return () => _vm.CycleMonitor(-1);
                case OverlayKeyActionKind.AppendChar:
                    char c = act.Char;
                    return () => _vm.AppendLabelChar(c);
                case OverlayKeyActionKind.ToggleDimmed:
                    return () => _vm.ToggleDimmed();
                case OverlayKeyActionKind.SuspendNow:
                    return () => _vm.EnterSuspend();
                case OverlayKeyActionKind.Nudge:
                    int dx = act.Dx;
                    int dy = act.Dy;
                    bool fast = act.Fast;
                    return () =>
                    {
                        int step = fast
                            ? OverlayActionConfig.ReadNudgeStepFast()
                            : OverlayActionConfig.ReadNudgeStep();
                        _vm.Nudge(dx * step, dy * step);
                    };
                default:
                    return () => { };
            }
        }

        // ---- Mouse (click-to-dismiss) ----

        private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == User32.HC_ACTION)
            {
                int msg = wParam.ToInt32();
                if (msg == User32.WM_LBUTTONDOWN || msg == User32.WM_RBUTTONDOWN
                    || msg == User32.WM_MBUTTONDOWN || msg == User32.WM_XBUTTONDOWN)
                {
                    // Ignore clicks WE synthesized (mouse_event sets LLMHF_INJECTED).
                    // Otherwise our own label click would dismiss the overlay and
                    // continuous mode could never stay up. Only a REAL user click
                    // dismisses. The click is still passed through below either way.
                    var m = (User32.MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(User32.MSLLHOOKSTRUCT));
                    // Skip our own synthesized clicks (injected), and skip while
                    // suspended (the user is clicking into the app beneath).
                    if ((_vm == null || !_vm.Suspended) && (m.flags & User32.LLMHF_INJECTED) == 0)
                    {
                        var close = _close;
                        if (close != null)
                        {
                            // Defer so the callback stays fast; pass the click through
                            // so the app beneath still receives it.
                            _dispatcher.BeginInvoke(close);
                        }
                    }
                }
            }
            return User32.CallNextHookEx(_msHook, code, wParam, lParam);
        }

        // ---- OverlayKeyAction factory helpers ----

        private static OverlayKeyAction Action(OverlayKeyActionKind kind)
            => new OverlayKeyAction { Kind = kind };

        private static OverlayKeyAction Char(char c)
            => new OverlayKeyAction { Kind = OverlayKeyActionKind.AppendChar, Char = c };

        private static OverlayKeyAction Nudge(int dx, int dy, bool fast)
            => new OverlayKeyAction { Kind = OverlayKeyActionKind.Nudge, Dx = dx, Dy = dy, Fast = fast };
    }
}
