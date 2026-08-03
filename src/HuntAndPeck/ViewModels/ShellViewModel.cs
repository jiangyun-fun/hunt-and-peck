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
        private readonly Func<bool> _isOverlayActive;
        private readonly Action _toggleOverlayMode;
        private readonly Action _closeOverlay;
        private readonly IHintLabelService _hintLabelService;
        private readonly IHintProviderService _hintProviderService;
        private readonly IDebugHintProviderService _debugHintProviderService;

        public ShellViewModel(
            Action<OverlayViewModel> showOverlay,
            Action<DebugOverlayViewModel> showDebugOverlay,
            Action<OptionsViewModel> showOptions,
            Func<bool> isOverlayActive,
            Action toggleOverlayMode,
            Action closeOverlay,
            IHintLabelService hintLabelService,
            IHintProviderService hintProviderService,
            IDebugHintProviderService debugHintProviderService,
            IKeyListenerService keyListener)
        {
            _showOverlay = showOverlay;
            _showDebugOverlay = showDebugOverlay;
            _showOptions = showOptions;
            _isOverlayActive = isOverlayActive;
            _toggleOverlayMode = toggleOverlayMode;
            _closeOverlay = closeOverlay;
            _hintLabelService = hintLabelService;
            var keyListener1 = keyListener;
            _hintProviderService = hintProviderService;
            _debugHintProviderService = debugHintProviderService;

            // Main overlay hotkey. Read once at startup from hap.exe.config
            // (HotkeyKey / HotkeyModifier); restart to apply a change, since the
            // global hotkey is registered once. Default: Ctrl+Shift+M (no Alt -- Alt
            // dismisses open context menus even inside a chord).
            keyListener1.HotKey = new HotKey
            {
                Keys = OverlayActionConfig.ReadHotkeyKey(Keys.M),
                Modifier = OverlayActionConfig.ReadHotkeyModifier(
                    KeyModifier.Control | KeyModifier.Shift)
            };

            // Dedicated one-shot hotkey: opens the overlay in ONE-SHOT mode regardless of the
            // configured OverlayTriggerMode (handy for a quick single click when the default is
            // Continuous). Read once at startup; restart to apply. Default Ctrl+Shift+, (Oemcomma).
            keyListener1.OneShotHotKey = new HotKey
            {
                Keys = OverlayActionConfig.ReadOneShotHotkeyKey(Keys.Oemcomma),
                Modifier = OverlayActionConfig.ReadOneShotHotkeyModifier(
                    KeyModifier.Control | KeyModifier.Shift)
            };

            // Quadrant hotkeys (Ctrl+Shift+F1..F4): open the overlay scoped to TL/TR/BL/BR.
            // Read once at startup; restart to apply. Default F1,F2,F3,F4 + Control,Shift.
            var quadrantKeys = OverlayActionConfig.ReadQuadrantHotkeyKeys();
            var quadrantMod = OverlayActionConfig.ReadQuadrantHotkeyModifier();
            keyListener1.QuadrantHotKeys = quadrantKeys
                .Select(k => new HotKey { Keys = k, Modifier = quadrantMod })
                .ToArray();

#if DEBUG
            keyListener1.DebugHotKey = new HotKey
            {
                Keys = Keys.OemSemicolon,
                Modifier = KeyModifier.Alt | KeyModifier.Shift
            };
#endif

            keyListener1.OnHotKeyActivated += _keyListener_OnHotKeyActivated;
            keyListener1.OnOneShotHotKeyActivated += _keyListener_OnOneShotHotKeyActivated;
            keyListener1.OnDebugHotKeyActivated += _keyListener_OnDebugHotKeyActivated;
            keyListener1.OnQuadrantHotKeyActivated += _keyListener_OnQuadrantHotKeyActivated;

            ShowOptionsCommand = new DelegateCommand(ShowOptions);
            ExitCommand = new DelegateCommand(Exit);
        }

        public DelegateCommand ShowOptionsCommand { get; }
        public DelegateCommand ExitCommand { get; }

        private async void _keyListener_OnHotKeyActivated(object sender, EventArgs e)
            => await OpenOverlayAsync(forceOneShot: false);

        private async void _keyListener_OnOneShotHotKeyActivated(object sender, EventArgs e)
            => await OpenOverlayAsync(forceOneShot: true);

        private async void _keyListener_OnQuadrantHotKeyActivated(int quadrant)
            => await OpenQuadrantOverlayAsync(quadrant);

        /// <summary>
        /// Opens the overlay. <paramref name="forceOneShot"/> (the dedicated one-shot hotkey)
        /// forces one-shot mode regardless of the configured <c>OverlayTriggerMode</c>; the main
        /// hotkey passes <c>false</c> and honors the config. If the overlay is already up, either
        /// hotkey toggles one-click &lt;-&gt; continuous (Grid only).
        /// </summary>
        private async Task OpenOverlayAsync(bool forceOneShot)
        {
            // Overlay already up: a 2nd hotkey press toggles one-click <-> continuous
            // (Grid only; Automation stays one-shot). Esc / a mouse click closes it.
            if (_isOverlayActive())
            {
                _toggleOverlayMode();
                return;
            }

            // Capture the foreground window on the UI thread, then enumerate off-thread.
            // The per-window cache (in the service) makes repeat presses on the same
            // window instant; the first press on a large tree (e.g. Chromium apps) still
            // takes seconds but runs off-thread so the UI does not freeze.
            var hWnd = User32.GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            var source = OverlayActionConfig.ReadHintSource();
            var gridSource = OverlayActionConfig.IsGridHintSource(source);
            // Grid + Screen: build one grid per monitor so Tab can cycle between them.
            // Otherwise (Automation, Grid + Window) use a single session + taskbar merge.
            var cycleCapable = gridSource && OverlayActionConfig.ReadHintBounds() == HintBounds.Screen;
            // Continuous mode is meaningful only for Grid (its labels are fixed screen
            // points that survive navigation); Automation stays one-shot. forceOneShot
            // (the one-shot hotkey) overrides a Continuous config default.
            var continuous = OverlayActionConfig.ComputeIsContinuous(
                forceOneShot, gridSource, OverlayActionConfig.ReadTriggerMode());

            // Layout presets (Grid only): when GridLayouts lists more than one geometry,
            // `;` cycles them live and the active one is persisted (ActiveLayout). Null
            // (Automation, or no GridLayouts configured) preserves today's behavior.
            var layouts = gridSource ? GridLayoutConfig.ReadGridLayouts() : new List<GridLayout>();
            bool hasLayouts = layouts.Count > 0;
            int activeLayout = hasLayouts ? GridLayoutConfig.ReadActiveLayout(layouts.Count) : 0;
            GridLayout layout = hasLayouts ? layouts[activeLayout] : null;

            var sw = Stopwatch.StartNew();
            if (cycleCapable)
            {
                // Type-to-zoom zones (opt-in): open with cols×rows large zone labels
                // over the foreground monitor; type a zone label to drill in. Falls
                // through to the full-monitor grid when zones are off or unavailable.
                if (OverlayActionConfig.ReadZoneZoomEnabled())
                {
                    var zoneVm = BuildZoneOverlayViewModel(hWnd);
                    if (zoneVm != null)
                    {
                        sw.Stop();
                        TimingLog.Log("enum+merge " + sw.ElapsedMilliseconds + "ms  zones");
                        ConfigureTriggerMode(zoneVm, gridSource, continuous);
                        _showOverlay(zoneVm);
                        return;
                    }
                }

                var built = await Task.Run(() => BuildMonitorSessions(hWnd, layout));
                sw.Stop();
                var cur = built.Sessions.Count > 0 ? built.Sessions[built.Current] : null;
                TimingLog.Log("enum+merge " + sw.ElapsedMilliseconds + "ms  monitors="
                    + built.Sessions.Count + "  hints="
                    + (cur != null && cur.Hints != null ? cur.Hints.Count : 0));
                if (built.Sessions.Count > 0)
                {
                    var vm = hasLayouts
                        ? new OverlayViewModel(built.Sessions, built.Current, _hintLabelService,
                            layouts, activeLayout, idx => BuildMonitorSessionList(hWnd, layouts[idx]))
                        : new OverlayViewModel(built.Sessions, built.Current, _hintLabelService);
                    ConfigureTriggerMode(vm, gridSource, continuous);
                    _showOverlay(vm);
                }
            }
            else
            {
                var session = await Task.Run(() => MergeWithTaskbar(_hintProviderService.EnumHints(hWnd, layout)));
                sw.Stop();
                TimingLog.Log("enum+merge " + sw.ElapsedMilliseconds + "ms  hints="
                    + (session != null && session.Hints != null ? session.Hints.Count : 0));
                if (session != null)
                {
                    var vm = hasLayouts
                        ? new OverlayViewModel(new List<HintSession> { session }, 0, _hintLabelService,
                            layouts, activeLayout, idx => SingleSessionWithTaskbar(hWnd, layouts[idx]))
                        : new OverlayViewModel(session, _hintLabelService);
                    ConfigureTriggerMode(vm, gridSource, continuous);
                    _showOverlay(vm);
                }
            }
        }

        /// <summary>
        /// Sets the overlay's trigger-mode capability/initial state. Continuous only when
        /// the source is Grid and the config default is Continuous.
        /// </summary>
        private static void ConfigureTriggerMode(OverlayViewModel vm, bool gridSource, bool continuous)
        {
            vm.ContinuousCapable = gridSource;
            vm.IsContinuous = continuous;
        }

        /// <summary>
        /// Opens the overlay scoped to one screen quadrant (Ctrl+Shift+F1..F4): a dense
        /// uniform grid (ZoneGridStep) over that quarter, on a full-screen overlay (labels
        /// only in the quadrant). Mirrors <see cref="OpenOverlayAsync"/> (active → toggle).
        /// </summary>
        private async Task OpenQuadrantOverlayAsync(int quadrant)
        {
            var hWnd = User32.GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            // A quadrant is always a Grid (ignores HintSource); Continuous follows config.
            var continuous = OverlayActionConfig.ComputeIsContinuous(
                false, true, OverlayActionConfig.ReadTriggerMode());

            // Build the new quadrant VM off-thread while the current overlay (if any) stays
            // visible, then swap it in. A quadrant hotkey ALWAYS shows its quadrant -- it does
            // NOT toggle one-click/continuous like the main hotkey does on a 2nd press.
            var vm = await Task.Run(() => BuildQuadrantOverlayViewModel(hWnd, quadrant));
            if (vm == null)
            {
                return;
            }
            if (_isOverlayActive())
            {
                _closeOverlay();
            }
            ConfigureTriggerMode(vm, true, continuous);
            _showOverlay(vm);
        }

        /// <summary>
        /// Builds a multi-session overlay covering all four quadrants (TL/TR/BL/BR) at
        /// ZoneGridStep, one <see cref="HintSession"/> per quarter, starting on the pressed
        /// <paramref name="quadrant"/>. Each session is rebased to the full monitor so the
        /// overlay stays full-screen with labels only in the active quadrant; the VM is
        /// marked <see cref="OverlayViewModel.IsQuadrantMode"/> so plain Tab cycles the
        /// quadrants via <see cref="OverlayViewModel.CycleMonitor"/> (the same path monitor
        /// cycling uses). Returns null on a degenerate monitor or when the pressed quadrant
        /// itself yields no grid points.
        /// </summary>
        private OverlayViewModel BuildQuadrantOverlayViewModel(IntPtr hWnd, int quadrant)
        {
            if (quadrant < 0 || quadrant > 3)
            {
                return null;
            }
            var screen = Screen.FromHandle(hWnd) ?? Screen.PrimaryScreen;
            if (screen == null)
            {
                return null;
            }
            var monitor = new System.Windows.Rect(
                screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);

            // SliceIntoZones(monitor, 2, 2) -> [TL, TR, BL, BR] in scan order, so the
            // quadrant index (0..3) the hotkey carries maps 1:1 to the list position.
            var quadRects = ZoneService.SliceIntoZones(monitor, 2, 2);

            int step = OverlayActionConfig.ReadZoneGridStep();
            var layout = new GridLayout
            {
                EdgeStep = step,
                CenterStep = step,
                Inset = 0,
                BandPercent = 0,
                DenseRegions = "Center"
            };

            // Build exactly four sessions (one per quadrant) so _currentSession stays the
            // quadrant index and the Q n/4 badge is correct. A uniform grid always yields
            // points for a non-zero rect, but defensively substitute an empty session if a
            // non-pressed quarter comes back null (0 hints -> GetHintStrings(0) is empty ->
            // renders nothing; Tab still cycles through it). The pressed quadrant must be
            // non-empty, else give up (return null) as before.
            var sessions = new List<HintSession>(4);
            for (int q = 0; q < 4; q++)
            {
                var qr = quadRects[q];
                var s = _hintProviderService.EnumGridHintsForBounds(hWnd, qr, layout);
                if (s == null || s.Hints == null || s.Hints.Count == 0)
                {
                    if (q == quadrant)
                    {
                        return null;
                    }
                    s = new HintSession
                    {
                        Hints = new List<Hint>(),
                        OwningWindow = hWnd,
                        OwningWindowBounds = monitor
                    };
                }
                else
                {
                    // Full-screen overlay: rebase hint render-rects from the quadrant origin
                    // to the monitor origin. PointHint cursor targets are already absolute,
                    // so clicks stay correct.
                    RebaseTo(s.Hints, qr.Left - monitor.Left, qr.Top - monitor.Top);
                    s.OwningWindowBounds = monitor;
                }
                sessions.Add(s);
            }

            return new OverlayViewModel(sessions, quadrant, _hintLabelService)
            {
                IsQuadrantMode = true
            };
        }

        private struct MonitorSessions
        {
            public List<HintSession> Sessions;
            public int Current;
        }

        /// <summary>
        /// Builds one Grid session per monitor, sorted left-to-right then top-to-bottom,
        /// starting on the monitor the foreground window is on. For monitor cycling
        /// (Grid + Screen). <paramref name="layout"/> selects the geometry preset (null =
        /// the legacy flat keys). No taskbar merge: each monitor's full-screen grid already
        /// covers its own taskbar strip, and secondary monitors have no taskbar.
        /// </summary>
        private MonitorSessions BuildMonitorSessions(IntPtr hWnd, GridLayout layout)
        {
            var screens = Screen.AllScreens
                .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
                .ToList();

            var sessions = BuildSessionsForScreens(hWnd, screens, layout);

            var fgScreen = Screen.FromHandle(hWnd);
            var current = screens.FindIndex(s => fgScreen != null && s.Bounds.Equals(fgScreen.Bounds));
            if (current < 0)
            {
                current = 0;
            }
            return new MonitorSessions { Sessions = sessions, Current = current };
        }

        /// <summary>
        /// Layout-cycling rebuild target (Grid + Screen): regenerates the per-monitor
        /// sessions for a new preset. The current-monitor index is kept by the overlay VM
        /// (clamped), so the user stays on the monitor they are viewing after a switch.
        /// </summary>
        private List<HintSession> BuildMonitorSessionList(IntPtr hWnd, GridLayout layout)
        {
            var screens = Screen.AllScreens
                .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
                .ToList();
            return BuildSessionsForScreens(hWnd, screens, layout);
        }

        private List<HintSession> BuildSessionsForScreens(IntPtr hWnd, List<Screen> screens, GridLayout layout)
        {
            var sessions = new List<HintSession>(screens.Count);
            foreach (var screen in screens)
            {
                var b = screen.Bounds;
                sessions.Add(_hintProviderService.EnumGridHintsForBounds(
                    hWnd, new System.Windows.Rect(b.X, b.Y, b.Width, b.Height), layout));
            }
            return sessions;
        }

        /// <summary>
        /// Builds a type-to-zoom zone <see cref="OverlayViewModel"/> for Grid + Screen +
        /// ZoneZoomEnabled. The overlay opens with cols×rows large zone labels over the
        /// FOREGROUND monitor only (Tab monitor-cycling is disabled in zone mode); typing
        /// a zone label drills into a uniform fine-grid lens (ZoneGridStep) centered on the
        /// zone (ZoneWidth×ZoneHeight, default the auto cell size). The lens session is
        /// rebased to the full monitor so the overlay stays full-screen (badge centered,
        /// nothing clipped). Returns null (-> caller falls back to the full-monitor grid)
        /// when cols*rows exceeds HintCharacters (zone labels would not be single-char).
        /// </summary>
        private OverlayViewModel BuildZoneOverlayViewModel(IntPtr hWnd)
        {
            var screen = Screen.FromHandle(hWnd) ?? Screen.PrimaryScreen;
            if (screen == null)
            {
                return null;
            }
            var monitor = new System.Windows.Rect(
                screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);

            int cols = OverlayActionConfig.ReadZoneCols();
            int rows = OverlayActionConfig.ReadZoneRows();
            int zoneCount = cols * rows;

            // Gate: every zone needs a single-char label, so the zone count must fit the
            // configured HintCharacters. On failure, return null so the caller falls back
            // to the full-monitor grid instead of ambiguous multi-char zones.
            int hintChars = HintCharacterCount();
            if (zoneCount <= 0 || zoneCount > hintChars)
            {
                return null;
            }

            // The in-zone grid is uniform at ZoneGridStep (Center-only => edge-to-edge fill),
            // so translating it by one cell-width coincides with the next zone (pan-lens).
            int step = OverlayActionConfig.ReadZoneGridStep();
            var zoneLayout = new GridLayout
            {
                EdgeStep = step,
                CenterStep = step,
                Inset = 0,
                BandPercent = 0,
                DenseRegions = "Center"
            };

            var pickSession = ZoneService.BuildPickSession(hWnd, monitor, cols, rows, 10.0);
            return new OverlayViewModel(
                pickSession,
                _hintLabelService,
                monitor,
                cols,
                rows,
                OverlayActionConfig.ReadZoneFontSize(),
                OverlayActionConfig.ReadZoneWidth(),
                OverlayActionConfig.ReadZoneHeight(),
                OverlayActionConfig.ReadZoneReturnToPickOnFire(),
                lensRect =>
                {
                    var s = _hintProviderService.EnumGridHintsForBounds(hWnd, lensRect, zoneLayout);
                    if (s != null)
                    {
                        // Keep the overlay full-screen: rebase hint render-rects from the
                        // lens origin to the monitor origin and report the full monitor as
                        // OwningWindowBounds. PointHint cursor targets are already absolute,
                        // so clicks stay correct.
                        RebaseTo(s.Hints, lensRect.Left - monitor.Left, lensRect.Top - monitor.Top);
                        s.OwningWindowBounds = monitor;
                    }
                    return s;
                });
        }

        /// <summary>
        /// Distinct char count in the configured HintCharacters (for the zone-count
        /// gate). Mirrors HintLabelService: a blank HintCharacters means the 14-char
        /// home-row default.
        /// </summary>
        private static int HintCharacterCount()
        {
            var raw = OverlayActionConfig.ReadRawString("HintCharacters");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 14;
            }
            var n = raw.Trim().ToUpperInvariant().Distinct().Count();
            return n > 0 ? n : 14;
        }

        /// <summary>
        /// Layout-cycling rebuild target (single-session Grid + Window): regenerates the
        /// foreground session with a new preset and re-merges the taskbar. Returns a 0- or
        /// 1-element list so the single- and multi-monitor rebuild shapes both satisfy the
        /// overlay VM's Func&lt;int, IList&lt;HintSession&gt;&gt; rebuild delegate.
        /// </summary>
        private IList<HintSession> SingleSessionWithTaskbar(IntPtr hWnd, GridLayout layout)
        {
            var session = MergeWithTaskbar(_hintProviderService.EnumHints(hWnd, layout));
            return session == null ? new List<HintSession>() : new List<HintSession> { session };
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
