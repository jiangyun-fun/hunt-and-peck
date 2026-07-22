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
        // Semi-transparent pill fills (alpha 0xCC ~= 0.8): softens the vivid yellow and
        // lets a hint of the background show through. The text brush stays fully opaque,
        // so the label stays crisp (dimming the whole canvas would also dull the text).
        private static readonly Brush ActiveBg = SemiBrush(0xCC, 0xFF, 0xFF, 0x00);   // yellow
        private static readonly Brush InactiveBg = SemiBrush(0xCC, 0xFF, 0xFA, 0xCD); // light yellow
        private static readonly Brush TextBrush = Brushes.Black;
        private static readonly Typeface LabelTypeface =
            new Typeface(new FontFamily("Helvetica, Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private readonly VisualCollection _visuals;

        // Parallel by index: the view-model, its cached text, and the visual that draws it.
        private List<HintViewModel> _hints;
        private FormattedText[] _formatted;
        private List<DrawingVisual> _visualByHint;
        private double _fontSize = 14;

        // Padding between the label text and the edge of its pill.
        private const double Pad = 2.0;

        public HintCanvas()
        {
            _visuals = new VisualCollection(this);
        }

        private static Brush SemiBrush(byte a, byte r, byte g, byte b)
        {
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
            _formatted = new FormattedText[_hints.Count];
            for (int i = 0; i < _hints.Count; i++)
            {
                _formatted[i] = new FormattedText(_hints[i].Label ?? "", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, LabelTypeface, _fontSize, TextBrush);
            }
        }

        /// <summary>
        /// Re-renders hint <paramref name="i"/> into its own DrawingVisual: a semi-
        /// transparent rounded pill plus the cached label text, positioned at the hint's
        /// bounds. Overall dim/hide is driven by the canvas <c>Opacity</c> (bound to
        /// <c>LabelOpacity</c>), not here.
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
                dc.DrawRoundedRectangle(h.Active ? ActiveBg : InactiveBg, null,
                    new Rect(x, y, ft.Width + Pad * 2, ft.Height + Pad * 2), 3, 3);
                dc.DrawText(ft, new Point(x + Pad, y + Pad));
            }
        }
    }
}
