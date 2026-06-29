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
        private HintViewModel _activeHint;
        private int _cursorX;
        private int _cursorY;

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
        /// Clear the TextBox so the next label can be typed fresh after a jump.
        /// Implemented by the view.
        /// </summary>
        public Action ResetInput { get; set; }

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
                    // Jump the cursor onto the label WITHOUT clicking. This label
                    // becomes the active one that arrows will slide. The overlay
                    // stays open; the user clicks manually when lined up.
                    matching[0].Hint.MoveMouseToCenter();
                    POINT p;
                    User32.GetCursorPos(out p);
                    _cursorX = p.X;
                    _cursorY = p.Y;
                    _activeHint = matching[0];
                    ResetInput?.Invoke();
                }
            }
        }

        /// <summary>
        /// Slides the active label and the cursor together by (dx, dy) px. Both
        /// move the same delta so the label marker stays under the cursor. No-op
        /// until a label has been jumped to.
        /// </summary>
        public void Nudge(int dx, int dy)
        {
            if (_activeHint == null)
            {
                return;
            }
            _cursorX += dx;
            _cursorY += dy;
            User32.SetCursorPos(_cursorX, _cursorY);
            _activeHint.CanvasLeft += dx;
            _activeHint.CanvasTop += dy;
        }
    }
}
