using HuntAndPeck.Models;
using HuntAndPeck.Properties;

namespace HuntAndPeck.ViewModels
{
    public class HintViewModel : NotifyPropertyChanged
    {
        private string _label;
        private bool _active;
        private string _fontSizeReadValue;
        private double _canvasLeft;
        private double _canvasTop;

        public HintViewModel(Hint hint)
        {
            Hint = hint;
            FontSizeReadValue = Settings.Default.FontSize;
            // Canvas position is owned here (INPC) so arrow-slide can move a label
            // without touching the underlying Hint. Initialized from the hint's
            // window-relative bounding rectangle.
            CanvasLeft = hint.BoundingRectangle.Left;
            CanvasTop = hint.BoundingRectangle.Top;
        }

        public Hint Hint { get; set; }

        /// <summary>Canvas.Left for this label (window-relative px). Slides on nudge.</summary>
        public double CanvasLeft
        {
            get { return _canvasLeft; }
            set { _canvasLeft = value; NotifyOfPropertyChange(); }
        }

        /// <summary>Canvas.Top for this label (window-relative px). Slides on nudge.</summary>
        public double CanvasTop
        {
            get { return _canvasTop; }
            set { _canvasTop = value; NotifyOfPropertyChange(); }
        }

        public bool Active
        {
            get { return _active; }
            set { _active = value; NotifyOfPropertyChange(); }
        }

        public string Label
        {
            get { return _label; }
            set { _label = value; NotifyOfPropertyChange(); }
        }

        public string FontSizeReadValue
        {
            get { return _fontSizeReadValue; }
            set { _fontSizeReadValue = value; NotifyOfPropertyChange(); }
        }
    }
}
