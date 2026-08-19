using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;

namespace HuntAndPeck.Views
{
    /// <summary>
    /// The persistent quadrant guide: an always-on-top, click-through, near-invisible
    /// window per monitor that draws a faint center cross and one letter per quadrant,
    /// marking the four regions the quadrant hotkeys (default Ctrl+Shift+F1..F4 =
    /// TL/TR/BL/BR) open. A spatial memory aid visible during NORMAL use; the hint
    /// overlay (which re-asserts topmost) paints above it while open.
    /// <para>
    /// Code-only (no XAML): the content is fully drawn (no bindings). Built once at
    /// startup via <see cref="CreateForAllScreens"/> (config: QuadrantGuideEnabled /
    /// QuadrantGuideLabels -- restart to apply), exactly one window per
    /// <c>Screen.AllScreens</c> so each monitor gets its own cross at its own DPI.
    /// </para>
    /// </summary>
    public class QuadrantGuideWindow : ForegroundWindow
    {
        // Faintness of the whole guide: visible against most backgrounds without
        // reading as screen furniture. Tunable on the box if it fights dark themes.
        private const double GuideOpacity = 0.30;

        // Quadrant letter size (px at 96 DPI; scaled by the monitor's device scale,
        // like HintCanvas scales its label sizes).
        private const double LabelEmSize = 16.0;

        // Letter pill padding / corner radius (px at 96 DPI).
        private const double LabelPad = 4.0;
        private const double LabelRadius = 3.0;

        private readonly Rect _monitor;
        private readonly string[] _labels;
        private readonly GuideCanvas _canvas;
        private readonly Grid _root = new Grid();

        /// <param name="monitor">Monitor bounds in PHYSICAL pixels (Screen.Bounds).</param>
        /// <param name="labels">Four labels in scan order TL, TR, BL, BR (more are
        /// ignored; the factory guarantees four).</param>
        public QuadrantGuideWindow(Rect monitor, string[] labels)
        {
            _monitor = monitor;
            _labels = labels;
            _canvas = new GuideCanvas(monitor, labels)
            {
                Opacity = GuideOpacity
            };
            _root.Children.Add(_canvas);

            // Same window shape as the overlay: borderless transparent topmost that
            // never activates and is click-through (set on load, once the HWND exists).
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Topmost = true;
            ShowActivated = false;
            Background = Brushes.Transparent;
            Content = _root;
            Loaded += QuadrantGuideWindow_OnLoaded;
        }

        // The guide must never steal foreground (nothing to focus) and never close
        // itself on deactivation (it is permanently deactivated by design).
        protected override bool ForceForegroundOnRender => false;
        protected override bool CloseOnDeactivate => false;

        /// <summary>
        /// One guide window per monitor, or an empty list when the guide is disabled
        /// (QuadrantGuideEnabled=false) or no labels resolve. Must run on the UI
        /// thread (creates windows).
        /// </summary>
        public static IList<QuadrantGuideWindow> CreateForAllScreens()
        {
            var windows = new List<QuadrantGuideWindow>();
            if (!OverlayActionConfig.ReadQuadrantGuideEnabled())
            {
                return windows;
            }
            string[] labels = OverlayActionConfig.ReadQuadrantGuideLabels();
            if (labels.Length < 4)
            {
                return windows;
            }
            foreach (var screen in System.Windows.Forms.Screen.AllScreens)
            {
                var bounds = screen.Bounds;
                var window = new QuadrantGuideWindow(
                    new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), labels);
                window.Show();
                windows.Add(window);
            }
            return windows;
        }

        private void QuadrantGuideWindow_OnLoaded(object sender, RoutedEventArgs e)
        {
            // Same DPI pattern as OverlayView: counter-scale the layout so the canvas
            // can work in physical px, size/position the window in DIU, and hand the
            // device scale to the renderer so its size constants come out DPI-correct.
            var m = PresentationSource.FromVisual(this).CompositionTarget.TransformToDevice;
            double sx = m.M11;
            double sy = m.M22;
            Left = _monitor.Left / sx;
            Top = _monitor.Top / sy;
            Width = _monitor.Width / sx;
            Height = _monitor.Height / sy;
            _root.LayoutTransform = new ScaleTransform(1 / sx, 1 / sy);
            _canvas.Dpi = sx;

            SetClickThrough(true);
            // WS_EX_TOOLWINDOW keeps this permanently-shown window out of Alt+Tab /
            // the task list (SetClickThrough covers TRANSPARENT + NOACTIVATE).
            var hwnd = new WindowInteropHelper(this).Handle;
            int ext = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
            User32.SetWindowLong(hwnd, User32.GWL_EXSTYLE, ext | User32.WS_EX_TOOLWINDOW);
        }

        /// <summary>
        /// Draws the guide: the center cross (dotted, same style as the group-view
        /// zone borders) and one letter pill at each quadrant center. Static content,
        /// so a plain <c>OnRender</c> suffices (no per-element visuals needed).
        /// </summary>
        private sealed class GuideCanvas : FrameworkElement
        {
            private readonly Rect _monitor;
            private readonly string[] _labels;
            private double _dpi;

            public GuideCanvas(Rect monitor, string[] labels)
            {
                _monitor = monitor;
                _labels = labels;
                // Work in physical px (the window counter-scales the layout).
                Width = monitor.Width;
                Height = monitor.Height;
            }

            /// <summary>Device scale of the monitor; setting it re-renders.</summary>
            public double Dpi
            {
                get { return _dpi; }
                set
                {
                    var v = value > 0 ? value : 1.0;
                    if (Math.Abs(v - _dpi) < 1e-9)
                    {
                        return;
                    }
                    _dpi = v;
                    InvalidateVisual();
                }
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (_dpi <= 0)
                {
                    return; // not sized yet; Dpi setter re-renders
                }

                // Cross through the monitor center, dotted like the zone borders.
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB4, 0x40, 0x40, 0x40)), 1.5 * _dpi);
                pen.DashStyle = new DashStyle(new DoubleCollection { 0.0, 3.0 }, 0);
                pen.DashCap = PenLineCap.Round;
                pen.Freeze();
                double midX = _monitor.Left + _monitor.Width / 2.0;
                double midY = _monitor.Top + _monitor.Height / 2.0;
                dc.DrawLine(pen, new Point(midX, _monitor.Top), new Point(midX, _monitor.Bottom));
                dc.DrawLine(pen, new Point(_monitor.Left, midY), new Point(_monitor.Right, midY));

                // One letter pill at each quadrant center (scan order TL,TR,BL,BR --
                // the quadrant hotkey order). Same yellow pill look as the labels.
                var typeface = new Typeface(
                    HintCanvas.ResolveFontFamily("JetBrains Mono NL"),
                    FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var bg = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0x00));
                bg.Freeze();
                double pad = LabelPad * _dpi;
                double radius = LabelRadius * _dpi;
                double em = LabelEmSize * _dpi;
                for (int q = 0; q < 4 && q < _labels.Length; q++)
                {
                    double cx = _monitor.Left + _monitor.Width * (q % 2 == 0 ? 0.25 : 0.75);
                    double cy = _monitor.Top + _monitor.Height * (q < 2 ? 0.25 : 0.75);
                    var text = (_labels[q] ?? string.Empty).ToUpperInvariant();
                    var ft = new FormattedText(text, CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, typeface, em, Brushes.Black);
                    double pillW = ft.Width + pad * 2;
                    double pillH = ft.Height + pad * 2;
                    var rect = new Rect(cx - pillW / 2.0, cy - pillH / 2.0, pillW, pillH);
                    dc.DrawRoundedRectangle(bg, null, rect, radius, radius);
                    dc.DrawText(ft, new Point(rect.Left + pad, rect.Top + pad));
                }
            }
        }
    }
}
