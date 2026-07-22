using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
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
        private static readonly Brush ActiveBg = Brushes.Yellow;
        private static readonly Brush InactiveBg = Brushes.LightYellow;
        private static readonly Brush TextBrush = Brushes.Black;
        private static readonly Typeface LabelTypeface =
            new Typeface(new FontFamily("Helvetica, Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private readonly VisualCollection _visuals;

        // Parallel by index: the view-model, its cached text + outline geometry, and
        // the visual that draws it.
        private List<HintViewModel> _hints;
        private FormattedText[] _formatted;
        private Geometry[] _geometry;          // text outline, used in read-mode rendering
        private List<DrawingVisual> _visualByHint;
        private double _fontSize = 14;
        private Pen _outlinePen;               // black stroke around read-mode glyphs

        // Padding between the label text and the edge of its pill. Shared by the
        // pill rect and the cached geometry origin so they line up exactly.
        private const double Pad = 2.0;

        public HintCanvas()
        {
            _visuals = new VisualCollection(this);
        }

        public static readonly DependencyProperty HintsSourceProperty =
            DependencyProperty.Register("HintsSource", typeof(IList), typeof(HintCanvas),
                new FrameworkPropertyMetadata(OnHintsSourceChanged));

        public IList HintsSource
        {
            get { return (IList)GetValue(HintsSourceProperty); }
            set { SetValue(HintsSourceProperty, value); }
        }

        /// <summary>
        /// Read-mode (backtick): labels render as a two-tone outline (yellow fill +
        /// black stroke) with no pill, at full opacity. A literal single-color hollow
        /// outline cannot be crisp on both light and dark backgrounds, so the fill is
        /// kept (thin -- just the glyph interior) and the stroke provides the edge on
        /// light backgrounds while the fill pops on dark ones. Occludes almost nothing,
        /// so the text behind stays readable.
        /// </summary>
        public static readonly DependencyProperty ReadModeProperty =
            DependencyProperty.Register("ReadMode", typeof(bool), typeof(HintCanvas),
                new FrameworkPropertyMetadata(false, OnReadModeChanged));

        public bool ReadMode
        {
            get { return (bool)GetValue(ReadModeProperty); }
            set { SetValue(ReadModeProperty, value); }
        }

        private static void OnReadModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            if (c._hints == null)
            {
                return;
            }
            // Re-render every label into its own visual (no full InvalidateVisual).
            for (int i = 0; i < c._hints.Count; i++)
            {
                c.RenderHint(i);
            }
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
            c._geometry = null;
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
                _geometry = null;
                return;
            }
            if (!double.TryParse(_hints[0].FontSizeReadValue, out var fs) || fs <= 0)
            {
                fs = 14;
            }
            _fontSize = fs;

            // Outline pen scales with the font so the stroke stays proportional.
            // Frozen so it can be reused across render passes without re-copying.
            _outlinePen = new Pen(Brushes.Black, Math.Max(1.0, _fontSize / 9.0));
            _outlinePen.Freeze();

            _formatted = new FormattedText[_hints.Count];
            _geometry = new Geometry[_hints.Count];
            for (int i = 0; i < _hints.Count; i++)
            {
                var ft = new FormattedText(_hints[i].Label ?? "", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, LabelTypeface, _fontSize, TextBrush);
                _formatted[i] = ft;
                // The text outline geometry, baked at the label's absolute position
                // (same origin DrawText uses) so read-mode needs no per-draw transform.
                // BuildGeometry ignores the FormattedText brush; fill is set at draw time.
                var br = _hints[i].Hint.BoundingRectangle;
                _geometry[i] = ft.BuildGeometry(new Point(br.Left + Pad, br.Top + Pad));
            }
        }

        /// <summary>
        /// Re-renders hint <paramref name="i"/> into its own DrawingVisual, positioned
        /// at the hint's bounds. Base mode: a colored pill + solid black text (already
        /// legible on any background, including dark -- the bright pill contrasts).
        /// Read-mode: a two-tone outline (fill + black stroke) with no pill, so labels
        /// stay crisp on any background while the text behind stays readable.
        /// </summary>
        private void RenderHint(int i)
        {
            var ft = _formatted[i];
            var h = _hints[i];
            var br = h.Hint.BoundingRectangle;
            double x = br.Left;
            double y = br.Top;

            using (var dc = _visualByHint[i].RenderOpen())
            {
                if (ReadMode)
                {
                    // Two-tone outline (not literally hollow): a single-color outline
                    // vanishes on one background extreme. Yellow fill pops on dark; the
                    // black stroke gives the edge on light. No pill -> read-through.
                    dc.DrawGeometry(h.Active ? ActiveBg : InactiveBg, _outlinePen, _geometry[i]);
                }
                else
                {
                    dc.DrawRoundedRectangle(h.Active ? ActiveBg : InactiveBg, null,
                        new Rect(x, y, ft.Width + Pad * 2, ft.Height + Pad * 2), 3, 3);
                    dc.DrawText(ft, new Point(x + Pad, y + Pad));
                }
            }
        }
    }
}
