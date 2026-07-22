using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
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

        // Non-activating overlay: do NOT force foreground (that would dismiss any
        // open context menu) and do NOT close on deactivation (we are never
        // activated; dismissal is driven by the keyboard/mouse hook installed by
        // App.ShowOverlay -> OverlayKeyboardHook). Input reaches us through that
        // global hook, not through WPF focus, so we don't need foreground.
        protected override bool ForceForegroundOnRender => false;
        protected override bool CloseOnDeactivate => false;

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

            // Re-assert top-most z-order WITHOUT stealing activation, so we paint
            // above an open context menu but don't dismiss it. Topmost=True keeps
            // us in the topmost band; this re-asserts our position above other
            // topmost popups (e.g. an open right-click menu).
            var hwnd = new WindowInteropHelper(this).Handle;
            User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);

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

        // Key handling lives in OverlayKeyboardHook (a global low-level hook) now,
        // not in WPF PreviewKeyDown, because the overlay is non-activated.

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
