using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using HuntAndPeck.Models;
using HuntAndPeck.ViewModels;

namespace HuntAndPeck.Views
{
    /// <summary>
    /// Draws each hint label as its own <see cref="DrawingVisual"/> child, so an
    /// <c>Active</c> flip (typing) re-renders only that one label instead of the whole
    /// overlay. <see cref="FormattedText"/> is still built once per label and cached; a
    /// keystroke just re-opens the changed visual's drawing context. DrawingVisuals
    /// bypass measure/arrange, so layout cost stays flat regardless of label count.
    /// </summary>
    public class HintCanvas : FrameworkElement
    {
        private static readonly Brush TextBrush = Brushes.Black;

        // Bundled font family. JetBrains Mono NL = JetBrains Mono with ligatures disabled,
        // so punctuation label pairs (e.g. "//") never render as programming ligatures. The
        // TTFs are embedded as Resources (Fonts/*.ttf) and resolved via pack URI, so the app
        // renders them even when the font is not installed on the box.
        private const string BundledFontFamily = "JetBrains Mono NL";

        // Label typeface, rebuilt in BuildFormatted from the per-overlay
        // FontFamilyReadValue. Was a static hardcoded "Helvetica, Arial"; now configurable
        // (default bundled JetBrains Mono NL) while the Bold weight is preserved.
        private Typeface _typeface;

        private readonly VisualCollection _visuals;

        // Parallel by index: the view-model, its cached text, and the visual that draws it.
        private List<HintViewModel> _hints;
        private FormattedText[] _formatted;
        private List<DrawingVisual> _visualByHint;
        private double _fontSize = 14;

        // Device scale (TransformToDevice) of the overlay's monitor; set by
        // OverlayView. Label SIZE constants are multiplied by this (see DpiScale).
        private double _dpi = 1.0;

        // Pill fill brushes, rebuilt when PillOpacity changes. Semi-transparent so the
        // vivid yellow softens and background peeks through; the text stays fully opaque
        // (crisp). Opacity is configurable via HintPillOpacity (default 0.8).
        private Brush _activeBg;
        private Brush _inactiveBg;

        // Padding between the label text and the edge of its pill.
        private const double Pad = 2.0;
        private const double DefaultCornerRadius = 3.0;
        private const double DefaultPillOpacity = 0.8;

        public HintCanvas()
        {
            _visuals = new VisualCollection(this);
            BuildBrushes();
        }

        /// <summary>
        /// Pill fill opacity (0-1), bound from the view-model. Softens the vivid yellow;
        /// the text stays fully opaque regardless. Changing it rebuilds the brushes and
        /// re-renders every label.
        /// </summary>
        public static readonly DependencyProperty PillOpacityProperty =
            DependencyProperty.Register("PillOpacity", typeof(double), typeof(HintCanvas),
                new FrameworkPropertyMetadata(DefaultPillOpacity, OnPillOpacityChanged));

        public double PillOpacity
        {
            get { return (double)GetValue(PillOpacityProperty); }
            set { SetValue(PillOpacityProperty, value); }
        }

        private static void OnPillOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            c.BuildBrushes();
            if (c._hints != null)
            {
                for (int i = 0; i < c._hints.Count; i++)
                {
                    c.RenderHint(i);
                }
            }
        }

        /// <summary>
        /// When true, an inactive (non-matching) hint renders nothing instead of the dim
        /// pill -- so after typing the first char of a label, only matching labels show.
        /// Bound from the view-model; changing it re-renders every hint.
        /// </summary>
        public static readonly DependencyProperty HideInactiveProperty =
            DependencyProperty.Register("HideInactive", typeof(bool), typeof(HintCanvas),
                new FrameworkPropertyMetadata(false, OnHideInactiveChanged));

        public bool HideInactive
        {
            get { return (bool)GetValue(HideInactiveProperty); }
            set { SetValue(HideInactiveProperty, value); }
        }

        private static void OnHideInactiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            if (c._hints != null)
            {
                for (int i = 0; i < c._hints.Count; i++)
                {
                    c.RenderHint(i);
                }
            }
        }

        /// <summary>
        /// Device scale factor (TransformToDevice) of the overlay's monitor. Label
        /// SIZE constants (font emSize, padding, corner radius) are scaled by this so
        /// they render DPI-correct; hint POSITIONS stay in physical px (they already
        /// round-trip through the layoutGrid 1/scale transform). Without it, sizes come
        /// out ~fontSize physical px regardless of DPI (tiny on high-DPI / scaled
        /// displays, e.g. 4K @ 300%). Setting it rebuilds the cached FormattedText
        /// (emSize depends on it) and re-renders every label.
        /// </summary>
        public double DpiScale
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
                if (_hints != null && _hints.Count > 0)
                {
                    BuildFormatted();
                    for (int i = 0; i < _hints.Count; i++)
                    {
                        RenderHint(i);
                    }
                }
            }
        }

        private void BuildBrushes()
        {
            double alpha = PillOpacity;
            if (alpha < 0) alpha = 0;
            else if (alpha > 1) alpha = 1;
            _activeBg = SemiBrush(alpha, 0xFF, 0xFF, 0x00);    // yellow
            _inactiveBg = SemiBrush(alpha, 0xFF, 0xFA, 0xCD);  // light yellow
        }

        private static Brush SemiBrush(double alpha, byte r, byte g, byte b)
        {
            byte a = (byte)Math.Round(alpha * 255.0);
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }

        public static readonly DependencyProperty HintsSourceProperty =
            DependencyProperty.Register("HintsSource", typeof(IList), typeof(HintCanvas),
                new FrameworkPropertyMetadata(OnHintsSourceChanged));

        public IList HintsSource
        {
            get { return (IList)GetValue(HintsSourceProperty); }
            set { SetValue(HintsSourceProperty, value); }
        }

        protected override int VisualChildrenCount
        {
            get { return _visuals.Count; }
        }

        protected override Visual GetVisualChild(int index)
        {
            return _visuals[index];
        }

        private static void OnHintsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            c.DetachAll();
            c._visuals.Clear();
            c._hints = null;
            c._formatted = null;
            c._visualByHint = null;

            var list = e.NewValue as IList;
            if (list != null && list.Count > 0)
            {
                c._hints = new List<HintViewModel>(list.Count);
                c._visualByHint = new List<DrawingVisual>(list.Count);
                foreach (var item in list)
                {
                    var h = item as HintViewModel;
                    if (h != null)
                    {
                        c._hints.Add(h);
                        h.PropertyChanged += c.Hint_PropertyChanged;
                    }
                }
                c.BuildFormatted();

                for (int i = 0; i < c._hints.Count; i++)
                {
                    var dv = new DrawingVisual();
                    c._visuals.Add(dv);
                    c._visualByHint.Add(dv);
                    c.RenderHint(i);
                }
            }
        }

        private void DetachAll()
        {
            if (_hints == null)
            {
                return;
            }
            foreach (var h in _hints)
            {
                h.PropertyChanged -= Hint_PropertyChanged;
            }
        }

        private void Hint_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Only Active changes during an overlay; re-render just this hint's visual
            // (not the whole overlay, as the old InvalidateVisual approach did).
            if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != "Active")
            {
                return;
            }
            var h = sender as HintViewModel;
            if (h == null || _hints == null)
            {
                return;
            }
            int idx = _hints.IndexOf(h);
            if (idx >= 0)
            {
                RenderHint(idx);
            }
        }

        private void BuildFormatted()
        {
            if (_hints == null || _hints.Count == 0)
            {
                _formatted = null;
                return;
            }
            if (!double.TryParse(_hints[0].FontSizeReadValue, out var fs) || fs <= 0)
            {
                fs = 14;
            }
            _fontSize = fs;
            // Build the typeface from the per-overlay family (default bundled JetBrains
            // Mono NL when unset). Bold weight is preserved.
            string family = string.IsNullOrWhiteSpace(_hints[0].FontFamilyReadValue)
                ? BundledFontFamily : _hints[0].FontFamilyReadValue;
            _typeface = new Typeface(ResolveFontFamily(family), FontStyles.Normal,
                FontWeights.Bold, FontStretches.Normal);
            double emSize = _fontSize * _dpi;
            _formatted = new FormattedText[_hints.Count];
            for (int i = 0; i < _hints.Count; i++)
            {
                _formatted[i] = new FormattedText(_hints[i].Label ?? "", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, _typeface, emSize, TextBrush);
            }
        }

        /// <summary>
        /// Resolves the configured label font family to a WPF <see cref="FontFamily"/>.
        /// The bundled family (<see cref="BundledFontFamily"/>) is served from the
        /// embedded Fonts/ resources via pack URI (self-contained; renders even when the
        /// font is not installed). Any other name is treated as an installed/system
        /// family, with comma fallbacks allowed (e.g. "Consolas, Courier New").
        /// </summary>
        private static FontFamily ResolveFontFamily(string name)
        {
            if (string.Equals(name, BundledFontFamily, StringComparison.OrdinalIgnoreCase))
            {
                return new FontFamily(new Uri("pack://application:,,,/Fonts/"), "./#" + BundledFontFamily);
            }
            return new FontFamily(name);
        }

        /// <summary>
        /// Re-renders hint <paramref name="i"/> into its own DrawingVisual: a semi-
        /// transparent rounded pill plus the cached label text, positioned at the hint's
        /// bounds. Overall dim/hide is driven by the canvas <c>Opacity</c> (bound to
        /// <c>LabelOpacity</c>), not here.
        /// </summary>
        private void RenderHint(int i)
        {
            var h = _hints[i];
            // Hide-non-matching: an inactive label renders nothing (clears its visual).
            if (!h.Active && HideInactive)
            {
                using (var dc = _visualByHint[i].RenderOpen()) { }
                return;
            }
            var ft = _formatted[i];
            var br = h.Hint.BoundingRectangle;

            // Scale the size constants by the device DPI so the pill renders at a
            // DPI-correct physical size; positions (br.Left/Top) stay in physical px.
            double pad = Pad * _dpi;
            double radius = DefaultCornerRadius * _dpi;
            double pillW = ft.Width + pad * 2;
            double pillH = ft.Height + pad * 2;
            // PointHint: br.Left/Top IS the cursor target, so center the pill on it.
            // Previously the pill was top-left-anchored there, which sat every label
            // down-right of its click point and left the grid with asymmetric margins
            // (left/top blank larger than right/bottom). UI-automation hints: br is the
            // element rect; keep the label at its top-left corner as before.
            double x, y;
            if (h.Hint is PointHint)
            {
                x = br.Left - pillW / 2.0;
                y = br.Top - pillH / 2.0;
            }
            else
            {
                x = br.Left;
                y = br.Top;
            }

            using (var dc = _visualByHint[i].RenderOpen())
            {
                dc.DrawRoundedRectangle(h.Active ? _activeBg : _inactiveBg, null,
                    new Rect(x, y, pillW, pillH), radius, radius);
                dc.DrawText(ft, new Point(x + pad, y + pad));
            }
        }
    }
}
