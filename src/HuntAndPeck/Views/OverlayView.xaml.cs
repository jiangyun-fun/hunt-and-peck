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

        // Chrome (badge strip / leader popup / match box) is laid out at fixed
        // physical px (the layoutGrid counter-scale cancels WPF's DPI render, and
        // unlike HintCanvas these XAML panels have no DpiScale hook), so on a
        // high-resolution monitor they render tiny relative to the screen. They are
        // scaled by the monitor's DIU height vs. this 1080p reference (>= 1: a
        // 1080p-equivalent screen keeps today's sizes; 4K@100% doubles them).
        private const double ChromeReferenceHeightPx = 1080.0;
        private const double ChromeScaleMax = 3.0;

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
            // Skippable (TopmostReassertEnabled, hot-reload): the periodic re-assert is the
            // leading suspect for clearing a Continuous-mode selection in the target app, so
            // it can be disabled to test that hypothesis. The one-shot ReassertTopmost()
            // above (initial z-order) still runs; only the periodic timer is gated.
            if (OverlayActionConfig.ReadTopmostReassertEnabled())
            {
                _topmostReassertTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(TopmostReassertIntervalMs)
                };
                _topmostReassertTimer.Tick += (s, ev) => ReassertTopmost();
                _topmostReassertTimer.Start();
            }

            // Measure window-load to content-rendered (the label layout/render cost).
            _renderSw = Stopwatch.StartNew();
            ContentRendered += OverlayView_OnContentRendered;
        }

        /// <summary>
        /// Positions and sizes the overlay window to the view-model's current Bounds,
        /// dividing by the device scale (Bounds is in physical pixels; the window is in
        /// WPF device-independent units). Called on load and on every monitor switch.
        /// Also re-applies the chrome scale (it follows the monitor's resolution).
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
            ApplyChromeScale(vm);
        }

        /// <summary>
        /// Scales the fixed-size chrome (bottom badge strip, leader popup, match box)
        /// with the monitor's resolution: their FontSize/Padding values are physical
        /// px (the layoutGrid counter-scale cancels WPF's DPI render), so without this
        /// they render tiny on a high-resolution monitor. The factor is the monitor's
        /// DIU height (physical / device scale) vs. a 1080p reference, clamped to
        /// [1, <see cref="ChromeScaleMax"/>] -- nothing ever shrinks below today's
        /// sizes. Labels scale separately via HintCanvas.DpiScale + HintFontSize.
        /// </summary>
        private void ApplyChromeScale(OverlayViewModel vm)
        {
            if (vm == null || _scaleY <= 0)
            {
                return;
            }
            double diuHeight = vm.Bounds.Height / _scaleY;
            double s = diuHeight / ChromeReferenceHeightPx;
            if (s < 1.0)
            {
                s = 1.0;
            }
            else if (s > ChromeScaleMax)
            {
                s = ChromeScaleMax;
            }
            var scale = new ScaleTransform(s, s);
            badgeStrip.LayoutTransform = scale;
            leaderPopup.LayoutTransform = scale;
            matchBox.LayoutTransform = scale;
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
