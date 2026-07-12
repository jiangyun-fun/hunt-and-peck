using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using HuntAndPeck.Models;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using HuntAndPeck.Services.Interfaces;
using Application = System.Windows.Application;

namespace HuntAndPeck.ViewModels
{
    internal class ShellViewModel
    {
        private readonly Action<OverlayViewModel> _showOverlay;
        private readonly Action<DebugOverlayViewModel> _showDebugOverlay;
        private readonly Action<OptionsViewModel> _showOptions;
        private readonly IHintLabelService _hintLabelService;
        private readonly IHintProviderService _hintProviderService;
        private readonly IDebugHintProviderService _debugHintProviderService;

        public ShellViewModel(
            Action<OverlayViewModel> showOverlay,
            Action<DebugOverlayViewModel> showDebugOverlay,
            Action<OptionsViewModel> showOptions,
            IHintLabelService hintLabelService,
            IHintProviderService hintProviderService,
            IDebugHintProviderService debugHintProviderService,
            IKeyListenerService keyListener)
        {
            _showOverlay = showOverlay;
            _showDebugOverlay = showDebugOverlay;
            _showOptions = showOptions;
            _hintLabelService = hintLabelService;
            var keyListener1 = keyListener;
            _hintProviderService = hintProviderService;
            _debugHintProviderService = debugHintProviderService;

            // Main overlay hotkey. Read once at startup from hap.exe.config
            // (HotkeyKey / HotkeyModifier); restart to apply a change, since the
            // global hotkey is registered once. Default: Ctrl+Shift+Alt+F.
            keyListener1.HotKey = new HotKey
            {
                Keys = OverlayActionConfig.ReadHotkeyKey(Keys.F),
                Modifier = OverlayActionConfig.ReadHotkeyModifier(
                    KeyModifier.Control | KeyModifier.Alt | KeyModifier.Shift)
            };

#if DEBUG
            keyListener1.DebugHotKey = new HotKey
            {
                Keys = Keys.OemSemicolon,
                Modifier = KeyModifier.Alt | KeyModifier.Shift
            };
#endif

            keyListener1.OnHotKeyActivated += _keyListener_OnHotKeyActivated;
            keyListener1.OnDebugHotKeyActivated += _keyListener_OnDebugHotKeyActivated;

            ShowOptionsCommand = new DelegateCommand(ShowOptions);
            ExitCommand = new DelegateCommand(Exit);
        }

        public DelegateCommand ShowOptionsCommand { get; }
        public DelegateCommand ExitCommand { get; }

        private async void _keyListener_OnHotKeyActivated(object sender, EventArgs e)
        {
            // Capture the foreground window on the UI thread, then enumerate off-thread.
            // The per-window cache (in the service) makes repeat presses on the same
            // window instant; the first press on a large tree (e.g. Chromium apps) still
            // takes seconds but runs off-thread so the UI does not freeze.
            var hWnd = User32.GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            // Grid + Screen: build one grid per monitor so Tab can cycle between them.
            // Otherwise (Automation, Grid + Window) use a single session + taskbar merge.
            var cycleCapable = OverlayActionConfig.IsGridHintSource(OverlayActionConfig.ReadHintSource())
                               && OverlayActionConfig.ReadHintBounds() == HintBounds.Screen;

            var sw = Stopwatch.StartNew();
            if (cycleCapable)
            {
                var built = await Task.Run(() => BuildMonitorSessions(hWnd));
                sw.Stop();
                var cur = built.Sessions.Count > 0 ? built.Sessions[built.Current] : null;
                TimingLog.Log("enum+merge " + sw.ElapsedMilliseconds + "ms  monitors="
                    + built.Sessions.Count + "  hints="
                    + (cur != null && cur.Hints != null ? cur.Hints.Count : 0));
                if (built.Sessions.Count > 0)
                {
                    var vm = new OverlayViewModel(built.Sessions, built.Current, _hintLabelService);
                    _showOverlay(vm);
                }
            }
            else
            {
                var session = await Task.Run(() => MergeWithTaskbar(_hintProviderService.EnumHints(hWnd)));
                sw.Stop();
                TimingLog.Log("enum+merge " + sw.ElapsedMilliseconds + "ms  hints="
                    + (session != null && session.Hints != null ? session.Hints.Count : 0));
                if (session != null)
                {
                    var vm = new OverlayViewModel(session, _hintLabelService);
                    _showOverlay(vm);
                }
            }
        }

        private struct MonitorSessions
        {
            public List<HintSession> Sessions;
            public int Current;
        }

        /// <summary>
        /// Builds one Grid session per monitor, sorted left-to-right then top-to-bottom,
        /// starting on the monitor the foreground window is on. For monitor cycling
        /// (Grid + Screen). No taskbar merge: each monitor's full-screen grid already
        /// covers its own taskbar strip, and secondary monitors have no taskbar.
        /// </summary>
        private MonitorSessions BuildMonitorSessions(IntPtr hWnd)
        {
            var screens = Screen.AllScreens
                .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
                .ToList();

            var fgScreen = Screen.FromHandle(hWnd);
            var current = screens.FindIndex(s => fgScreen != null && s.Bounds.Equals(fgScreen.Bounds));
            if (current < 0)
            {
                current = 0;
            }

            var sessions = new List<HintSession>(screens.Count);
            foreach (var screen in screens)
            {
                var b = screen.Bounds;
                sessions.Add(_hintProviderService.EnumGridHintsForBounds(
                    hWnd, new System.Windows.Rect(b.X, b.Y, b.Width, b.Height)));
            }
            return new MonitorSessions { Sessions = sessions, Current = current };
        }

        /// <summary>
        /// Merges the taskbar's hints into the foreground session so both show in one
        /// overlay. The overlay is enlarged to the union of the two windows, and every
        /// hint's bounds are re-based to that union origin. Mutating BoundingRectangle
        /// is safe: Grid sessions are fresh per press, and Automation sessions are
        /// re-read (RefreshSessionPositions) on the next cache hit.
        /// </summary>
        private HintSession MergeWithTaskbar(HintSession foreground)
        {
            if (foreground == null)
            {
                return null;
            }

            // Grid + Screen: the foreground grid already spans the full monitor (taskbar
            // strip included), so enumerating the taskbar would generate a SECOND
            // full-screen grid and stack two labels at every cell. Skip it. Window mode
            // (window grid does not reach the taskbar) and Automation mode (the taskbar
            // adds its own real controls the foreground walk misses) still merge.
            if (!OverlayActionConfig.ShouldMergeTaskbar(
                    OverlayActionConfig.ReadHintSource(),
                    OverlayActionConfig.ReadHintBounds()))
            {
                return foreground;
            }

            var taskbarHWnd = User32.FindWindow("Shell_traywnd", "");
            if (taskbarHWnd == IntPtr.Zero || taskbarHWnd == foreground.OwningWindow)
            {
                return foreground;
            }

            // Only merge the taskbar when it shares a monitor with the foreground window.
            // Shell_traywnd lives on the primary monitor; merging it when the target is on
            // a secondary monitor would union the two monitors and stretch the overlay
            // across both displays.
            var fgScreen = Screen.FromHandle(foreground.OwningWindow);
            var taskbarScreen = Screen.FromHandle(taskbarHWnd);
            if (fgScreen == null || taskbarScreen == null ||
                !fgScreen.Bounds.Equals(taskbarScreen.Bounds))
            {
                return foreground;
            }

            var taskbar = _hintProviderService.EnumHints(taskbarHWnd);
            if (taskbar == null || taskbar.Hints == null || taskbar.Hints.Count == 0)
            {
                return foreground;
            }

            var fb = foreground.OwningWindowBounds;
            var tb = taskbar.OwningWindowBounds;
            var union = System.Windows.Rect.Union(fb, tb);

            RebaseTo(foreground.Hints, fb.Left - union.Left, fb.Top - union.Top);
            RebaseTo(taskbar.Hints, tb.Left - union.Left, tb.Top - union.Top);

            var merged = new List<Hint>(foreground.Hints.Count + taskbar.Hints.Count);
            merged.AddRange(foreground.Hints);
            merged.AddRange(taskbar.Hints);

            return new HintSession
            {
                Hints = merged,
                OwningWindow = foreground.OwningWindow,
                OwningWindowBounds = union
            };
        }

        private static void RebaseTo(IList<Hint> hints, double dx, double dy)
        {
            for (int i = 0; i < hints.Count; i++)
            {
                var h = hints[i];
                var br = h.BoundingRectangle;
                h.BoundingRectangle = new System.Windows.Rect(br.Left + dx, br.Top + dy, br.Width, br.Height);
            }
        }

        private void _keyListener_OnDebugHotKeyActivated(object sender, EventArgs e)
        {
            var session = _debugHintProviderService.EnumDebugHints();
            if (session != null)
            {
                var vm = new DebugOverlayViewModel(session);
                _showDebugOverlay(vm);
            }
        }

        public void Exit()
        {
            Application.Current.Shutdown();
        }

        public void ShowOptions()
        {
            var vm = new OptionsViewModel();
            _showOptions(vm);
        }
    }
}
