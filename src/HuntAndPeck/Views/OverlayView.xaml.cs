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
                // Default finalize: the cursor is already on the matched label
                // (set by the VM); fire a real left click there and close. The
                // overlay is click-through so the click reaches the app beneath.
                vm.PerformClickAndClose = () =>
                {
                    DoLeftClick();
                    Close();
                };
            }

            // Click-through from the start so a synthesized click (default) and a
            // manual click both reach the app beneath; keyboard focus is unaffected.
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

            if (e.Key == Key.Space && vm != null)
            {
                vm.ToggleMoveOnly();
                e.Handled = true;   // never let Space enter the TextBox
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

        /// <summary>Fires a real left click at the current cursor position.</summary>
        private static void DoLeftClick()
        {
            User32.mouse_event(User32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }
    }
}
