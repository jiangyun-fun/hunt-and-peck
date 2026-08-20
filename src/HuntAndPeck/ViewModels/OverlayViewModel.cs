using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using HuntAndPeck.Models;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using HuntAndPeck.Services.Interfaces;
using HuntAndPeck.Services.Macro;
using HuntAndPeck.Views;
using System.Security.Principal;

namespace HuntAndPeck.ViewModels
{
    internal class OverlayViewModel : NotifyPropertyChanged
    {
        private Rect _bounds;
        private ObservableCollection<HintViewModel> _hints = new ObservableCollection<HintViewModel>();
        private double _offsetX;
        private double _offsetY;
        private readonly IList<ClickAction> _modeOrder;
        private ClickAction _currentAction;

        // --- Leader dispatcher (<Space>) ---
        // <Space> opens a transient dispatcher: the next key is a leader command (set
        // mode / close / suspend / cycle-layout / toggle-dim), not a label char. The
        // binding map (key -> action) is read once per overlay from LeaderBindings.
        private bool _leaderPending;
        private Dictionary<char, LeaderBinding> _leaderBindings;
        private string _leaderMenuText;

        // --- Snapshot region (<leader>s) ---
        // A 2-pick sub-phase: type corner-1's label, then corner-2's. The rectangle between
        // them (offset-applied, like a click) is captured to the clipboard via CaptureRegion
        // (wired by App, which owns the overlay window and hides it for a clean shot).
        private enum SnapshotPhase { None, AwaitCorner1, AwaitCorner2 }
        private SnapshotPhase _snapshotPhase = SnapshotPhase.None;
        private Point _snapshotAnchor; // screen px, offset-applied

        // --- Text-span selection (<leader>v) ---
        // A 2-pick sub-phase mirroring snapshot: type the start label, then the end
        // label; the text between is selected. Pick-1 always plain-clicks (clears the
        // prior selection -- a later mousedown ON a selection starts a text drag-drop,
        // not a selection drag -- and sets the anchor). Pick-2: ShiftClick (default)
        // Shift+click extends; Drag synthesizes the whole drag in one shot (down@anchor,
        // move, up) -- no button is held while typing the second label, so a cancel
        // never leaves it stuck.
        private enum SelectPhase { None, AwaitStart, AwaitEnd }
        private SelectPhase _selectPhase = SelectPhase.None;
        private Point _selectAnchor;           // Drag method: start point (screen px, offset-applied)
        private TextSelectMethod _selectMethod;
        private bool _selectionActionsClose;   // selection actions close even in Continuous (default)

        // Multi-event input bursts (double/triple click, span-select) run on a background
        // thread with small gaps between events. Load-bearing: the UI thread owns the LL
        // hooks, so a burst run ON it is held by the OS until the thread pumps and then
        // flushed as ONE 0ms batch -- the target app randomly mishandles that (click-count
        // / shift-latch / drag races; observed as d/v/t succeeding at low frequency).
        // Off-thread, each event delivers promptly and the Thread.Sleep gaps become real
        // time between events. Mirrors the macro engine (also off-UI-thread).
        //
        // The continuation dispatcher MUST be the APPLICATION dispatcher, not
        // Dispatcher.CurrentDispatcher: quadrant overlay VMs are constructed inside
        // Task.Run (ShellViewModel.OpenQuadrantOverlayAsync), and CurrentDispatcher on
        // that worker thread creates a dispatcher nothing ever pumps -- every post-fire
        // continuation (close / continuous reset) posted to it was silently lost,
        // leaving the overlay stuck on the fired label after d/t/v (observed on-box
        // via Ctrl+Shift+F1 + <leader>d). Null-guarded only for hosts without a WPF
        // Application (the xUnit test runner): FireInputAsync is not exercised there,
        // so the fallback dispatcher is never used.
        private readonly Dispatcher _uiDispatcher = Application.Current != null
            ? Application.Current.Dispatcher
            : Dispatcher.CurrentDispatcher;
        private static readonly object SynthGate = new object();
        private const int ClickGapMs = 20;

        private readonly IHintLabelService _hintLabelService;
        private readonly string _fontSizeRaw;
        private readonly string _fontFamilyRaw;
        private readonly double _pillOpacity;
        private readonly double _dimOpacity;
        private readonly bool _hideInactive;
        private IList<HintSession> _sessions;
        private int _currentSession;
        // Layout cycling (`;`): presets + the persisted active index + a delegate that
        // rebuilds the sessions for a given preset. Null when only one/no layout is
        // configured (Automation, Grid+Window without GridLayouts), in which case `;`
        // is a no-op / passes through.
        private readonly IList<GridLayout> _layouts;
        private int _activeLayout;
        private readonly Func<int, IList<HintSession>> _rebuildSessions;
        private string _match = "";
        private bool _continuousCapable;
        private bool _isContinuous;
        private bool _dimmed;
        private bool _suspended;

        // Set when a fire's SendInput injected 0 events (UIPI: the target window is
        // more elevated than hap); surfaced by the post-fire callbacks on the UI
        // thread. Reset at the start of each fire attempt.
        private bool _inputBlocked;

        // Read once per process (elevation cannot change mid-overlay); names the fix
        // in the blocked badge so an on-box report is interpretable.
        private static readonly bool IsElevated = new WindowsPrincipal(
            WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        // --- Type-to-zoom zones (Grid + Screen, ZoneZoomEnabled) ---
        // ZonePick: show cols*rows large labels; type one to drill into its sub-rect.
        // ZoneFilled: the fine grid for one zone; Esc returns to ZonePick.
        // Normal: the feature is off (or non-Grid+Screen); all zone guards are no-ops.
        private enum ZonePhase { Normal, ZonePick, ZoneFilled }
        private ZonePhase _zonePhase = ZonePhase.Normal;
        private readonly HintSession _zonePickSession;
        private readonly string _zoneFontSizeRaw;
        private readonly Rect[] _zoneRects;
        private readonly double _zoneCellW;     // auto zone-cell size (for NudgeStep "auto")
        private readonly double _zoneCellH;
        private readonly double _zoneLensW;     // zone-filled lens size (CenteredLens)
        private readonly double _zoneLensH;
        private readonly Func<Rect, HintSession> _buildZoneSession;
        private readonly bool _zoneReturnToPickOnFire;
        private Dictionary<char, int> _zoneLabelToIndex;
        private int _currentZoneIndex = -1;

        // --- Quadrant mode (Ctrl+Shift+F1..F4) ---
        // The overlay holds one session per screen quadrant (TL/TR/BL/BR) so plain Tab
        // can cycle them via CycleMonitor (the same path monitor-cycling uses). False
        // in every other mode. _currentSession doubles as the quadrant index (0..3),
        // so the Q n/4 badge reads it directly. Set once by the builder (ShellViewModel)
        // after construction.
        private bool _isQuadrantMode;

        // --- Group view (progressive 1-char labels; GroupViewEnabled, <leader>p) ---
        // The overlay opens showing ONE dotted box per first-char label group (<=29
        // boxes) instead of every pill; typing a group char reveals only that group's
        // points, labeled by their second char alone (HintCanvas strips the typed
        // prefix). Same points/labels/coverage as the full view -- pure presentation.
        // Grid-like sessions only (all-PointHint); zone mode and Automation opt out.
        // With a valid GroupZones spec (default 4x6) the boxes are a REGULAR cols x
        // rows grid and the labels themselves are zone-based: first char = the zone's
        // key (first cols*rows HintCharacters in scan order), second char cycles the
        // char set within the zone (a 1-point zone is labeled by its key alone and
        // fires instantly). Zone assignment overflow falls back to scan-order labels
        // + derived boxes.
        private readonly bool _groupViewConfigured;
        private bool _groupViewOn;
        private bool _groupable;
        private IList<GroupHintBox> _groupBoxes;
        private readonly string _groupFontSizeRaw;
        private readonly char[] _hintChars;          // HintCharacters, read once per overlay
        private readonly int _groupZoneCols;         // 0 = no valid GroupZones spec
        private readonly int _groupZoneRows;
        private List<GroupHintBox> _zoneBoxes;       // last session's zone boxes (toggle rebuild)

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
            : this(sessions, current, hintLabelService, null, 0, null) { }

        /// <summary>
        /// Full ctor: monitor sessions + layout-cycling state. <paramref name="layouts"/>
        /// is the parsed GridLayouts presets; <paramref name="activeLayout"/> is the
        /// starting (persisted) index; <paramref name="rebuildSessions"/> regenerates the
        /// sessions for a given preset index when the user cycles with `;`. Pass null
        /// layouts/rebuild when layout cycling does not apply (Automation, or no
        /// GridLayouts configured).
        /// </summary>
        public OverlayViewModel(IList<HintSession> sessions, int current, IHintLabelService hintLabelService,
            IList<GridLayout> layouts, int activeLayout, Func<int, IList<HintSession>> rebuildSessions)
        {
            _hintLabelService = hintLabelService;
            _sessions = sessions ?? new List<HintSession>();
            _currentSession = _sessions.Count == 0
                ? 0
                : ((current % _sessions.Count) + _sessions.Count) % _sessions.Count;
            _layouts = layouts;
            _activeLayout = (layouts == null || layouts.Count == 0)
                ? 0
                : GridLayoutConfig.ClampActiveLayout(activeLayout, layouts.Count);
            _rebuildSessions = rebuildSessions;
            _modeOrder = OverlayActionConfig.ReadClickActionOrder();
            _currentAction = DefaultMode(); // start on the first mode (Left, by default)
            InitLeader();

            // Read the font size ONCE for the whole overlay. Re-reading the config
            // file per hint (via ReadHintFontSize) made overlay build O(N) in disk
            // reads and dominated latency at high label counts.
            _fontSizeRaw = OverlayActionConfig.ReadHintFontSize()
                ?? HuntAndPeck.Properties.Settings.Default.FontSize;
            // Label font family read once per overlay; passed to each HintViewModel
            // (default bundled JetBrains Mono NL when unset).
            _fontFamilyRaw = OverlayActionConfig.ReadHintFontFamily();
            // Pill fill opacity (0-1) read once per overlay; bound to HintCanvas.
            _pillOpacity = OverlayActionConfig.ReadHintPillOpacity();
            // Dimmed-label opacity (0-1) read once per overlay; used by LabelOpacity.
            _dimOpacity = OverlayActionConfig.ReadHintDimOpacity();
            _hideInactive = OverlayActionConfig.ReadHideNonMatchingLabels();
            // Group view (progressive labels), read once per overlay; the effective
            // state is per-session (_groupable) and toggleable via <leader>p.
            _groupViewConfigured = OverlayActionConfig.ReadGroupViewEnabled();
            _groupViewOn = _groupViewConfigured;
            _groupFontSizeRaw = OverlayActionConfig.ReadGroupFontSize();
            // Zone-grid labeling: parse the GroupZones spec once; 0x0 when invalid or
            // too big for the char set (every zone needs its own key char).
            _hintChars = HintLabelService.ReadHintCharacters();
            int zc, zr;
            if (GroupViewService.TryParseZoneSpec(OverlayActionConfig.ReadGroupZones(), out zc, out zr)
                && zc * zr <= _hintChars.Length)
            {
                _groupZoneCols = zc;
                _groupZoneRows = zr;
            }
            // Text-span selection gesture (ShiftClick|Drag), read once per overlay.
            _selectMethod = OverlayActionConfig.ReadTextSelectMethod();
            _selectionActionsClose = OverlayActionConfig.ReadSelectionActionsClose();

            if (_sessions.Count > 0)
            {
                LoadSession(_sessions[_currentSession]);
            }
        }

        /// <summary>
        /// Zone (type-to-zoom) ctor: Grid + Screen with ZoneZoomEnabled. The overlay
        /// opens in <see cref="ZonePhase.ZonePick"/> showing one large label per zone
        /// (cols×rows over the monitor); <see cref="SelectZone"/> drills into a zone by
        /// building a fine-grid <paramref name="buildZoneSession"/> over a lens (size
        /// <paramref name="zoneWidth"/>×<paramref name="zoneHeight"/>, default the auto cell
        /// size) centered on the zone. The in-zone grid uses a single ZoneGridStep (uniform),
        /// so layout cycling (`3`) is disabled in zone mode. Monitor cycling (Tab) is disabled
        /// (single foreground monitor). <paramref name="zonePickSession"/> is the synthetic
        /// pick session from <see cref="ZoneService.BuildPickSession"/>. Standalone (does not
        /// chain to the full ctor) so it can set _zonePhase before LoadSession and keep
        /// _rebuildSessions/_layouts null.
        /// </summary>
        public OverlayViewModel(
            HintSession zonePickSession,
            IHintLabelService hintLabelService,
            Rect monitorBounds,
            int zoneCols,
            int zoneRows,
            string zoneFontSizeRaw,
            int zoneWidth,
            int zoneHeight,
            bool zoneReturnToPickOnFire,
            Func<Rect, HintSession> buildZoneSession)
        {
            _hintLabelService = hintLabelService;
            _zonePickSession = zonePickSession;
            _sessions = new List<HintSession> { zonePickSession };
            _currentSession = 0;
            _layouts = null;       // zone mode uses a single ZoneGridStep, not GridLayouts
            _activeLayout = 0;
            _rebuildSessions = null;
            _modeOrder = OverlayActionConfig.ReadClickActionOrder();
            _currentAction = DefaultMode();
            InitLeader();

            _fontSizeRaw = OverlayActionConfig.ReadHintFontSize()
                ?? HuntAndPeck.Properties.Settings.Default.FontSize;
            _fontFamilyRaw = OverlayActionConfig.ReadHintFontFamily();
            _pillOpacity = OverlayActionConfig.ReadHintPillOpacity();
            _dimOpacity = OverlayActionConfig.ReadHintDimOpacity();
            _hideInactive = OverlayActionConfig.ReadHideNonMatchingLabels();
            // Text-span selection gesture (ShiftClick|Drag), read once per overlay.
            _selectMethod = OverlayActionConfig.ReadTextSelectMethod();
            _selectionActionsClose = OverlayActionConfig.ReadSelectionActionsClose();

            _zoneRects = ZoneService.SliceIntoZones(monitorBounds, zoneCols, zoneRows);
            _zoneCellW = _zoneRects.Length > 0 ? _zoneRects[0].Width : monitorBounds.Width;
            _zoneCellH = _zoneRects.Length > 0 ? _zoneRects[0].Height : monitorBounds.Height;
            _zoneLensW = zoneWidth > 0 ? zoneWidth : _zoneCellW;
            _zoneLensH = zoneHeight > 0 ? zoneHeight : _zoneCellH;
            _zoneFontSizeRaw = zoneFontSizeRaw;
            _zoneReturnToPickOnFire = zoneReturnToPickOnFire;
            _buildZoneSession = buildZoneSession;
            _zonePhase = ZonePhase.ZonePick;

            LoadSession(zonePickSession);
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
            // Zone-grid labeling (grid sessions with a valid GroupZones spec): first
            // char = the zone's key, second char cycles the char set within the zone.
            // Zones tile the hints' EXTENT (not the session bounds -- quadrant
            // sessions bound to the full monitor while their points cluster in one
            // quarter). On overflow (a zone denser than the char set) this returns
            // false and the session keeps the scan-order labels below.
            List<string> zoneLabels = null;
            List<GroupHintBox> zoneBoxes = null;
            if (_groupZoneCols > 0)
            {
                GroupViewService.TryAssignZoneLabels(session.Hints,
                    _groupZoneCols, _groupZoneRows, _hintChars, out zoneLabels, out zoneBoxes);
            }
            _zoneBoxes = zoneBoxes;
            IList<string> labels = zoneLabels ?? _hintLabelService.GetHintStrings(session.Hints.Count);
            var fresh = new ObservableCollection<HintViewModel>();
            for (int i = 0; i < labels.Count; ++i)
            {
                var hint = session.Hints[i];
                fresh.Add(new HintViewModel(hint, EffectiveFontSize, EffectiveFontFamily)
                {
                    Label = labels[i],
                    Active = true    // all highlighted (yellow) at init / on monitor switch
                });
            }
            Hints = fresh;
            _match = "";
            // Zone-pick: build the char -> zone-index map from the assigned labels so
            // SelectZone can route a typed zone char to its zone. Null in other phases.
            _zoneLabelToIndex = _zonePhase == ZonePhase.ZonePick
                ? ZoneService.LabelToIndexMap(labels)
                : null;
            // Group view: each session (monitor / layout / quadrant) has its own labels,
            // so groupability and boxes are recomputed per load. _groupable is the
            // session's CAPABILITY (independent of _groupViewOn, so <leader>p can turn
            // the view back on). Zone sessions never enable it (the zone ctor leaves
            // _groupViewOn false).
            _groupable = GroupViewService.IsGroupable(session.Hints, labels);
            _groupBoxes = _groupViewOn && _groupable
                ? (_zoneBoxes ?? GroupViewService.BuildGroupBoxes(fresh))
                : null;
            NotifyOfPropertyChange(nameof(MatchString));
            NotifyOfPropertyChange(nameof(MatchLength));
            NotifyOfPropertyChange(nameof(GroupView));
            NotifyOfPropertyChange(nameof(GroupBoxes));
            NotifyOfPropertyChange(nameof(ZoneGridCols));
            NotifyOfPropertyChange(nameof(ZoneGridRows));
            NotifyOfPropertyChange(nameof(Bounds));
        }

        /// <summary>
        /// Font size used for the current session's labels: the larger zone-pick size
        /// while picking (so the 9 zone labels are prominent), the normal fine-grid
        /// size otherwise. HintCanvas reads hints[0].FontSizeReadValue once per
        /// session, so swapping this string per phase makes the pick labels big
        /// without any HintCanvas change.
        /// </summary>
        private string EffectiveFontSize
            => _zonePhase == ZonePhase.ZonePick ? _zoneFontSizeRaw : _fontSizeRaw;

        /// <summary>
        /// Label font family for the current overlay (default bundled JetBrains Mono NL).
        /// Zones share the family with the fine grid (only the size differs), so this is
        /// phase-independent.
        /// </summary>
        private string EffectiveFontFamily => _fontFamilyRaw;

        /// <summary>True while the overlay shows the zone-pick labels (type a zone char to drill in).</summary>
        public bool IsZonePick => _zonePhase == ZonePhase.ZonePick;

        /// <summary>Overlay badge: the zone phase ("ZONES" while picking, "ZONE n" while filled).</summary>
        public string ZoneLabel => _zonePhase == ZonePhase.ZonePick
            ? "ZONES"
            : (_zonePhase == ZonePhase.ZoneFilled ? "ZONE " + (_currentZoneIndex + 1).ToString() : "");

        /// <summary>The zone badge shows only while a zone phase is active (Collapsed otherwise).</summary>
        public Visibility ZoneBadgeVisibility => _zonePhase != ZonePhase.Normal
            ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// True when the overlay was opened by a quadrant hotkey (Ctrl+Shift+F1..F4): the
        /// VM holds one session per screen quadrant (TL/TR/BL/BR) so plain Tab cycles them
        /// via <see cref="CycleMonitor"/>. False in every other mode. Drives the Q n/4 badge.
        /// </summary>
        public bool IsQuadrantMode
        {
            get { return _isQuadrantMode; }
            set
            {
                _isQuadrantMode = value;
                NotifyOfPropertyChange(nameof(QuadrantLabel));
                NotifyOfPropertyChange(nameof(QuadrantBadgeVisibility));
            }
        }

        /// <summary>Overlay badge: "Q n/4" in quadrant mode (mirrors LayoutLabel's "L n/N"); empty otherwise.
        /// _currentSession is the quadrant index 0..3 (TL/TR/BL/BR), so the badge reads it directly.</summary>
        public string QuadrantLabel => _isQuadrantMode && _currentSession >= 0 && _currentSession < 4
            ? string.Format("Q{0}/4", _currentSession + 1)
            : string.Empty;

        /// <summary>The quadrant badge shows only in quadrant mode (Collapsed otherwise).</summary>
        public Visibility QuadrantBadgeVisibility => _isQuadrantMode ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Zone-pick: type a zone label char to drill into that zone. Builds the fine grid
        /// over a lens (CenteredLens on the zone center) via the buildZoneSession delegate
        /// and loads it; the delegate sets OwningWindowBounds = monitor, so the overlay
        /// stays full-screen (badge screen-centered, labels not clipped). No-op outside
        /// ZonePick or for an unknown char (a mistype is swallowed).
        /// </summary>
        public void SelectZone(char c)
        {
            if (_zonePhase != ZonePhase.ZonePick || _zoneLabelToIndex == null)
            {
                return;
            }
            int idx;
            if (!_zoneLabelToIndex.TryGetValue(char.ToUpperInvariant(c), out idx))
            {
                return;
            }
            if (idx < 0 || idx >= _zoneRects.Length)
            {
                return;
            }
            EnterZoneFilled(idx);
        }

        private void EnterZoneFilled(int idx)
        {
            var zone = _zoneRects[idx];
            var lens = ZoneService.CenteredLens(
                new Point(zone.Left + zone.Width / 2.0, zone.Top + zone.Height / 2.0),
                _zoneLensW, _zoneLensH);
            var session = _buildZoneSession(lens);
            if (session == null || session.Hints == null || session.Hints.Count == 0)
            {
                return;
            }
            _currentZoneIndex = idx;
            _zonePhase = ZonePhase.ZoneFilled;
            _sessions = new List<HintSession> { session };
            _currentSession = 0;
            LoadSession(session);
            OffsetX = 0;
            OffsetY = 0;
            NotifyOfPropertyChange(nameof(ZoneLabel));
            NotifyOfPropertyChange(nameof(ZoneBadgeVisibility));
        }

        private void EnterZonePick()
        {
            _zonePhase = ZonePhase.ZonePick;
            _currentZoneIndex = -1;
            _sessions = new List<HintSession> { _zonePickSession };
            _currentSession = 0;
            LoadSession(_zonePickSession);
            OffsetX = 0;
            OffsetY = 0;
            NotifyOfPropertyChange(nameof(ZoneLabel));
            NotifyOfPropertyChange(nameof(ZoneBadgeVisibility));
        }

        /// <summary>
        /// Cycles to the next (delta = +1, Tab) or previous (delta = -1, Shift+Tab)
        /// monitor's session, wrapping around. No-op when there is only one session.
        /// Resets the pan offset; the caller clears the typed prefix.
        /// </summary>
        public void CycleMonitor(int delta)
        {
            // Zones cover the foreground monitor only; Tab monitor-cycling is disabled.
            if (_zonePhase != ZonePhase.Normal)
            {
                return;
            }
            if (_sessions == null || _sessions.Count <= 1)
            {
                return;
            }
            _currentSession = (_currentSession + delta + _sessions.Count) % _sessions.Count;
            LoadSession(_sessions[_currentSession]);
            OffsetX = 0;
            OffsetY = 0;
            // Quadrant mode: _currentSession is the new quadrant index, so refresh the
            // Q n/4 badge. Harmless in monitor mode (QuadrantLabel is empty there).
            NotifyOfPropertyChange(nameof(QuadrantLabel));
        }

        /// <summary>
        /// True when the overlay holds one session per monitor (Grid + Screen) and can
        /// follow the foreground window to another monitor via <see cref="SwitchToMonitor"/>.
        /// False for single-session modes (Automation, Grid + Window), zone mode (one
        /// monitor by design), and quadrant mode (all four sessions share one monitor's
        /// bounds, so a bounds match could not pick the right quadrant).
        /// </summary>
        public bool CanFollowForegroundMonitor
            => _zonePhase == ZonePhase.Normal && !_isQuadrantMode
               && _sessions != null && _sessions.Count > 1;

        /// <summary>
        /// Focus-follow (multi-monitor Grid + Screen): switches to the session covering
        /// <paramref name="monitorBounds"/> (physical px, e.g. Screen.FromHandle of the
        /// new foreground window) -- the same session swap <see cref="CycleMonitor"/>
        /// makes, but driven by the foreground monitor changing (Alt+Tab to another
        /// monitor) instead of Tab. Each monitor's session was built for its own bounds
        /// (portrait monitors included), so this is an instant swap with correct
        /// geometry. No-op when not multi-session, when no session matches (a monitor
        /// with no session, e.g. plugged in after open), or when already on that
        /// monitor -- in particular a same-monitor focus event never disturbs a typed
        /// prefix. Resets the pan offset like Tab.
        /// </summary>
        public void SwitchToMonitor(Rect monitorBounds)
        {
            if (!CanFollowForegroundMonitor)
            {
                return;
            }
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].OwningWindowBounds == monitorBounds)
                {
                    if (i == _currentSession)
                    {
                        return;
                    }
                    _currentSession = i;
                    // LoadSession sets Bounds (the view repositions), rebuilds the
                    // labels for this monitor's point count, and clears the prefix.
                    LoadSession(_sessions[i]);
                    OffsetX = 0;
                    OffsetY = 0;
                    // Ungated: the follow is invisible in the gated log (no enum
                    // phase), and a no-show ("labels stayed on the old monitor")
                    // must be triageable -- did we switch, or skip for no match?
                    TimingLog.LogAlways("overlay follow -> "
                        + (int)monitorBounds.X + "," + (int)monitorBounds.Y
                        + " " + (int)monitorBounds.Width + "x" + (int)monitorBounds.Height);
                    return;
                }
            }
            TimingLog.LogAlways("overlay follow skipped: no session for "
                + (int)monitorBounds.X + "," + (int)monitorBounds.Y
                + " " + (int)monitorBounds.Width + "x" + (int)monitorBounds.Height);
        }

        /// <summary>
        /// True when more than one layout preset is configured AND a rebuild delegate is
        /// wired, so the `;` key can cycle grid shapes live (Grid only). Drives both the
        /// keyboard hook (whether `;` is captured) and the layout badge.
        /// </summary>
        public bool LayoutCycleCapable
            => _layouts != null && _layouts.Count > 1 && _rebuildSessions != null;

        /// <summary>Overlay badge: the current preset, e.g. "L2/2". Empty when not cycle-capable.</summary>
        public string LayoutLabel => LayoutCycleCapable
            ? string.Format("L{0}/{1}", _activeLayout + 1, _layouts.Count)
            : string.Empty;

        /// <summary>
        /// The layout badge is Collapsed unless layout cycling is active, so no empty box
        /// shows next to the trigger-mode badge in the common single-layout / Automation
        /// case. Constant for a session (set at construction); bound directly, no converter.
        /// </summary>
        public Visibility LayoutBadgeVisibility => LayoutCycleCapable ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Cycles to the next layout preset (`;`): advances the index (wraps), persists it
        /// so the choice survives a restart, regenerates the sessions with the new geometry
        /// via the rebuild delegate, and reloads the current session. Stays on the same
        /// monitor (index clamped, not reset). Mirrors <see cref="CycleMonitor"/>'s
        /// session-swap + pan reset.
        /// </summary>
        public void CycleLayout()
        {
            if (!LayoutCycleCapable)
            {
                return;
            }

            _activeLayout = (_activeLayout + 1) % _layouts.Count;
            GridLayoutConfig.WriteActiveLayout(_activeLayout);

            var fresh = _rebuildSessions(_activeLayout);
            if (fresh == null || fresh.Count == 0)
            {
                return;
            }

            _sessions = fresh;
            // Stay on the monitor the user is viewing (clamp, do not reset to 0).
            if (_currentSession >= _sessions.Count)
            {
                _currentSession = _sessions.Count - 1;
            }
            if (_currentSession < 0)
            {
                _currentSession = 0;
            }
            LoadSession(_sessions[_currentSession]);
            OffsetX = 0;
            OffsetY = 0;
            NotifyOfPropertyChange(nameof(LayoutLabel));
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

        /// <summary>
        /// Whether non-matching labels are hidden (not just dimmed) after the first typed
        /// char. Read once per overlay; bound to HintCanvas.HideInactive.
        /// </summary>
        public bool HideInactive => _hideInactive;

        private ClickAction CurrentAction
        {
            get { return _currentAction; }
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
                    case ClickAction.Triple: return "TRIPLE CLICK";
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
                    case ClickAction.Triple: return Brushes.Orange;
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
        /// Invoked with a screen-pixel rectangle to capture to the clipboard. Wired by
        /// App.ShowOverlay, which owns the overlay window (hides it for a clean shot,
        /// CopyFromScreen, Clipboard.SetImage, restores). Null in non-app contexts (tests).
        /// </summary>
        public Action<Rect> CaptureRegion { get; set; }

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

        /// <summary>
        /// Overlay badge: the current trigger mode. While suspended (insert mode) it
        /// names the exit gesture so it stays discoverable (double-tap, since a single
        /// q/Esc is ordinary app input there).
        /// </summary>
        public string TriggerModeLabel => _suspended ? "INSERT (qq/Esc Esc)"
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
        /// Insert mode / persistent suspend: the overlay stops capturing keys AND hides
        /// its labels (opacity 0), leaving only the INSERT badge, so you can type
        /// into the app beneath (vimium, Excel) with zero key collision. Vim-style:
        /// enter with plain <c>i</c> (or <c>&lt;leader&gt;z</c>); exit with a DOUBLE
        /// press of the same key (<c>q q</c> / <c>Esc Esc</c> within 500 ms — single
        /// presses pass through to the app as normal input) or the main hotkey
        /// (Ctrl+Shift+M / Capslock+f).
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
        /// Dimmed (<c>&lt;leader&gt;x</c>): labels drop to a low opacity so the text behind
        /// is readable, but keys stay captured so you can still type a label. Toggle
        /// (again restores). Note: opacity-dim couples label contrast to the background,
        /// so it is harder to see on dark surfaces -- accepted tradeoff for the simpler
        /// look.
        /// </summary>
        public bool Dimmed
        {
            get { return _dimmed; }
            set { _dimmed = value; NotifyOfPropertyChange(nameof(LabelOpacity)); }
        }

        /// <summary>
        /// Render opacity for the label canvas: 0 (hidden) when suspended, the configured
        /// dim level when dimmed (<c>&lt;leader&gt;x</c>), full otherwise. Base mode relies
        /// on the semi-transparent pill fill (HintCanvas) for its slight see-through, not
        /// on a canvas-wide dim, so the text stays crisp.
        /// </summary>
        public double LabelOpacity => _suspended ? 0.0 : (_dimmed ? _dimOpacity : 1.0);

        /// <summary>
        /// The click-mode badge is hidden while suspended so only the SUSPENDED status
        /// shows. Exposed as Visibility so the XAML binds directly (no converter needed).
        /// </summary>
        public Visibility ClickModeBadgeVisibility => _suspended ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Toggles dim mode (<c>&lt;leader&gt;x</c>): full &lt;-&gt; dimmed labels.</summary>
        public void ToggleDimmed() { Dimmed = !Dimmed; }

        /// <summary>
        /// Enters insert mode / persistent suspend (plain <c>i</c>, <c>&lt;leader&gt;z</c>).
        /// Cancels a pending leader first; no-op while a 2-pick phase (snapshot/select)
        /// is active, so an accidental <c>i</c> mid-pick cannot suspend under the phase.
        /// Exit via <see cref="ExitSuspend"/> or the main hotkey.
        /// </summary>
        public void EnterSuspend()
        {
            if (_leaderPending)
            {
                ExitLeaderPending();
            }
            if (_snapshotPhase != SnapshotPhase.None || _selectPhase != SelectPhase.None)
            {
                return;
            }
            Suspended = true;
        }

        /// <summary>Exits insert mode / suspend, back to the live overlay (q/Esc, main hotkey).</summary>
        public void ExitSuspend()
        {
            if (_suspended)
            {
                Suspended = false;
            }
        }

        /// <summary>
        /// The typed label prefix, for display (bound one-way to the TextBox). Input
        /// arrives via the global keyboard hook (OverlayKeyboardHook.AppendLabelChar),
        /// not via a focused TextBox, so the overlay can stay non-activated and not
        /// dismiss an open context menu.
        /// </summary>
        public string MatchString => _match;

        /// <summary>
        /// Length of the typed label prefix. In group view, 0 = level 1 (group boxes
        /// only) and greater = level 2 (only the matching group's pills, drawn with the
        /// prefix stripped). Bound to HintCanvas.GroupMatchLength.
        /// </summary>
        public int MatchLength => _match.Length;

        /// <summary>
        /// Group view active for the current session: one dotted box per first-char
        /// label group at level 1, prefix-stripped labels at level 2. False for zone
        /// sessions, Automation / taskbar-merged sessions, and after <leader>p toggles
        /// it off. Bound to HintCanvas.GroupView.
        /// </summary>
        public bool GroupView => _groupViewOn && _groupable;

        /// <summary>
        /// The current session's group boxes (null/empty when the group view is off).
        /// Bound to HintCanvas.GroupBoxesSource.
        /// </summary>
        public IList<GroupHintBox> GroupBoxes => _groupBoxes;

        /// <summary>
        /// Group key-char font size as a raw string (GroupFontSize config; default 14,
        /// 0 = follow HintFontSize). Bound to HintCanvas.GroupFontSizeText.
        /// </summary>
        public string GroupFontSize => _groupFontSizeRaw ?? _fontSizeRaw;

        /// <summary>
        /// Zone-grid columns of the current group boxes; 0 when the boxes are not a
        /// regular zone grid (the v1 derived fallback, or no boxes at all). Bound to
        /// HintCanvas.ZoneGridCols, which draws single shared borders for a zone grid.
        /// </summary>
        public int ZoneGridCols => (_groupBoxes != null && _zoneBoxes != null) ? _groupZoneCols : 0;

        /// <summary>
        /// Horizontal glyph stretch for the labels (HintFontWidth percent / 100, read
        /// once per overlay; default 1.15). Widens pills + glyphs so small-size
        /// confusables (w/m in the bundled mono at 8 px) separate. Bound to
        /// HintCanvas.LabelWidthScale.
        /// </summary>
        private readonly double _labelWidthScale = OverlayActionConfig.ReadHintFontWidthScale();

        public double LabelWidthScale => _labelWidthScale;

        /// <summary>Zone-grid rows; see <see cref="ZoneGridCols"/>.</summary>
        public int ZoneGridRows => (_groupBoxes != null && _zoneBoxes != null) ? _groupZoneRows : 0;

        /// <summary>
        /// &lt;leader&gt;p: toggle the group view (dotted first-char group boxes vs. the
        /// full-label view) for this overlay session. No-op when the session is not
        /// group-capable (zone mode, Automation / taskbar-merged). Clears any typed
        /// prefix so the switch starts from a clean slate (level 1 boxes, or all labels).
        /// </summary>
        public void ToggleGroupView()
        {
            // Zone sessions have their own drill UX (zone pick -> filled); do not nest
            // group view inside it.
            if (_zonePhase != ZonePhase.Normal || !_groupable)
            {
                return;
            }
            _groupViewOn = !_groupViewOn;
            ClearMatch();
            // Restore the session's zone boxes when zone labeling is active; the
            // derived boxes are only the fallback shape.
            _groupBoxes = _groupViewOn ? (_zoneBoxes ?? GroupViewService.BuildGroupBoxes(Hints)) : null;
            NotifyOfPropertyChange(nameof(GroupView));
            NotifyOfPropertyChange(nameof(GroupBoxes));
            NotifyOfPropertyChange(nameof(ZoneGridCols));
            NotifyOfPropertyChange(nameof(ZoneGridRows));
        }

        /// <summary>Appends one typed label character and runs the prefix match.</summary>
        public void AppendLabelChar(char c)
        {
            _match += char.ToUpperInvariant(c);
            NotifyOfPropertyChange(nameof(MatchString));
            NotifyOfPropertyChange(nameof(MatchLength));
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
            NotifyOfPropertyChange(nameof(MatchLength));
            // Re-highlight every label (yellow) so the next label is typeable. This is
            // the reset after each continuous-mode click (LoadSession also starts here).
            ApplyMatch(_match);
        }

        /// <summary>
        /// Esc behavior: if a prefix has been typed, clear it (cancel the selection, stay
        /// up) so the user can retype from scratch; if nothing is typed, close the overlay.
        /// This matches the fuzzy-finder convention (Esc clears, Esc-on-empty exits) and
        /// forgives a mistyped char without losing the overlay. Pan/click-mode are kept.
        /// </summary>
        public void HandleEscape()
        {
            // While the leader dispatcher is open, Esc (and 1, its alias) cancel it
            // instead of clearing a prefix or closing the overlay.
            if (_leaderPending)
            {
                ExitLeaderPending();
                return;
            }
            if (_snapshotPhase != SnapshotPhase.None)
            {
                // Esc/1 while picking snapshot corners cancels the snapshot.
                ExitSnapshotPhase();
                return;
            }
            if (_selectPhase != SelectPhase.None)
            {
                // Esc/1 while picking selection endpoints cancels it (nothing to release:
                // neither method holds a button between picks).
                ExitSelectText();
                return;
            }
            if (!string.IsNullOrEmpty(_match))
            {
                ClearMatch();
                return;
            }
            // Zone-filled: Esc goes back to the zone-pick view (9 labels). Zone-pick
            // always has an empty match (label chars route to SelectZone), so a second
            // Esc falls through to close the overlay.
            if (_zonePhase == ZonePhase.ZoneFilled)
            {
                EnterZonePick();
                return;
            }
            CloseOverlay?.Invoke();
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
                // Snapshot 2-pick: the matched label is a region corner, not a click.
                if (_snapshotPhase != SnapshotPhase.None)
                {
                    HandleSnapshotCorner(matching[0].Hint);
                    return;
                }
                // Text-span selection 2-pick: the matched label is a selection endpoint,
                // not a click. HandleSelectPoint does its own cursor move + offset.
                if (_selectPhase != SelectPhase.None)
                {
                    HandleSelectPoint(matching[0].Hint);
                    return;
                }
                // Move the cursor onto the matched label, then apply the grid
                // pan offset so it lands where the label was shifted to.
                matching[0].Hint.MoveMouseToCenter();
                POINT p;
                User32.GetCursorPos(out p);
                User32.SetCursorPos(p.X + (int)_offsetX, p.Y + (int)_offsetY);
                _inputBlocked = false;   // fresh attempt: clear the UIPI-blocked badge

                // Post-fire: stay up (continuous) or close. Selection actions (Double/
                // Triple) close EVEN in Continuous mode by default (SelectionActionsClose):
                // staying up cleared the just-made selection in the target app (observed
                // Notepad3/Edge). Left/Right/Move stay continuous (repeated clicking/nav).
                Action postFire = () =>
                {
                    SurfaceInputBlocked();
                    bool selAction = CurrentAction == ClickAction.Double
                                  || CurrentAction == ClickAction.Triple;
                    if (_isContinuous && !(selAction && _selectionActionsClose))
                    {
                        // Stay up: reset for the next label (mode back to the first / Left
                        // by default, and every label re-highlighted).
                        ResetForNextClick();
                        // Optional (ZoneZoomReturnToPickOnFire): re-zoom to the 9-label pick
                        // view after each fire. Default stays in the zone for nearby repeats.
                        if (_zonePhase == ZonePhase.ZoneFilled && _zoneReturnToPickOnFire)
                        {
                            EnterZonePick();
                        }
                    }
                    else
                    {
                        CloseOverlay?.Invoke();
                    }
                };

                // The overlay is click-through, so these real clicks reach the
                // app beneath; Move performs no click (the user clicks manually).
                // Single clicks are reliable on-thread; multi-event bursts (Double/
                // Triple) go off-thread so their events deliver as properly separated
                // clicks rather than one batched 0ms burst (see FireInputAsync).
                switch (CurrentAction)
                {
                    case ClickAction.Left: DoLeftClick(); postFire(); break;
                    case ClickAction.Right: DoRightClick(); postFire(); break;
                    case ClickAction.Double: FireInputAsync(DoDoubleClick, postFire); break;
                    case ClickAction.Triple: FireInputAsync(DoTripleClick, postFire); break;
                    case ClickAction.Move: postFire(); break;
                }
            }
        }

        /// <summary>
        /// Continuous mode: reset for the next label after a click -- click mode back to
        /// the first in the order (Left by default) and every label re-highlighted.
        /// </summary>
        private void ResetForNextClick()
        {
            _currentAction = DefaultMode(); // continuous mode: back to the default (Left)
            NotifyOfPropertyChange(nameof(CurrentModeName));
            NotifyOfPropertyChange(nameof(CurrentModeBrush));
            ClearMatch();
        }

        /// <summary>
        /// Runs an input burst on a background thread, then continues on the UI thread.
        /// Off-thread is load-bearing: the UI thread owns the LL hooks, so a burst run on
        /// it is HELD by the OS until the thread pumps and then flushed as one 0ms batch --
        /// the app sees identical-timestamp events and randomly mishandles them (observed
        /// as d/v/t succeeding only at low frequency). Off-thread each event delivers
        /// promptly and the Thread.Sleep gaps inside the burst become real time between
        /// events. The gate serializes bursts. Mirrors the macro engine (also off-thread).
        /// </summary>
        private void FireInputAsync(Action synth, Action after)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    lock (SynthGate)
                    {
                        synth();
                    }
                }
                catch (Exception ex)
                {
                    // Never let a burst failure strand the overlay: log it, and still
                    // run the continuation (close / reset) below.
                    TimingLog.Log("input burst failed: " + ex.Message);
                }
                finally
                {
                    _uiDispatcher.BeginInvoke(after);
                }
            });
        }

        /// <summary>
        /// Pans ALL labels by (dx, dy) px via the offset (the panel's
        /// TranslateTransform moves every label together).
        /// </summary>
        public void Nudge(NudgeTier tier, int dx, int dy)
        {
            var ns = NudgeStepFor(tier);
            int sx, sy;
            if (ns.IsAuto)
            {
                // "auto" = one zone cell (zone mode: cellW for h/l, cellH for j/k); a tier
                // default otherwise (non-zone mode has no zone cell).
                sx = _zoneCellW > 0 ? (int)Math.Round(_zoneCellW) : AutoFallback(tier);
                sy = _zoneCellH > 0 ? (int)Math.Round(_zoneCellH) : AutoFallback(tier);
            }
            else
            {
                sx = ns.X;
                sy = ns.Y;
            }
            // dx/dy are unit directions (-1/0/1); the 0-axis contributes nothing.
            OffsetX += dx * sx;
            OffsetY += dy * sy;
        }

        private static NudgeStep NudgeStepFor(NudgeTier tier)
        {
            switch (tier)
            {
                case NudgeTier.Small: return OverlayActionConfig.ReadNudgeStepSmall();
                case NudgeTier.Large: return OverlayActionConfig.ReadNudgeStepLarge();
                default: return OverlayActionConfig.ReadNudgeStepMedium();
            }
        }

        private static int AutoFallback(NudgeTier tier)
        {
            switch (tier)
            {
                case NudgeTier.Small: return 3;
                case NudgeTier.Large: return 300;
                default: return 15;
            }
        }

        // -------- Leader dispatcher (<Space>) --------

        /// <summary>True while the leader dispatcher popup is open.</summary>
        public bool IsLeaderPending => _leaderPending;

        /// <summary>Popup visibility (Collapsed unless a leader is pending).</summary>
        public Visibility LeaderMenuVisibility => _leaderPending ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>The popup text (one "key  action" line per binding, display order).</summary>
        public string LeaderMenuText => _leaderMenuText;

        /// <summary>
        /// Opens the leader dispatcher (<Space>). Guards: ignored while suspended (keys
        /// pass through then anyway) or already pending -- a second <Space> cancels
        /// (toggle), so the menu can never get stuck open. The typed label prefix is
        /// PRESERVED across the leader: dispatch branches on <see cref="_leaderPending"/>
        /// (the hook checks it before label chars), not on the prefix, so a stale
        /// prefix never intercepts leader keys -- and keeping it means a mid-drill
        /// mode switch (<Space>r while inside zone K) returns you to K's level-2
        /// labels instead of resetting to the boxes. The 2-pick phases (snapshot /
        /// select) clear the prefix on entry themselves.
        /// </summary>
        public void EnterLeader()
        {
            if (_suspended)
            {
                return;
            }
            if (_leaderPending)
            {
                ExitLeaderPending();
                return;
            }
            _leaderPending = true;
            NotifyOfPropertyChange(nameof(LeaderMenuVisibility));
        }

        /// <summary>
        /// Routes the key pressed while the leader is pending. A mapped key fires its
        /// action (set mode / close / suspend / cycle-layout / toggle-dim); an unmapped
        /// key just cancels the dispatcher and returns to label typing. Either way the
        /// dispatcher closes after one key.
        /// </summary>
        public void LeaderCommand(char c)
        {
            if (!_leaderPending)
            {
                return;
            }
            char key = char.ToUpperInvariant(c);
            LeaderBinding b;
            if (_leaderBindings.TryGetValue(key, out b))
            {
                ExitLeaderPending();
                switch (b.Kind)
                {
                    case LeaderKind.Mode: SetMode(b.Mode); break;
                    case LeaderKind.Close: CloseOverlay?.Invoke(); break;
                    case LeaderKind.Suspend: EnterSuspend(); break;
                    case LeaderKind.CycleLayout: CycleLayout(); break;
                    case LeaderKind.ToggleDim: ToggleDimmed(); break;
                    case LeaderKind.Snapshot: EnterSnapshotRegion(); break;
                    case LeaderKind.SelectText: EnterSelectText(); break;
                    case LeaderKind.ToggleGroupView: ToggleGroupView(); break;
                    case LeaderKind.ToggleQuadrantGuide: QuadrantGuideWindow.Toggle(); break;
                }
            }
            else
            {
                // Unmapped key: cancel the dispatcher, fire nothing.
                ExitLeaderPending();
            }
        }

        private void ExitLeaderPending()
        {
            _leaderPending = false;
            NotifyOfPropertyChange(nameof(LeaderMenuVisibility));
        }

        /// <summary>Sets the click mode directly (replaces the removed Space cycle).</summary>
        public void SetMode(ClickAction mode)
        {
            _currentAction = mode;
            NotifyOfPropertyChange(nameof(CurrentModeName));
            NotifyOfPropertyChange(nameof(CurrentModeBrush));
        }

        /// <summary>The default click mode: the first entry of ClickModeOrder (Left).</summary>
        private ClickAction DefaultMode()
        {
            return _modeOrder != null && _modeOrder.Count > 0 ? _modeOrder[0] : ClickAction.Left;
        }

        /// <summary>Reads LeaderBindings once and builds the lookup map + popup text.</summary>
        private void InitLeader()
        {
            var ordered = LeaderBindingConfig.ReadLeaderBindings();
            var dict = new Dictionary<char, LeaderBinding>();
            var unique = new List<LeaderBinding>();
            foreach (var b in ordered)
            {
                dict[b.Key] = b;
                if (!unique.Any(x => x.Key == b.Key))
                {
                    unique.Add(b);
                }
            }
            _leaderBindings = dict;
            _leaderMenuText = BuildLeaderMenuText(unique);
        }

        private static string BuildLeaderMenuText(IList<LeaderBinding> bindings)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var b in bindings)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.Append(char.ToLowerInvariant(b.Key));
                sb.Append("  ");
                sb.Append(b.DisplayLabel());
            }
            return sb.ToString();
        }

        // -------- Snapshot region (<leader>s) --------

        /// <summary>SNAP badge visibility (Collapsed unless a snapshot pick is in progress).</summary>
        public Visibility SnapshotBadgeVisibility
            => _snapshotPhase != SnapshotPhase.None ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>SNAP badge text: "SNAP 1/2" while awaiting the first corner, "SNAP 2/2" after.</summary>
        public string SnapshotBadgeLabel
            => _snapshotPhase == SnapshotPhase.AwaitCorner1 ? "SNAP 1/2"
             : _snapshotPhase == SnapshotPhase.AwaitCorner2 ? "SNAP 2/2"
             : "";

        /// <summary>SEL badge visibility (Collapsed unless a text-span pick is in progress).</summary>
        public Visibility SelectBadgeVisibility
            => _selectPhase != SelectPhase.None ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>SEL badge text: "SEL 1/2" while awaiting the start, "SEL 2/2" after.</summary>
        public string SelectBadgeLabel
            => _selectPhase == SelectPhase.AwaitStart ? "SEL 1/2"
             : _selectPhase == SelectPhase.AwaitEnd ? "SEL 2/2"
             : "";

        /// <summary>
        /// Blocked-badge visibility: shown after a fire whose SendInput injected 0
        /// events (UIPI -- the target window is more elevated than hap, e.g. an
        /// elevated v2rayN while hap runs unelevated). Collapsed otherwise.
        /// </summary>
        public Visibility BlockedBadgeVisibility
            => _inputBlocked ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Blocked-badge text; names the fix when hap itself is unelevated.</summary>
        public string BlockedBadgeLabel
            => _inputBlocked ? (IsElevated ? "INPUT BLOCKED" : "INPUT BLOCKED - RUN hap AS ADMIN") : "";

        /// <summary>
        /// Enters snapshot-region mode (<leader>s). Guards: ignored while suspended or if a
        /// snapshot is already in progress. Clears any partial prefix so the next keys are
        /// read as corner labels.
        /// </summary>
        public void EnterSnapshotRegion()
        {
            if (_suspended || _snapshotPhase != SnapshotPhase.None)
            {
                return;
            }
            _snapshotPhase = SnapshotPhase.AwaitCorner1;
            if (!string.IsNullOrEmpty(_match))
            {
                ClearMatch();
            }
            NotifyOfPropertyChange(nameof(SnapshotBadgeVisibility));
            NotifyOfPropertyChange(nameof(SnapshotBadgeLabel));
        }

        /// <summary>
        /// Handles a matched label as a snapshot corner. Corner-1 is stored as the anchor
        /// (labels re-highlight so corner-2 is pickable); corner-2 forms the rectangle,
        /// fires CaptureRegion, then closes (one-shot by default; Continuous +
        /// SelectionActionsClose=false stays up).
        /// </summary>
        private void HandleSnapshotCorner(Hint hint)
        {
            Point p = hint.TargetScreenPoint();
            p.X += _offsetX;
            p.Y += _offsetY;

            if (_snapshotPhase == SnapshotPhase.AwaitCorner1)
            {
                _snapshotAnchor = p;
                _snapshotPhase = SnapshotPhase.AwaitCorner2;
                NotifyOfPropertyChange(nameof(SnapshotBadgeLabel));
                ClearMatch(); // re-highlight all labels so corner-2 is pickable
                return;
            }

            // AwaitCorner2: form the rectangle and capture.
            Rect rect = NormalizeRegion(_snapshotAnchor, p);
            ExitSnapshotPhase();
            if (rect.Width >= 1 && rect.Height >= 1)
            {
                CaptureRegion?.Invoke(rect);
            }

            // Snapshot closes unless the user opted to keep the overlay up in
            // Continuous mode (SelectionActionsClose=false) -- the same policy as
            // span-select and Double/Triple (d/t/v all behave one-shot by default).
            if (_isContinuous && !_selectionActionsClose)
            {
                ResetForNextClick();
            }
            else
            {
                CloseOverlay?.Invoke();
            }
        }

        private void ExitSnapshotPhase()
        {
            _snapshotPhase = SnapshotPhase.None;
            NotifyOfPropertyChange(nameof(SnapshotBadgeVisibility));
            NotifyOfPropertyChange(nameof(SnapshotBadgeLabel));
        }

        // -------- Text-span selection (<leader>v) --------

        /// <summary>
        /// Enters text-span selection (<leader>v): a 2-pick sub-phase mirroring snapshot.
        /// Guards: ignored while suspended or if a pick is already in progress.
        /// </summary>
        public void EnterSelectText()
        {
            if (_suspended || _selectPhase != SelectPhase.None)
            {
                return;
            }
            _selectPhase = SelectPhase.AwaitStart;
            if (!string.IsNullOrEmpty(_match))
            {
                ClearMatch();
            }
            NotifyOfPropertyChange(nameof(SelectBadgeVisibility));
            NotifyOfPropertyChange(nameof(SelectBadgeLabel));
        }

        /// <summary>
        /// Handles a matched label as a selection endpoint. Start: move + plain click
        /// (clears any prior selection; anchors). End: ShiftClick Shift+click extends,
        /// or Drag synthesizes the whole drag (down@anchor, move, up) in one shot. Then
        /// follows trigger mode.
        /// </summary>
        private void HandleSelectPoint(Hint hint)
        {
            // Resolve this pick's screen target: label center + pan offset (same offset a
            // click / snapshot corner applies), so the endpoint lands where the label was.
            hint.MoveMouseToCenter();
            POINT p;
            User32.GetCursorPos(out p);
            int x = p.X + (int)_offsetX;
            int y = p.Y + (int)_offsetY;
            _inputBlocked = false;   // fresh attempt: clear the UIPI-blocked badge

            if (_selectPhase == SelectPhase.AwaitStart)
            {
                // BOTH methods plain-click at the start. The click CLEARS any existing
                // selection: without it, the Drag method's later mousedown lands ON the
                // previous selection and the app starts a text drag-and-drop (moving the
                // text) instead of a new selection drag -- observed as selection working,
                // then cancelling, alternating between attempts. It also sets the
                // anchor/caret for the extend.
                User32.SetCursorPos(x, y);
                DoLeftClick();
                if (_inputBlocked)
                {
                    SurfaceInputBlocked();   // pick-1 runs on-thread; surface now
                }
                _selectAnchor = new Point(x, y);
                _selectPhase = SelectPhase.AwaitEnd;
                NotifyOfPropertyChange(nameof(SelectBadgeLabel));
                ClearMatch();                           // re-highlight so the end label is pickable
                return;
            }

            // AwaitEnd: finish the selection off-thread (real gaps between down/move/up
            // are what make the app register the gesture; a 0ms batch was flaky).
            Point anchor = _selectAnchor;
            Action afterSelect = () =>
            {
                SurfaceInputBlocked();
                ExitSelectText();
                // Span-select closes unless the user opted to keep the overlay up in
                // Continuous mode (SelectionActionsClose=false).
                if (_isContinuous && !_selectionActionsClose)
                {
                    ResetForNextClick();
                }
                else
                {
                    CloseOverlay?.Invoke();
                }
            };
            if (_selectMethod == TextSelectMethod.Drag)
            {
                // Whole drag in one shot (no button held during typing): down at the
                // anchor, gap, jump to the end (the move fires WM_MOUSEMOVE -> extends
                // the selection), gap, up. Nothing to release on cancel; nothing is held.
                FireInputAsync(() =>
                {
                    User32.SetCursorPos((int)anchor.X, (int)anchor.Y);
                    Thread.Sleep(ClickGapMs);
                    DoLeftDown();
                    Thread.Sleep(ClickGapMs);
                    User32.SetCursorPos(x, y);
                    Thread.Sleep(ClickGapMs);
                    DoLeftUp();
                }, afterSelect);
            }
            else
            {
                FireInputAsync(() =>
                {
                    User32.SetCursorPos(x, y);
                    Thread.Sleep(ClickGapMs);
                    DoShiftClick();                     // extend selection from the anchor to here
                }, afterSelect);
            }
        }

        /// <summary>Cancels text-span selection. Nothing to release: neither method holds
        /// a button between picks (Drag synthesizes the whole drag at pick-2).</summary>
        private void ExitSelectText()
        {
            _selectPhase = SelectPhase.None;
            NotifyOfPropertyChange(nameof(SelectBadgeVisibility));
            NotifyOfPropertyChange(nameof(SelectBadgeLabel));
        }

        /// <summary>
        /// Normalizes two screen points into a non-negative rectangle (min corner, abs size),
        /// so corner-entry order does not matter. Internal for unit testing.
        /// </summary>
        internal static Rect NormalizeRegion(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            double w = Math.Abs(b.X - a.X);
            double h = Math.Abs(b.Y - a.Y);
            return new Rect(x, y, w, h);
        }

        private void DoLeftClick()
        {
            SendMouseEvent(User32.MOUSEEVENTF_LEFTDOWN);
            SendMouseEvent(User32.MOUSEEVENTF_LEFTUP);
        }

        private void DoRightClick()
        {
            SendMouseEvent(User32.MOUSEEVENTF_RIGHTDOWN);
            SendMouseEvent(User32.MOUSEEVENTF_RIGHTUP);
        }

        private void DoDoubleClick()
        {
            // Two left clicks with a REAL gap between them (runs off-thread via
            // FireInputAsync). The zero-gap form was randomly coalesced/mishandled by the
            // target app (the burst flushed as one 0ms batch); the gap is what makes the
            // double-click register reliably.
            DoLeftClick();
            Thread.Sleep(ClickGapMs);
            DoLeftClick();
        }

        private void DoTripleClick()
        {
            // Three left clicks with real gaps (selects a whole line in most apps; a
            // sentence in Word). Runs off-thread; see DoDoubleClick for why the gaps.
            DoLeftClick();
            Thread.Sleep(ClickGapMs);
            DoLeftClick();
            Thread.Sleep(ClickGapMs);
            DoLeftClick();
        }

        // -------- Text-span selection primitives --------
        // DoLeftDown/DoLeftUp split a click so the Drag method can synthesize a drag
        // (down@anchor, move, up) in one shot at pick-2. DoShiftClick is the ShiftClick
        // method's pick-2: extend the selection from the anchor to the cursor.

        private void DoLeftDown()
        {
            SendMouseEvent(User32.MOUSEEVENTF_LEFTDOWN);
        }

        private void DoLeftUp()
        {
            SendMouseEvent(User32.MOUSEEVENTF_LEFTUP);
        }

        /// <summary>
        /// Sends one mouse button event via SendInput and records UIPI blocking: the
        /// call injects 0 events when the foreground window's integrity level is higher
        /// than hap's (an elevated app -- v2rayN etc. -- while hap runs unelevated).
        /// mouse_event could not report this (void return). The flag is surfaced on the
        /// UI thread by the post-fire callbacks (badge + timing log); it resets at the
        /// start of each fire attempt.
        /// </summary>
        private void SendMouseEvent(uint flags)
        {
            if (InputSynthesis.SendMouseEvent(flags) == 0)
            {
                _inputBlocked = true;
            }
        }

        /// <summary>
        /// Surfaces a UIPI-blocked fire (SendInput injected 0 events -- the target
        /// window is more elevated than hap): a red badge plus a timing-log line, so
        /// an on-box report is interpretable. Called from the post-fire callbacks on
        /// the UI thread; the badge clears at the start of the next fire attempt.
        /// </summary>
        private void SurfaceInputBlocked()
        {
            NotifyOfPropertyChange(nameof(BlockedBadgeVisibility));
            NotifyOfPropertyChange(nameof(BlockedBadgeLabel));
            if (_inputBlocked)
            {
                TimingLog.Log("SendInput blocked (0 events injected); hap elevated=" + IsElevated
                    + " -- elevated target apps need hap started as administrator");
            }
        }

        private static void DoShiftClick()
        {
            // Shift+click extends a selection from the caret to the cursor. ShiftDown,
            // settle, click, settle, ShiftUp -- sent as separate SendInputs with gaps
            // (runs off-thread) so the app has time to latch the synthetic Shift before
            // and across the click; the atomic one-SendInput form randomly registered as
            // an unshifted click. VK_SHIFT is not a captured key in our LL keyboard hook
            // (Classify -> None -> pass-through), so the Shift reaches the app's key state.
            var shiftDown = new[] { KeyInput(User32.VK_SHIFT, false) };
            var click = new[]
            {
                MouseInput(User32.MOUSEEVENTF_LEFTDOWN),
                MouseInput(User32.MOUSEEVENTF_LEFTUP)
            };
            var shiftUp = new[] { KeyInput(User32.VK_SHIFT, true) };
            int cb = Marshal.SizeOf(typeof(User32.INPUT));
            User32.SendInput((uint)shiftDown.Length, shiftDown, cb);
            Thread.Sleep(ClickGapMs);
            User32.SendInput((uint)click.Length, click, cb);
            Thread.Sleep(ClickGapMs);
            User32.SendInput((uint)shiftUp.Length, shiftUp, cb);
        }

        private static User32.INPUT KeyInput(int vk, bool up)
        {
            return new User32.INPUT
            {
                type = User32.INPUT_KEYBOARD,
                u = new User32.INPUTUNION
                {
                    ki = new User32.KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = 0,
                        dwFlags = up ? User32.KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private static User32.INPUT MouseInput(uint flags)
        {
            return new User32.INPUT
            {
                type = User32.INPUT_MOUSE,
                u = new User32.INPUTUNION
                {
                    mi = new User32.MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = 0,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }
    }
}
