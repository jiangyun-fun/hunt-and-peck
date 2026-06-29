using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.ObjectModel;
using System.Windows;
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
        private bool _moveOnly;

        public OverlayViewModel(
            HintSession session,
            IHintLabelService hintLabelService)
        {
            _bounds = session.OwningWindowBounds;

            var labels = hintLabelService.GetHintStrings(session.Hints.Count());
            for (int i = 0; i < labels.Count; ++i)
            {
                var hint = session.Hints[i];
                _hints.Add(new HintViewModel(hint)
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
            get
            {
                return _bounds;
            }
            set
            {
                _bounds = value;
                NotifyOfPropertyChange();
            }
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

        /// <summary>True when Space has toggled move-only: typing 2 chars positions without clicking.</summary>
        public bool IsMoveOnlyMode
        {
            get { return _moveOnly; }
            private set { _moveOnly = value; NotifyOfPropertyChange(); }
        }

        /// <summary>Legend shown on the overlay so the gestures are discoverable.</summary>
        public string ActiveLegend => OverlayActionConfig.OverlayLegend;

        public ObservableCollection<HintViewModel> Hints
        {
            get
            {
                return _hints;
            }
            set
            {
                _hints = value;
                NotifyOfPropertyChange();
            }
        }

        public Action CloseOverlay { get; set; }

        /// <summary>
        /// Default mode: fire a real left click at the current cursor position
        /// (already moved onto the matched label), then close. Implemented by the
        /// view. Not used in move-only mode.
        /// </summary>
        public Action PerformClickAndClose { get; set; }

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
                    // pan offset so it lands where the label was shifted to by the
                    // arrow keys.
                    matching[0].Hint.MoveMouseToCenter();
                    POINT p;
                    User32.GetCursorPos(out p);
                    User32.SetCursorPos(p.X + (int)_offsetX, p.Y + (int)_offsetY);

                    if (_moveOnly)
                    {
                        // Position only; the user clicks manually after close.
                        CloseOverlay?.Invoke();
                    }
                    else
                    {
                        PerformClickAndClose?.Invoke();
                    }
                }
            }
        }

        /// <summary>
        /// Pans ALL labels by (dx, dy) px via the offset (the panel's
        /// TranslateTransform moves every label together). The cursor is not
        /// moved here; it jumps to a label only when you type its characters.
        /// </summary>
        public void Nudge(int dx, int dy)
        {
            OffsetX += dx;
            OffsetY += dy;
        }

        /// <summary>Toggles move-only (Space): finalize positions without clicking.</summary>
        public void ToggleMoveOnly()
        {
            IsMoveOnlyMode = !_moveOnly;
        }
    }
}
