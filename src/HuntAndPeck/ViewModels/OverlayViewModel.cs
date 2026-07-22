using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using HuntAndPeck.Models;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using HuntAndPeck.Services.Interfaces;

namespace HuntAndPeck.ViewModels
{
    internal class OverlayViewModel : NotifyPropertyChanged
    {
        private Rect _bounds;
        private ObservableCollection<HintViewModel> _hints = new ObservableCollection<HintViewModel>();
        private double _offsetX;
        private double _offsetY;
        private readonly IList<ClickAction> _modeOrder;
        private int _modeIndex;

        private readonly IHintLabelService _hintLabelService;
        private readonly string _fontSizeRaw;
        private readonly double _pillOpacity;
        private readonly double _dimOpacity;
        private readonly IList<HintSession> _sessions;
        private int _currentSession;
        private string _match = "";
        private bool _continuousCapable;
        private bool _isContinuous;
        private bool _dimmed;
        private bool _suspended;

        /// <summary>
        /// Single-session ctor: Automation, Grid+Window, and the headless /hint and
        /// /tray entry points. Wraps the session as a one-element list (Tab is a no-op).
        /// </summary>
        public OverlayViewModel(HintSession session, IHintLabelService hintLabelService)
            : this(new List<HintSession> { session }, 0, hintLabelService) { }

        /// <summary>
        /// Multi-session ctor for monitor cycling (Grid + Screen): one session per
        /// monitor, starting at <paramref name="current"/>. Tab/Shift+Tab cycle the
        /// displayed monitor.
        /// </summary>
        public OverlayViewModel(IList<HintSession> sessions, int current, IHintLabelService hintLabelService)
        {
            _hintLabelService = hintLabelService;
            _sessions = sessions ?? new List<HintSession>();
            _currentSession = _sessions.Count == 0
                ? 0
                : ((current % _sessions.Count) + _sessions.Count) % _sessions.Count;
            _modeOrder = OverlayActionConfig.ReadClickActionOrder();
            _modeIndex = 0; // start on the first mode (Left, by default)

            // Read the font size ONCE for the whole overlay. Re-reading the config
            // file per hint (via ReadHintFontSize) made overlay build O(N) in disk
            // reads and dominated latency at high label counts.
            _fontSizeRaw = OverlayActionConfig.ReadHintFontSize()
                ?? HuntAndPeck.Properties.Settings.Default.FontSize;
            // Pill fill opacity (0-1) read once per overlay; bound to HintCanvas.
            _pillOpacity = OverlayActionConfig.ReadHintPillOpacity();
            // Dimmed-label opacity (0-1) read once per overlay; used by LabelOpacity.
            _dimOpacity = OverlayActionConfig.ReadHintDimOpacity();

            if (_sessions.Count > 0)
            {
                LoadSession(_sessions[_currentSession]);
            }
        }

        /// <summary>
        /// Loads a session: sets Bounds and rebuilds the hint labels. Each monitor has
        /// its own point count, so labels are regenerated per monitor. Replacing the
        /// Hints collection (rather than clearing in place) makes HintCanvas rebuild its
        /// cached visuals.
        /// </summary>
        private void LoadSession(HintSession session)
        {
            _bounds = session.OwningWindowBounds;
            var labels = _hintLabelService.GetHintStrings(session.Hints.Count);
            var fresh = new ObservableCollection<HintViewModel>();
            for (int i = 0; i < labels.Count; ++i)
            {
                var hint = session.Hints[i];
                fresh.Add(new HintViewModel(hint, _fontSizeRaw)
                {
                    Label = labels[i],
                    Active = true    // all highlighted (yellow) at init / on monitor switch
                });
            }
            Hints = fresh;
            _match = "";
            NotifyOfPropertyChange(nameof(MatchString));
            NotifyOfPropertyChange(nameof(Bounds));
        }

        /// <summary>
        /// Cycles to the next (delta = +1, Tab) or previous (delta = -1, Shift+Tab)
        /// monitor's session, wrapping around. No-op when there is only one session.
        /// Resets the pan offset; the caller clears the typed prefix.
        /// </summary>
        public void CycleMonitor(int delta)
        {
            if (_sessions == null || _sessions.Count <= 1)
            {
                return;
            }
            _currentSession = (_currentSession + delta + _sessions.Count) % _sessions.Count;
            LoadSession(_sessions[_currentSession]);
            OffsetX = 0;
            OffsetY = 0;
        }

        /// <summary>
        /// Bounds in logical screen coordiantes
        /// </summary>
        public Rect Bounds
        {
            get { return _bounds; }
            set { _bounds = value; NotifyOfPropertyChange(); }
        }

        /// <summary>Grid pan offset X (px). Bound to the label panel's TranslateTransform.</summary>
        public double OffsetX
        {
            get { return _offsetX; }
            set { _offsetX = value; NotifyOfPropertyChange(); }
        }

        /// <summary>Grid pan offset Y (px). Bound to the label panel's TranslateTransform.</summary>
        public double OffsetY
        {
            get { return _offsetY; }
            set { _offsetY = value; NotifyOfPropertyChange(); }
        }

        /// <summary>
        /// Pill fill opacity (0-1), bound to HintCanvas. Softens the vivid yellow; the
        /// text stays fully opaque. Hot-reload via the HintPillOpacity config key.
        /// </summary>
        public double PillOpacity => _pillOpacity;

        private ClickAction CurrentAction
        {
            get { return _modeOrder[_modeIndex]; }
        }

        /// <summary>Human-readable name of the current click mode, for the badge.</summary>
        public string CurrentModeName
        {
            get
            {
                switch (CurrentAction)
                {
                    case ClickAction.Left: return "LEFT CLICK";
                    case ClickAction.Right: return "RIGHT CLICK";
                    case ClickAction.Double: return "DOUBLE CLICK";
                    default: return "MOVE ONLY";
                }
            }
        }

        /// <summary>Badge background color for the current click mode.</summary>
        public SolidColorBrush CurrentModeBrush
        {
            get
            {
                switch (CurrentAction)
                {
                    case ClickAction.Left: return Brushes.Yellow;
                    case ClickAction.Right: return Brushes.LightSalmon;
                    case ClickAction.Double: return Brushes.LightGreen;
                    default: return Brushes.LightSkyBlue;
                }
            }
        }

        public ObservableCollection<HintViewModel> Hints
        {
            get { return _hints; }
            set { _hints = value; NotifyOfPropertyChange(); }
        }

        public Action CloseOverlay { get; set; }

        /// <summary>
        /// True when the hint source is Grid (continuous mode is meaningful). Automation
        /// stays one-shot because its labels go stale on navigation.
        /// </summary>
        public bool ContinuousCapable
        {
            get { return _continuousCapable; }
            set { _continuousCapable = value; }
        }

        /// <summary>
        /// Continuous mode: the overlay stays up after each click (reset for the next
        /// label) until Esc / a mouse click. One-click (default): closes after the first
        /// click. Toggled at runtime by pressing the hotkey again (Grid only).
        /// </summary>
        public bool IsContinuous
        {
            get { return _isContinuous; }
            set { _isContinuous = value; NotifyOfPropertyChange(nameof(TriggerModeLabel)); }
        }

        /// <summary>Overlay badge: the current trigger mode (or SUSPENDED).</summary>
        public string TriggerModeLabel => _suspended ? "SUSPENDED"
            : (_isContinuous ? "CONTINUOUS" : "ONE-SHOT");

        /// <summary>Flips one-click &lt;-&gt; continuous. No-op for non-Grid (Automation).</summary>
        public void ToggleContinuous()
        {
            if (!_continuousCapable)
            {
                return;
            }
            IsContinuous = !_isContinuous;
        }

        /// <summary>
        /// Persistent suspend (backslash): the overlay stops capturing keys AND hides its
        /// labels (opacity 0), leaving only the SUSPENDED status, so you can type into the
        /// app beneath (vimium, Excel) with zero key collision. Resume by pressing the
        /// main hotkey again (Ctrl+Shift+M / Capslock+f); Esc closes.
        /// </summary>
        public bool Suspended
        {
            get { return _suspended; }
            set
            {
                _suspended = value;
                NotifyOfPropertyChange(nameof(LabelOpacity));
                NotifyOfPropertyChange(nameof(TriggerModeLabel));
                NotifyOfPropertyChange(nameof(ClickModeBadgeVisibility));
            }
        }

        /// <summary>
        /// Dimmed (backtick): labels drop to a low opacity so the text behind is readable,
        /// but keys stay captured so you can still type a label. Toggle (backtick again
        /// restores). Note: opacity-dim couples label contrast to the background, so it is
        /// harder to see on dark surfaces -- accepted tradeoff for the simpler look.
        /// </summary>
        public bool Dimmed
        {
            get { return _dimmed; }
            set { _dimmed = value; NotifyOfPropertyChange(nameof(LabelOpacity)); }
        }

        /// <summary>
        /// Render opacity for the label canvas: 0 (hidden) when suspended, the configured
        /// dim level when dimmed (backtick), full otherwise. Base mode relies on the
        /// semi-transparent pill fill (HintCanvas) for its slight see-through, not on a
        /// canvas-wide dim, so the text stays crisp.
        /// </summary>
        public double LabelOpacity => _suspended ? 0.0 : (_dimmed ? _dimOpacity : 1.0);

        /// <summary>
        /// The click-mode badge is hidden while suspended so only the SUSPENDED status
        /// shows. Exposed as Visibility so the XAML binds directly (no converter needed).
        /// </summary>
        public Visibility ClickModeBadgeVisibility => _suspended ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Toggles dim mode (backtick): full &lt;-&gt; dimmed labels.</summary>
        public void ToggleDimmed() { Dimmed = !Dimmed; }

        /// <summary>Enters persistent suspend (backslash). Resume via the main hotkey.</summary>
        public void EnterSuspend() { Suspended = true; }

        /// <summary>
        /// The typed label prefix, for display (bound one-way to the TextBox). Input
        /// arrives via the global keyboard hook (OverlayKeyboardHook.AppendLabelChar),
        /// not via a focused TextBox, so the overlay can stay non-activated and not
        /// dismiss an open context menu.
        /// </summary>
        public string MatchString => _match;

        /// <summary>Appends one typed label character and runs the prefix match.</summary>
        public void AppendLabelChar(char c)
        {
            _match += char.ToUpperInvariant(c);
            NotifyOfPropertyChange(nameof(MatchString));
            ApplyMatch(_match);
        }

        /// <summary>
        /// Clears the typed prefix and re-highlights every label (yellow) so the next
        /// label is typeable. Used by the continuous-mode reset after a click;
        /// LoadSession handles the initial state and the per-monitor reset itself.
        /// </summary>
        public void ClearMatch()
        {
            _match = "";
            NotifyOfPropertyChange(nameof(MatchString));
            // Re-highlight every label (yellow) so the next label is typeable. This is
            // the reset after each continuous-mode click (LoadSession also starts here).
            ApplyMatch(_match);
        }

        private void ApplyMatch(string value)
        {
            var matching = Hints.Where(x => x.Label.StartsWith(value, StringComparison.OrdinalIgnoreCase)).ToList();
            var matchingSet = new HashSet<HintViewModel>(matching);

            // Only flip hints whose Active state actually changes, so we don't
            // raise PropertyChanged (and trigger WPF binding/layout work) for
            // every hint on each keystroke.
            foreach (var x in Hints)
            {
                bool shouldMatch = matchingSet.Contains(x);
                if (x.Active != shouldMatch)
                {
                    x.Active = shouldMatch;
                }
            }

            if (matching.Count == 1)
            {
                // Move the cursor onto the matched label, then apply the grid
                // pan offset so it lands where the label was shifted to.
                matching[0].Hint.MoveMouseToCenter();
                POINT p;
                User32.GetCursorPos(out p);
                User32.SetCursorPos(p.X + (int)_offsetX, p.Y + (int)_offsetY);

                // The overlay is click-through, so these real clicks reach the
                // app beneath; Move performs no click (the user clicks manually).
                switch (CurrentAction)
                {
                    case ClickAction.Left: DoLeftClick(); break;
                    case ClickAction.Right: DoRightClick(); break;
                    case ClickAction.Double: DoDoubleClick(); break;
                    case ClickAction.Move: break;
                }

                if (_isContinuous)
                {
                    // Stay up: reset for the next label (mode back to the first / Left
                    // by default, and every label re-highlighted).
                    ResetForNextClick();
                }
                else
                {
                    CloseOverlay?.Invoke();
                }
            }
        }

        /// <summary>
        /// Continuous mode: reset for the next label after a click -- click mode back to
        /// the first in the order (Left by default) and every label re-highlighted.
        /// </summary>
        private void ResetForNextClick()
        {
            _modeIndex = 0;
            NotifyOfPropertyChange(nameof(CurrentModeName));
            NotifyOfPropertyChange(nameof(CurrentModeBrush));
            ClearMatch();
        }

        /// <summary>
        /// Pans ALL labels by (dx, dy) px via the offset (the panel's
        /// TranslateTransform moves every label together).
        /// </summary>
        public void Nudge(int dx, int dy)
        {
            OffsetX += dx;
            OffsetY += dy;
        }

        /// <summary>Advances to the next click mode (Space); wraps around.</summary>
        public void CycleMode()
        {
            _modeIndex = (_modeIndex + 1) % _modeOrder.Count;
            NotifyOfPropertyChange(nameof(CurrentModeName));
            NotifyOfPropertyChange(nameof(CurrentModeBrush));
        }

        private static void DoLeftClick()
        {
            User32.mouse_event(User32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }

        private static void DoRightClick()
        {
            User32.mouse_event(User32.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
            User32.mouse_event(User32.MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
        }

        private static void DoDoubleClick()
        {
            // Two rapid left clicks register as a double-click.
            User32.mouse_event(User32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            User32.mouse_event(User32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }
    }
}
