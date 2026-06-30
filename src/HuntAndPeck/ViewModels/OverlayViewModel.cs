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

        public OverlayViewModel(
            HintSession session,
            IHintLabelService hintLabelService)
        {
            _bounds = session.OwningWindowBounds;
            _modeOrder = OverlayActionConfig.ReadClickActionOrder();
            _modeIndex = 0; // start on the first mode (Left, by default)

            // Read the font size ONCE for the whole overlay. Re-reading the config
            // file per hint (via ReadHintFontSize) made overlay build O(N) in disk
            // reads and dominated latency at high label counts.
            var fontSize = OverlayActionConfig.ReadHintFontSize()
                ?? HuntAndPeck.Properties.Settings.Default.FontSize;

            var labels = hintLabelService.GetHintStrings(session.Hints.Count());
            for (int i = 0; i < labels.Count; ++i)
            {
                var hint = session.Hints[i];
                _hints.Add(new HintViewModel(hint, fontSize)
                {
                    Label = labels[i],
                    Active = false
                });
            }
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

        /// <summary>Legend shown on the overlay so the gestures are discoverable.</summary>
        public string ActiveLegend => OverlayActionConfig.OverlayLegend;

        public ObservableCollection<HintViewModel> Hints
        {
            get { return _hints; }
            set { _hints = value; NotifyOfPropertyChange(); }
        }

        public Action CloseOverlay { get; set; }

        public string MatchString
        {
            set
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

                    CloseOverlay?.Invoke();
                }
            }
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
