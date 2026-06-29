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
    /// Draws ALL hint labels in a single OnRender pass (one visual) instead of one
    /// TextBlock per hint, so overlay layout cost stays ~constant regardless of
    /// label count. FormattedText is built once per label (cached); OnRender is
    /// cheap and re-runs whenever a hint's Active state flips (typing).
    /// </summary>
    public class HintCanvas : FrameworkElement
    {
        private static readonly Brush ActiveBg = Brushes.Yellow;
        private static readonly Brush InactiveBg = Brushes.LightYellow;
        private static readonly Brush TextBrush = Brushes.Black;
        private static readonly Typeface LabelTypeface =
            new Typeface(new FontFamily("Helvetica, Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        private List<HintViewModel> _hints;
        private FormattedText[] _formatted;
        private double _fontSize = 14;

        public static readonly DependencyProperty HintsSourceProperty =
            DependencyProperty.Register("HintsSource", typeof(IList), typeof(HintCanvas),
                new FrameworkPropertyMetadata(OnHintsSourceChanged));

        public IList HintsSource
        {
            get { return (IList)GetValue(HintsSourceProperty); }
            set { SetValue(HintsSourceProperty, value); }
        }

        private static void OnHintsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = (HintCanvas)d;
            c.DetachAll();
            c._hints = null;
            c._formatted = null;

            var list = e.NewValue as IList;
            if (list != null && list.Count > 0)
            {
                c._hints = new List<HintViewModel>(list.Count);
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
            }
            c.InvalidateVisual();
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
            // Only Active changes during an overlay; redraw is cheap (cached text).
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "Active")
            {
                InvalidateVisual();
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

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (_hints == null || _formatted == null)
            {
                return;
            }
            const double pad = 1.0;
            for (int i = 0; i < _hints.Count; i++)
            {
                var h = _hints[i];
                var ft = _formatted[i];
                var br = h.Hint.BoundingRectangle;
                double x = br.Left;
                double y = br.Top;
                drawingContext.DrawRectangle(h.Active ? ActiveBg : InactiveBg, null,
                    new Rect(x, y, ft.Width + pad * 2, ft.Height + pad * 2));
                drawingContext.DrawText(ft, new Point(x + pad, y + pad));
            }
        }
    }
}
