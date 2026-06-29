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
        public OverlayView()
        {
            InitializeComponent();
        }

        private void OverlayView_OnLoaded(object sender, RoutedEventArgs e)
        {
            var m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            var scaleX = m.M11;
            var scaleY = m.M22;

            // scale the items for non-96 DPIs
            layoutGrid.LayoutTransform = new ScaleTransform(1/scaleX, 1/scaleY);

            // resize the window for non-96 DPIs
            var vm = DataContext as OverlayViewModel;
            Left = vm.Bounds.Left / scaleX;
            Top = vm.Bounds.Top / scaleY;
            Width = vm.Bounds.Width / scaleX;
            Height = vm.Bounds.Height / scaleY;

            if (vm != null)
            {
                // Clear the TextBox between jumps so each label is typed fresh.
                vm.ResetInput = () => MatchStringControl.Clear();
            }

            // Click-through from the start: the user clicks manually, and that
            // click must reach the app beneath (which also deactivates this
            // overlay so it closes via ForegroundWindow.OnDeactivated).
            SetClickThrough(true);
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
                        // Slides the active label + cursor together; no-op until a
                        // label has been jumped to.
                        vm.Nudge(dx, dy);
                    }
                    e.Handled = true;
                    return;
            }

            // Let letters through to the TextBox; swallow everything else (Space,
            // Enter, ...) so the input stays clean.
            if (!IsLabelKey(e.Key))
            {
                e.Handled = true;
            }
        }

        private static bool IsLabelKey(Key key)
        {
            return key >= Key.A && key <= Key.Z;
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
