using HuntAndPeck.Models;

namespace HuntAndPeck.ViewModels
{
    public class HintViewModel : NotifyPropertyChanged
    {
        private string _label;
        private bool _active;
        private string _fontSizeReadValue;

        public HintViewModel(Hint hint, string fontSize)
        {
            Hint = hint;
            // Font size is read once per overlay by the OverlayViewModel and passed
            // in, so we don't re-read the config file for every hint.
            FontSizeReadValue = fontSize;
        }

        public Hint Hint { get; set; }

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
