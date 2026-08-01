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
            _hideInactive = OverlayActionConfig.ReadHideNonMatchingLabels();

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
            _modeIndex = 0;

            _fontSizeRaw = OverlayActionConfig.ReadHintFontSize()
                ?? HuntAndPeck.Properties.Settings.Default.FontSize;
            _pillOpacity = OverlayActionConfig.ReadHintPillOpacity();
            _dimOpacity = OverlayActionConfig.ReadHintDimOpacity();
            _hideInactive = OverlayActionConfig.ReadHideNonMatchingLabels();

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
            var labels = _hintLabelService.GetHintStrings(session.Hints.Count);
            var fresh = new ObservableCollection<HintViewModel>();
            for (int i = 0; i < labels.Count; ++i)
            {
                var hint = session.Hints[i];
                fresh.Add(new HintViewModel(hint, EffectiveFontSize)
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
            NotifyOfPropertyChange(nameof(MatchString));
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

        /// <summary>
        /// Esc behavior: if a prefix has been typed, clear it (cancel the selection, stay
        /// up) so the user can retype from scratch; if nothing is typed, close the overlay.
        /// This matches the fuzzy-finder convention (Esc clears, Esc-on-empty exits) and
        /// forgives a mistyped char without losing the overlay. Pan/click-mode are kept.
        /// </summary>
        public void HandleEscape()
        {
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
