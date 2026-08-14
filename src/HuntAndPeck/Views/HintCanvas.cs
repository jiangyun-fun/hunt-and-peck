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

        // Group view: the dedicated visual that draws the first-char group boxes
        // (dotted rects + key-char pills). Always _visuals[0], BELOW the hint pills
        // (they never intentionally coexist; this just keeps transient re-renders sane).
        private readonly DrawingVisual _groupVisual;

        // Parallel by index: the view-model, its cached text, and the visual that draws it.
        private List<HintViewModel> _hints;
        private FormattedText[] _formatted;
        private List<DrawingVisual> _visualByHint;
        private double _fontSize = 14;

        // Group view state: the parsed boxes, their cached char texts, the suffix
        // texts for level 2 (label minus the typed prefix), and the parsed group
        // char font size (0 = follow the label size).
        private List<GroupHintBox> _groupBoxes = new List<GroupHintBox>();
        private List<FormattedText> _groupTexts = new List<FormattedText>();
        private Dictionary<int, FormattedText> _suffixFormatted;
        private double _groupFontSize;

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
            _groupVisual = new DrawingVisual();
            _visuals.Add(_groupVisual);
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
        /// Group view on/off (bound from the view-model; <c>GroupViewEnabled</c> config,
        /// toggled live by <c>&lt;leader&gt;p</c>). While on and no label prefix is typed,
        /// only the group boxes show (every hint pill renders nothing); once a prefix is
        /// typed, only the matching group's pills show, labeled by the label minus the
        /// typed prefix (the group char is known, so just the second char displays).
        /// Changing it re-renders every hint plus the group visual.
        /// </summary>
        public static readonly DependencyProperty GroupViewProperty =
            DependencyProperty.Register("GroupView", typeof(bool), typeof(HintCanvas),
                new FrameworkPropertyMetadata(false, OnGroupViewChanged));

        public bool GroupView
        {
            get { return (bool)GetValue(GroupViewProperty); }
            set { SetValue(GroupViewProperty, value); }
        }

        private static void OnGroupViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            c.RenderGroupVisual();
            c.ReRenderAllHints();
        }

        /// <summary>
        /// The group boxes (IList of <see cref="GroupHintBox"/>), one per first-char
        /// label group, rebuilt by the view-model on each session load. Changing it
        /// rebuilds the cached key-char texts and re-renders the group visual.
        /// </summary>
        public static readonly DependencyProperty GroupBoxesSourceProperty =
            DependencyProperty.Register("GroupBoxesSource", typeof(IList), typeof(HintCanvas),
                new FrameworkPropertyMetadata(OnGroupBoxesSourceChanged));

        public IList GroupBoxesSource
        {
            get { return (IList)GetValue(GroupBoxesSourceProperty); }
            set { SetValue(GroupBoxesSourceProperty, value); }
        }

        private static void OnGroupBoxesSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            c.ParseGroupBoxes();
            c.RenderGroupVisual();
        }

        /// <summary>
        /// Length of the typed label prefix (bound from the view-model's match string).
        /// In group view, 0 = level 1 (boxes only) and greater = level 2 (prefix-stripped
        /// labels). Changing it clears the suffix-text cache and re-renders everything.
        /// </summary>
        public static readonly DependencyProperty GroupMatchLengthProperty =
            DependencyProperty.Register("GroupMatchLength", typeof(int), typeof(HintCanvas),
                new FrameworkPropertyMetadata(0, OnGroupMatchLengthChanged));

        public int GroupMatchLength
        {
            get { return (int)GetValue(GroupMatchLengthProperty); }
            set { SetValue(GroupMatchLengthProperty, value); }
        }

        private static void OnGroupMatchLengthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            c._suffixFormatted = null;
            c.RenderGroupVisual();
            c.ReRenderAllHints();
        }

        /// <summary>
        /// Group key-char font size as a raw string (hot-reload; 0/blank = follow the
        /// label font size). Changing it rebuilds the cached key-char texts and
        /// re-renders the group visual.
        /// </summary>
        public static readonly DependencyProperty GroupFontSizeTextProperty =
            DependencyProperty.Register("GroupFontSizeText", typeof(string), typeof(HintCanvas),
                new FrameworkPropertyMetadata(OnGroupFontSizeTextChanged));

        public string GroupFontSizeText
        {
            get { return (string)GetValue(GroupFontSizeTextProperty); }
            set { SetValue(GroupFontSizeTextProperty, value); }
        }

        private static void OnGroupFontSizeTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            c._groupFontSize = 0;   // invalidate the parsed cache so the new size applies
            c.ParseGroupBoxes();
            c.RenderGroupVisual();
        }

        private void ReRenderAllHints()
        {
            if (_hints != null)
            {
                for (int i = 0; i < _hints.Count; i++)
                {
                    RenderHint(i);
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
                    _suffixFormatted = null;      // emSize depends on DPI; rebuild lazily
                    for (int i = 0; i < _hints.Count; i++)
                    {
                        RenderHint(i);
                    }
                }
                ParseGroupBoxes();                // group char texts are DPI-scaled too
                RenderGroupVisual();
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
            // The group visual is a permanent child (index 0, below the pills).
            c._visuals.Add(c._groupVisual);
            c._hints = null;
            c._formatted = null;
            c._visualByHint = null;
            c._suffixFormatted = null;
            c._groupFontSize = 0;      // fallback follows the (possibly new) label size

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
                _formatted[i] = BuildText(_hints[i].Label ?? "", emSize);
            }
            // The typeface just came into existence: if the boxes binding pulled before
            // the hints binding (initial attach order is not guaranteed), re-parse so the
            // key-char texts build now.
            if (_groupBoxes.Count == 0 && GroupBoxesSource != null && GroupBoxesSource.Count > 0)
            {
                ParseGroupBoxes();
            }
        }

        /// <summary>Builds a label <see cref="FormattedText"/> with the cached typeface.</summary>
        private FormattedText BuildText(string text, double emSize)
        {
            return new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, _typeface, emSize, TextBrush);
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
        /// <para>
        /// Group view changes what shows: with no typed prefix (level 1) every pill is
        /// suppressed so only the group boxes draw; with a prefix (level 2) matching
        /// hints draw with the prefix stripped from their text (only the second char
        /// shows) and non-matching hints are hidden regardless of <c>HideInactive</c>.
        /// </para>
        /// </summary>
        private void RenderHint(int i)
        {
            var h = _hints[i];
            FormattedText ft;
            if (GroupView)
            {
                // Level 1 (no prefix): boxes only -- suppress every pill.
                // Level 2: matching pills with the prefix stripped; others hidden.
                if (GroupMatchLength == 0 || !h.Active)
                {
                    using (var dc = _visualByHint[i].RenderOpen()) { }
                    return;
                }
                ft = SuffixText(i);
            }
            else
            {
                // Hide-non-matching: an inactive label renders nothing (clears its visual).
                if (!h.Active && HideInactive)
                {
                    using (var dc = _visualByHint[i].RenderOpen()) { }
                    return;
                }
                ft = _formatted[i];
            }
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

        // -------- Group view (progressive 1-char labels) --------

        // Visual padding around a group box's tight bounds so the box reads as enclosing
        // the labels that will appear inside it.
        private const double GroupBoxPad = 5.0;

        /// <summary>
        /// Parses <see cref="GroupBoxesSource"/> into the local box list and (re)builds
        /// the cached key-char texts. The char font size is <see cref="GroupFontSizeText"/>
        /// when positive, else the label font size. Key-char texts are only built once the
        /// label typeface exists (BuildFormatted assigns it); if the boxes binding pulls
        /// before the hints binding on the initial attach, BuildFormatted re-parses.
        /// </summary>
        private void ParseGroupBoxes()
        {
            _groupBoxes.Clear();
            _groupTexts.Clear();
            if (_typeface == null)
            {
                return;
            }

            var src = GroupBoxesSource;
            if (src != null)
            {
                foreach (var item in src)
                {
                    if (item is GroupHintBox)
                    {
                        var g = (GroupHintBox)item;
                        _groupBoxes.Add(g);
                        _groupTexts.Add(BuildText(g.Key.ToString(CultureInfo.CurrentCulture), GroupCharEmSize()));
                    }
                }
            }
        }

        private double GroupCharEmSize()
        {
            if (_groupFontSize <= 0)
            {
                double fs;
                if (!double.TryParse(GroupFontSizeText, out fs) || fs <= 0)
                {
                    fs = _fontSize; // 0/blank = follow the label font size
                }
                _groupFontSize = fs;
            }
            return _groupFontSize * _dpi;
        }

        /// <summary>
        /// Draws (or clears) the group boxes: one dotted rounded rect per group plus its
        /// key char in a small yellow pill at the box's top-left corner. Only drawn at
        /// level 1 (GroupView on, no typed prefix); at level 2 the boxes clear so only
        /// the group's prefix-stripped pills show.
        /// </summary>
        private void RenderGroupVisual()
        {
            bool show = GroupView && GroupMatchLength == 0
                && _groupBoxes != null && _groupBoxes.Count > 0;
            if (!show)
            {
                using (var dc = _groupVisual.RenderOpen()) { }
                return;
            }

            double pad = Pad * _dpi;
            double radius = DefaultCornerRadius * _dpi;
            double inflate = GroupBoxPad * _dpi;
            using (var dc = _groupVisual.RenderOpen())
            {
                for (int i = 0; i < _groupBoxes.Count; i++)
                {
                    var g = _groupBoxes[i];
                    var rect = new Rect(
                        g.Bounds.Left - inflate,
                        g.Bounds.Top - inflate,
                        g.Bounds.Width + inflate * 2,
                        g.Bounds.Height + inflate * 2);
                    dc.DrawRoundedRectangle(null, DottedPen(), rect, radius, radius);

                    // Key-char pill centered in the box. A corner pill sits exactly
                    // where box boundaries meet (ambiguous ownership at a glance);
                    // centered matches the zone-zoom pick view (labels at zone centers)
                    // and reads as the target.
                    var ft = _groupTexts[i];
                    double pillW = ft.Width + pad * 2;
                    double pillH = ft.Height + pad * 2;
                    double px = rect.Left + (rect.Width - pillW) / 2.0;
                    double py = rect.Top + (rect.Height - pillH) / 2.0;
                    dc.DrawRoundedRectangle(_activeBg, null,
                        new Rect(px, py, pillW, pillH), radius, radius);
                    dc.DrawText(ft, new Point(px + pad, py + pad));
                }
            }
        }

        /// <summary>
        /// The label text for hint <paramref name="i"/> in group-view level 2: the full
        /// label minus the typed prefix (the group char is already known, so only what
        /// remains -- typically the single second char -- displays). Cached per index;
        /// the cache lives until the match length or the session changes.
        /// </summary>
        private FormattedText SuffixText(int i)
        {
            FormattedText ft;
            if (_suffixFormatted != null && _suffixFormatted.TryGetValue(i, out ft))
            {
                return ft;
            }
            var label = _hints[i].Label ?? "";
            int len = Math.Min(GroupMatchLength, label.Length);
            ft = BuildText(len < label.Length ? label.Substring(len) : string.Empty, _fontSize * _dpi);
            if (_suffixFormatted == null)
            {
                _suffixFormatted = new Dictionary<int, FormattedText>();
            }
            _suffixFormatted[i] = ft;
            return ft;
        }

        /// <summary>
        /// The dotted box outline pen (round dash cap + a 0-length dash = round dots).
        /// Dash values are multiples of the pen thickness in WPF.
        /// </summary>
        private Pen _dottedPen;

        private Pen DottedPen()
        {
            if (_dottedPen == null || _dottedPen.Thickness != 1.5 * _dpi)
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB4, 0x40, 0x40, 0x40)), 1.5 * _dpi);
                pen.DashStyle = new DashStyle(new DoubleCollection { 0.0, 3.0 }, 0);
                pen.DashCap = PenLineCap.Round;
                pen.Freeze();
                _dottedPen = pen;
            }
            return _dottedPen;
        }
    }
}
