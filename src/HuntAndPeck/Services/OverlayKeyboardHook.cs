using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
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
        Leader,
        InsertToggle,
        CycleMonitorNext,
        CycleMonitorPrev,
        Nudge
    }

    /// <summary>
    /// A label-pan step tier. Each tier has its own configurable 4-key row (L,D,U,R) and
    /// a per-axis pixel step (read by the VM). Large defaults to "auto" (= one zone cell).
    /// </summary>
    internal enum NudgeTier { Small, Medium, Large }

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
        public NudgeTier Tier;  // Nudge step tier
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
        // GetAsyncKeyState, which misses a Capslock AutoHotkey has neutralized). STATIC:
        // a persistent tracker hook (armed at app startup, above AutoHotkey) keeps these
        // accurate even BEFORE an overlay opens -- e.g. holding Capslock and tapping
        // quadrant hotkeys (Capslock+j -> Ctrl+Shift+F2 opens the overlay mid-hold); the
        // per-overlay hook arms too late to see that Capslock keydown.
        private static bool _sAltHeld;
        private static bool _sCapsHeld;
        private static IntPtr _sTrackerHook = IntPtr.Zero;
        private static readonly User32.HookProc _sTrackerProc = TrackerKeyboardProc;

        // The delegates MUST be kept in fields: if they are garbage-collected
        // while Windows still holds the callback pointer, the process crashes.
        private readonly User32.HookProc _kbProc;
        private readonly User32.HookProc _msProc;
        private IntPtr _kbHook = IntPtr.Zero;
        private IntPtr _msHook = IntPtr.Zero;

        // Idle auto-close timer (OverlayAutoCloseSec). Null when the feature is off (0).
        private DispatcherTimer _autoCloseTimer;

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
            // The persistent tracker (installed above AutoHotkey at app startup) keeps
            // _sAltHeld/_sCapsHeld accurate, including modifiers held before this overlay
            // opened (which the per-overlay hook would arm too late to see).
            EnsurePersistentTracker();

            var hMod = Kernel32.GetModuleHandle(null);
            _kbHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _msHook = User32.SetWindowsHookEx(User32.WH_MOUSE_LL, _msProc, hMod, 0);

            StartAutoCloseTimer();
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
            if (_autoCloseTimer != null)
            {
                _autoCloseTimer.Stop();
                _autoCloseTimer = null;
            }
        }

        /// <summary>Starts the idle auto-close timer when OverlayAutoCloseSec > 0; else no-op.</summary>
        private void StartAutoCloseTimer()
        {
            var sec = OverlayActionConfig.ReadAutoCloseSec();
            if (sec > 0)
            {
                _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
                _autoCloseTimer.Tick += OnAutoCloseTick;
                _autoCloseTimer.Start();
            }
        }

        /// <summary>Restarts the idle timer (call on any captured overlay input).</summary>
        private void ResetAutoClose()
        {
            if (_autoCloseTimer != null)
            {
                _autoCloseTimer.Stop();
                _autoCloseTimer.Start();
            }
        }

        private void OnAutoCloseTick(object sender, EventArgs e)
        {
            // Don't auto-close while suspended (the user suspended to type into the app).
            if (_vm != null && !_vm.Suspended)
            {
                _close?.Invoke();
            }
        }

        public void Dispose() => Disarm();

        /// <summary>
        /// Installs a one-time, app-lifetime low-level keyboard hook (above AutoHotkey, since
        /// the app starts after AHK) that ONLY tracks physical Alt/Capslock held-state into
        /// the static fields. It never swallows keys (always CallNextHookEx) -- an inert
        /// observer. This keeps _sAltHeld/_sCapsHeld accurate even when an overlay opens
        /// while a modifier is already held (e.g. holding Capslock and tapping quadrant
        /// hotkeys), which the per-overlay hook misses (it arms after the modifier keydown,
        /// and GetAsyncKeyState misses an AHK-neutralized Capslock).
        /// </summary>
        public static void EnsurePersistentTracker()
        {
            if (_sTrackerHook != IntPtr.Zero)
            {
                return;
            }
            var hMod = Kernel32.GetModuleHandle(null);
            _sTrackerHook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _sTrackerProc, hMod, 0);
        }

        private static IntPtr TrackerKeyboardProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == User32.HC_ACTION)
            {
                int msg = wParam.ToInt32();
                bool down = msg == User32.WM_KEYDOWN || msg == User32.WM_SYSKEYDOWN;
                bool up = msg == User32.WM_KEYUP || msg == User32.WM_SYSKEYUP;
                if (down || up)
                {
                    var k = (User32.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(User32.KBDLLHOOKSTRUCT));
                    int vk = (int)k.vkCode;
                    if (vk == User32.VK_MENU || vk == User32.VK_LMENU || vk == User32.VK_RMENU)
                    {
                        _sAltHeld = down;
                    }
                    else if (vk == User32.VK_CAPITAL)
                    {
                        _sCapsHeld = down;
                    }
                }
            }
            return User32.CallNextHookEx(_sTrackerHook, code, wParam, lParam);
        }

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
                _sAltHeld = down;
            }
            else if (vk == User32.VK_CAPITAL)
            {
                _sCapsHeld = down;
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
            bool altHeld = _sAltHeld || IsDown(User32.VK_MENU);
            if (_vm != null && _vm.Suspended)
            {
                // Insert mode exit keys: while suspended everything passes through to
                // the app (that IS insert mode), EXCEPT a plain Esc/Q, which resumes
                // the overlay (vim semantics: `i` enters, Esc exits). Modifier chords
                // (Alt/Capslock-held, Ctrl+Esc, Win+.) keep passing through untouched.
                if (down && !altHeld && !_sCapsHeld
                    && (vk == User32.VK_ESCAPE || vk == User32.VK_Q)
                    && !IsDown(User32.VK_CONTROL)
                    && !IsDown(User32.VK_LWIN) && !IsDown(User32.VK_RWIN))
                {
                    _dispatcher.BeginInvoke(new Action(() => _vm.ExitSuspend()));
                    ResetAutoClose();
                    return new IntPtr(1);
                }
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }
            if (altHeld || _sCapsHeld)
            {
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }

            if (!down)
            {
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }

            bool shift = IsDown(User32.VK_SHIFT);
            // Shift alone is allowed (Shift+letter is still that letter). Ctrl or Win
            // means the key is a shortcut, not a label char, so it passes through --
            // EXCEPT the hjkl pan chords (Shift+hjkl / Ctrl+Shift+hjkl), which Classify
            // captures below. (Alt is already gated out above by the altHeld check.)
            bool ctrl = IsDown(User32.VK_CONTROL);
            bool win = IsDown(User32.VK_LWIN) || IsDown(User32.VK_RWIN);
            // Whether the dedicated arrows pan the labels (legacy) or pass through to
            // the app (default, ArrowKeyBehavior=Passthrough). Read per keydown so an
            // Options change hot-reloads immediately; EnsureFresh is a stat, not a parse.
            bool arrowPan = OverlayActionConfig.ReadArrowKeyBehavior() == ArrowKeyBehavior.Pan;

            // Extended-key flag: set for the dedicated arrow/Nav cluster, NOT for the
            // numeric keypad (whose Nav keys, with NumLock off, reuse the same VK codes).
            // Used to tell real arrows (pan, when arrowPan) from numpad arrows (always
            // pass through, so a numpad-mouse tool keeps working).
            bool extended = (k.flags & User32.LLKHF_EXTENDED) != 0;

            // The configured label-character set (uppercased HintCharacters). OEM
            // punctuation is captured as label input only when its char is in this
            // set; letters are always typeable. Read per keydown (EnsureFresh is a
            // stat, not a parse) so an Options change hot-reloads immediately.
            string labelChars = (OverlayActionConfig.ReadRawString("HintCharacters") ?? "").ToUpperInvariant();
            int[] nudgeSmall = OverlayActionConfig.ReadNudgeKeysSmall();
            int[] nudgeMedium = OverlayActionConfig.ReadNudgeKeysMedium();
            int[] nudgeLarge = OverlayActionConfig.ReadNudgeKeysLarge();
            var act = Classify(vk, shift, ctrl, win, extended, arrowPan, labelChars,
                nudgeSmall, nudgeMedium, nudgeLarge);
            if (act.Kind == OverlayKeyActionKind.None)
            {
                return User32.CallNextHookEx(_kbHook, code, wParam, lParam);
            }

            // Defer the real work off the hook callback, but swallow the key now so
            // it never reaches the app beneath.
            _dispatcher.BeginInvoke(Dispatch(act));
            ResetAutoClose();   // a captured key resets the idle auto-close timer
            return new IntPtr(1);
        }

        private static bool IsDown(int vKey) => (User32.GetAsyncKeyState(vKey) & 0x8000) != 0;

        /// <summary>
        /// Pure decode of a virtual-key code + modifier state into an overlay action.
        /// Kept free of any window/hook dependency so the full mapping is unit-testable.
        /// Optional params default to the common case (a real extended key whose arrows
        /// still pan) used by most tests; the hook passes every argument explicitly.
        /// </summary>
        /// <param name="shift">Shift held.</param>
        /// <param name="ctrl">Ctrl held (Alt is gated out before this runs).</param>
        /// <param name="win">Win held -- Win combos always pass through to the OS.</param>
        /// <param name="extended">Dedicated arrow/Nav cluster (LLKHF_EXTENDED), not numpad.</param>
        /// <param name="arrowPan">If true, dedicated arrows pan the labels (legacy
        /// ArrowKeyBehavior=Pan); if false they pass through to the app beneath.</param>
        internal static OverlayKeyAction Classify(int vkCode, bool shift, bool ctrl,
            bool win = false, bool extended = true, bool arrowPan = true,
            string labelChars = null,
            int[] nudgeSmall = null, int[] nudgeMedium = null, int[] nudgeLarge = null)
        {
            if (vkCode == User32.VK_ESCAPE) return Action(OverlayKeyActionKind.Escape);
            if (vkCode == User32.VK_SPACE) return Action(OverlayKeyActionKind.Leader);
            if (vkCode == User32.VK_TAB)
            {
                // Plain Tab / Shift+Tab cycle monitors. But Ctrl+Tab / Ctrl+Shift+Tab
                // (browser/app tab switch) and Win+Tab (Task View) are high-demand app/OS
                // shortcuts, so they pass through when Ctrl or Win is held.
                if (ctrl || win)
                {
                    return Action(OverlayKeyActionKind.None);
                }
                return Action(shift ? OverlayKeyActionKind.CycleMonitorPrev
                                     : OverlayKeyActionKind.CycleMonitorNext);
            }

            // Nudge chords: Shift + a configured tier key (no Ctrl/Win). Each tier's 4
            // keys are [L,D,U,R]; the matched position gives the direction. Plain row keys
            // (no Shift) still type hints. Ctrl+Shift+<row> is an app shortcut -> it passes
            // through (the old Ctrl+Shift+hjkl small-pan is retired; use the Small tier).
            if (shift && !ctrl && !win)
            {
                int dir;
                if (TryNudgeDir(vkCode, nudgeMedium ?? DefaultMediumKeys, out dir)) return Nudge(NudgeTier.Medium, DirDx(dir), DirDy(dir));
                if (TryNudgeDir(vkCode, nudgeLarge ?? DefaultLargeKeys, out dir)) return Nudge(NudgeTier.Large, DirDx(dir), DirDy(dir));
                if (TryNudgeDir(vkCode, nudgeSmall ?? DefaultSmallKeys, out dir)) return Nudge(NudgeTier.Small, DirDx(dir), DirDy(dir));
            }

            // Dedicated arrows: pan ONLY when arrowPan (legacy). When ArrowKeyBehavior
            // =Passthrough (default) they fall through to None so the app beneath gets
            // them (Excel/list focus nav). Numpad nav keys (extended==false) always pass
            // through regardless, so a numpad-mouse tool keeps working. Tier: plain arrow
            // = Medium, Shift+arrow = Large.
            if (extended && arrowPan)
            {
                var arrowTier = shift ? NudgeTier.Large : NudgeTier.Medium;
                if (vkCode == User32.VK_LEFT) return Nudge(arrowTier, -1, 0);
                if (vkCode == User32.VK_UP) return Nudge(arrowTier, 0, -1);
                if (vkCode == User32.VK_RIGHT) return Nudge(arrowTier, 1, 0);
                if (vkCode == User32.VK_DOWN) return Nudge(arrowTier, 0, 1);
            }

            // A Ctrl/Win chord (Alt is gated out above) is an app shortcut, not a label
            // char or overlay function, so it passes through. hjkl pan chords were
            // already handled above.
            if (!(ctrl || win))
            {
                // Q = close (Esc alias) -- kept direct for convenience. Q is therefore a
                // RESERVED letter: the default HintCharacters excludes it (a label
                // containing Q could never be typed), and <leader>q cannot fire (Q
                // classifies as Escape before label input, and HandleEscape cancels a
                // pending leader). I = insert mode (vim-style suspend) and is reserved
                // the same way: the default HintCharacters excludes it, and <leader>i
                // cannot fire (I classifies as InsertToggle before label/leader input).
                // Shift+I is still the Large nudge-up chord (classified above), and
                // Ctrl+I / Win+I pass through. Digits 0-9 are NOT aliases -- `1` was
                // unaliased, so all digits pass through to the app (LabelCharForVk
                // returns null for them, so they fall through to None).
                if (vkCode == User32.VK_Q) return Action(OverlayKeyActionKind.Escape);
                if (vkCode == User32.VK_I) return Action(OverlayKeyActionKind.InsertToggle);

                // Label-character input: letters are always typeable; OEM punctuation
                // only when its (US-layout) char is in the configured HintCharacters.
                char? c = LabelCharForVk(vkCode, labelChars);
                if (c.HasValue)
                {
                    return Char(c.Value);
                }
            }
            return Action(OverlayKeyActionKind.None);
        }

        /// <summary>
        /// Maps a virtual key to a label character if it is typeable as a label:
        /// letters A-Z always; OEM punctuation only when its US-layout char is in
        /// <paramref name="labelChars"/> (the configured HintCharacters, uppercased).
        /// Digits are reserved for overlay functions and are never returned here.
        /// </summary>
        private static char? LabelCharForVk(int vkCode, string labelChars)
        {
            if (vkCode >= User32.VK_A && vkCode <= User32.VK_Z)
            {
                return (char)('A' + (vkCode - User32.VK_A));
            }
            char oc = OemCharForVk(vkCode);
            if (oc != '\0' && labelChars != null && labelChars.IndexOf(oc) >= 0)
            {
                return oc;
            }
            return null;
        }

        /// <summary>US-layout mapping of OEM virtual keys to their unshifted char.</summary>
        private static char OemCharForVk(int vkCode)
        {
            switch (vkCode)
            {
                case User32.VK_OEM_COMMA: return ',';
                case User32.VK_OEM_PERIOD: return '.';
                case User32.VK_OEM_2: return '/';   // / ?
                case User32.VK_OEM_1: return ';';   // ; :
                case User32.VK_OEM_7: return '\'';  // ' "
                case User32.VK_OEM_4: return '[';   // [ {
                case User32.VK_OEM_6: return ']';   // ] }
                case User32.VK_OEM_5: return '\\';  // \ |
                default: return '\0';
            }
        }

        private Action Dispatch(OverlayKeyAction act)
        {
            switch (act.Kind)
            {
                case OverlayKeyActionKind.Escape:
                    return () => _vm.HandleEscape();
                case OverlayKeyActionKind.Leader:
                    // <Space> opens the leader dispatcher. Toggle semantics: a second
                    // <Space> while a leader is already pending cancels it.
                    return () => _vm.EnterLeader();
                case OverlayKeyActionKind.InsertToggle:
                    // Plain `i` enters insert mode (persistent suspend): the overlay
                    // stops capturing keys and hides its labels so you can type into
                    // the app beneath; q/Esc (intercepted in KeyboardProc while
                    // suspended) or the main hotkey resumes.
                    return () => _vm.EnterSuspend();
                case OverlayKeyActionKind.CycleMonitorNext:
                    return () => _vm.CycleMonitor(1);
                case OverlayKeyActionKind.CycleMonitorPrev:
                    return () => _vm.CycleMonitor(-1);
                case OverlayKeyActionKind.AppendChar:
                    char c = act.Char;
                    // A printable char means different things by phase: while a leader is
                    // pending it is a leader command key; in zone-pick it selects a zone;
                    // otherwise it is a label char. Branching here (not in the pure
                    // Classify) keeps Classify pure; the _vm.Is* flags reflect live state.
                    return () =>
                    {
                        if (_vm.IsLeaderPending) _vm.LeaderCommand(c);
                        else if (_vm.IsZonePick) _vm.SelectZone(c);
                        else _vm.AppendLabelChar(c);
                    };
                case OverlayKeyActionKind.Nudge:
                    int dx = act.Dx;
                    int dy = act.Dy;
                    var tier = act.Tier;
                    return () => _vm.Nudge(tier, dx, dy);
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

        private static OverlayKeyAction Nudge(NudgeTier tier, int dx, int dy)
            => new OverlayKeyAction { Kind = OverlayKeyActionKind.Nudge, Dx = dx, Dy = dy, Tier = tier };

        // ---- configurable nudge-key decode ----
        // Each tier's 4 keys are VK codes in [L, D, U, R] order. Keys enum values ARE VK
        // codes, so the int cast is what Classify compares against. Defaults: Small=m , . /,
        // Medium=h j k l, Large=u i o p (positional L,D,U,R like hjkl).
        private static readonly int[] DefaultSmallKeys =
            { (int)Keys.M, (int)Keys.Oemcomma, (int)Keys.OemPeriod, (int)Keys.Oem2 };
        private static readonly int[] DefaultMediumKeys =
            { (int)Keys.H, (int)Keys.J, (int)Keys.K, (int)Keys.L };
        private static readonly int[] DefaultLargeKeys =
            { (int)Keys.U, (int)Keys.I, (int)Keys.O, (int)Keys.P };

        /// <summary>True (and sets dir = 0=L,1=D,2=U,3=R) when <paramref name="vkCode"/> is keys[dir].</summary>
        private static bool TryNudgeDir(int vkCode, int[] keys, out int dir)
        {
            dir = -1;
            if (keys == null) return false;
            for (int i = 0; i < keys.Length && i < 4; i++)
            {
                if (vkCode == keys[i]) { dir = i; return true; }
            }
            return false;
        }

        // dir 0=L (-1,0), 1=D (0,1), 2=U (0,-1), 3=R (1,0)
        private static int DirDx(int dir) => dir == 0 ? -1 : dir == 3 ? 1 : 0;
        private static int DirDy(int dir) => dir == 1 ? 1 : dir == 2 ? -1 : 0;
    }
}
