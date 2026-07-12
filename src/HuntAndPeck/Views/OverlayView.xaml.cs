using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using HuntAndPeck.ViewModels;

namespace HuntAndPeck.Views
{
    /// <summary>
    /// Interaction logic for OverlayView.xaml
    /// </summary>
    public partial class OverlayView
    {
        private Stopwatch _renderSw;
        private double _scaleX = 1.0;
        private double _scaleY = 1.0;

        public OverlayView()
        {
            InitializeComponent();
        }

        private void OverlayView_OnLoaded(object sender, RoutedEventArgs e)
        {
            var m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            _scaleX = m.M11;
            _scaleY = m.M22;

            // scale the items for non-96 DPIs
            layoutGrid.LayoutTransform = new ScaleTransform(1/_scaleX, 1/_scaleY);

            var vm = DataContext as OverlayViewModel;
            ApplyBounds(vm);

            // Reposition + resize when the user cycles to another monitor (Tab), so the
            // overlay always covers the current session's bounds.
            var inpc = vm as INotifyPropertyChanged;
            if (inpc != null)
            {
                inpc.PropertyChanged += (s, ev) =>
                {
                    if (string.IsNullOrEmpty(ev.PropertyName) || ev.PropertyName == nameof(OverlayViewModel.Bounds))
                    {
                        ApplyBounds(vm);
                    }
                };
            }

            // Click-through from the start so synthesized clicks (left/right/
            // double) and a manual click all reach the app beneath; keyboard
            // focus is unaffected, so typing keeps working.
            SetClickThrough(true);

            // Measure window-load to content-rendered (the label layout/render cost).
            _renderSw = Stopwatch.StartNew();
            ContentRendered += OverlayView_OnContentRendered;
        }

        /// <summary>
        /// Positions and sizes the overlay window to the view-model's current Bounds,
        /// dividing by the device scale (Bounds is in physical pixels; the window is in
        /// WPF device-independent units). Called on load and on every monitor switch.
        /// </summary>
        private void ApplyBounds(OverlayViewModel vm)
        {
            if (vm == null)
            {
                return;
            }
            Left = vm.Bounds.Left / _scaleX;
            Top = vm.Bounds.Top / _scaleY;
            Width = vm.Bounds.Width / _scaleX;
            Height = vm.Bounds.Height / _scaleY;
        }

        private void OverlayView_OnContentRendered(object sender, EventArgs e)
        {
            ContentRendered -= OverlayView_OnContentRendered;
            if (_renderSw != null)
            {
                TimingLog.Log("render " + _renderSw.ElapsedMilliseconds + "ms");
                _renderSw = null;
            }
        }

        private void OverlayView_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            var vm = DataContext as OverlayViewModel;

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Space && vm != null)
            {
                vm.CycleMode();
                e.Handled = true;   // never let Space enter the TextBox
                return;
            }

            if (e.Key == Key.Tab && vm != null)
            {
                // Cycle the overlay to the next (Tab) or previous (Shift+Tab) monitor.
                int delta = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1;
                vm.CycleMonitor(delta);
                MatchStringControl.Text = "";   // clear the typed prefix for the new monitor
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.Up:
                case Key.Down:
                case Key.Left:
                case Key.Right:
                    if (vm != null)
                    {
                        int step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift
                            ? OverlayActionConfig.ReadNudgeStepFast()
                            : OverlayActionConfig.ReadNudgeStep();
                        int dx = e.Key == Key.Left ? -step : (e.Key == Key.Right ? step : 0);
                        int dy = e.Key == Key.Up ? -step : (e.Key == Key.Down ? step : 0);
                        // Pan ALL labels together by the offset.
                        vm.Nudge(dx, dy);
                    }
                    e.Handled = true;
                    return;
            }

            // Let letters through to the TextBox; swallow everything else.
            if (!IsLabelKey(e.Key))
            {
                e.Handled = true;
            }
        }

        private static bool IsLabelKey(Key key)
        {
            // Letters (A-Z) and top-row digits (D0-D9) drive label matching.
            return (key >= Key.A && key <= Key.Z) || (key >= Key.D0 && key <= Key.D9);
        }

        /// <summary>
        /// Toggles WS_EX_TRANSPARENT on the overlay HWND. When on, the window is
        /// transparent to MOUSE hit-testing only (clicks fall through to the app
        /// beneath) while keyboard focus is unaffected, so typing keeps working.
        /// </summary>
        private void SetClickThrough(bool on)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ext = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
            if (on)
            {
                ext |= User32.WS_EX_TRANSPARENT;
            }
            else
            {
                ext &= ~User32.WS_EX_TRANSPARENT;
            }
            User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, ext);
        }
    }
}
