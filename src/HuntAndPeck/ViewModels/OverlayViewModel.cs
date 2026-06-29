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
        private bool _isMoveOnlyMode;
        private int _nudgeX;
        private int _nudgeY;

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

        /// <summary>True once Space has been pressed: continuous move-only positioning.</summary>
        public bool IsMoveOnlyMode
        {
            get { return _isMoveOnlyMode; }
            private set { _isMoveOnlyMode = value; NotifyOfPropertyChange(); }
        }

        /// <summary>Indicator text shown by the view while in move-only mode.</summary>
        public string MoveOnlyHint => OverlayActionConfig.MoveOnlyHint;

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
        /// Default mode: move the cursor onto the matched target, synthesize a real
        /// left click there, then close. Implemented by the view (click-through +
        /// mouse_event). Null in Invoke mode.
        /// </summary>
        public Action<Point> PerformClickAndClose { get; set; }

        /// <summary>
        /// Move-only mode: clear the TextBox so the next label can be typed fresh.
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
                    var target = matching[0].Hint;
                    target.MoveMouseToCenter();

                    // Capture the real landed cursor position so nudging and the
                    // click use it (consistent for PointHint and UIA hints).
                    POINT p;
                    User32.GetCursorPos(out p);
                    _nudgeX = p.X;
                    _nudgeY = p.Y;

                    if (_isMoveOnlyMode)
                    {
                        // Jump only; clear input for the next label; never click.
                        ResetInput?.Invoke();
                    }
                    else if (OverlayActionConfig.ReadClickMode() == ClickMode.Invoke)
                    {
                        target.Invoke();
                        CloseOverlay?.Invoke();
                    }
                    else
                    {
                        // RealClick (default): synthesize a real left click at the target.
                        PerformClickAndClose?.Invoke(new Point(_nudgeX, _nudgeY));
                    }
                }
            }
        }

        /// <summary>
        /// Enters move-only mode (Space): the overlay stays open, becomes
        /// click-through, and never auto-clicks. Captures the current cursor
        /// position so arrows can nudge from there.
        /// </summary>
        public void EnterMoveOnlyMode()
        {
            if (_isMoveOnlyMode)
            {
                return;
            }
            IsMoveOnlyMode = true;
            POINT p;
            User32.GetCursorPos(out p);
            _nudgeX = p.X;
            _nudgeY = p.Y;
        }

        /// <summary>
        /// Nudges the cursor by (dx, dy) in physical pixels, then clears the input
        /// so the next label starts fresh. Move-only mode only.
        /// </summary>
        public void Nudge(int dx, int dy)
        {
            _nudgeX += dx;
            _nudgeY += dy;
            User32.SetCursorPos(_nudgeX, _nudgeY);
            ResetInput?.Invoke();
        }
    }
}
