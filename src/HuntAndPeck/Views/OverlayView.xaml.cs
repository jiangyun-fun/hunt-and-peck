using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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

        // HWND cached at load so the re-assert timer can re-position us without
        // re-querying WindowInteropHelper every tick.
        private IntPtr _hwnd;

        // Re-asserts HWND_TOPMOST on a short cadence so labels stay above any
        // same-band popup (context menu / dropdown / tooltip) that opens AFTER
        // the overlay loads (the continuous-mode case). Stopped on close.
        private DispatcherTimer _topmostReassertTimer;

        // How often the overlay re-asserts topmost while it is up. 100 ms is
        // imperceptible vs. menu-open latency; SetWindowPos on an already-topmost
        // window is ~free.
        private const int TopmostReassertIntervalMs = 100;

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

            // Tell the label renderer the device scale so it sizes fonts / pills /
            // corner radii up by it; hint positions already round-trip. Without this
            // the 1/scale transform above cancels WPF's DPI render for sizes and the
            // labels render at raw physical px (tiny on high-DPI / scaled displays).
            hintCanvas.DpiScale = _scaleX;

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
            _hwnd = new WindowInteropHelper(this).Handle;
            ReassertTopmost();

            // In continuous mode a label-click can open a NEW popup (context menu,
            // dropdown, tooltip) AFTER load. That popup lands above us in the
            // topmost band and buries our labels until we re-assert. A short timer
            // keeps us on top of any same-band popup that appears mid-session.
            // SWP_NOACTIVATE means we never steal activation, so the menu beneath
            // stays open. (Cannot reach toast notifications -- those live in
            // ZBID_IMMERSIVE_NOTIFICATION, above all ZBID_DESKTOP windows.)
            _topmostReassertTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TopmostReassertIntervalMs)
            };
            _topmostReassertTimer.Tick += (s, ev) => ReassertTopmost();
            _topmostReassertTimer.Start();

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
            // WS_EX_NOACTIVATE always on: the overlay must never be activated (it
            // reads input via the global hook, not focus), so closing it causes no
            // foreground transition that would dismiss an open context menu beneath.
            ext |= User32.WS_EX_NOACTIVATE;
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

        /// <summary>
        /// Re-asserts this overlay at the top of the topmost z-order band WITHOUT
        /// stealing activation (SWP_NOACTIVATE), so labels paint above an open
        /// context menu / dropdown / tooltip without dismissing it. Called once on
        /// load and then on a short timer to cover popups that appear mid-session.
        /// </summary>
        private void ReassertTopmost()
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }
            User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }

        protected override void OnClosed(EventArgs e)
        {
            // Stop the re-assert timer and drop the HWND so a late tick (if any)
            // can't touch a destroyed window.
            if (_topmostReassertTimer != null)
            {
                _topmostReassertTimer.Stop();
                _topmostReassertTimer = null;
            }
            _hwnd = IntPtr.Zero;
            base.OnClosed(e);
        }
    }
}
