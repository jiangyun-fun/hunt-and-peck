using HuntAndPeck.Properties;
using HuntAndPeck.Services;
using System;
using System.ComponentModel;
using System.Windows;

namespace HuntAndPeck.ViewModels
{
    internal class OptionsViewModel : INotifyPropertyChanged
    {
        public OptionsViewModel()
        {
            DisplayName = "Options";
            FontSize = Settings.Default.FontSize;
            Settings.Default.PropertyChanged += OnSettingsPropertyChanged;
        }

        public string DisplayName { get; set; }

        // --- FontSize: backed by the .NET Settings store (the dialog's original knob) ---
        private string _fontSize;
        public string FontSize
        // Assign the font size value to a variable and update it every time user
        // changes the option in tray menu
        {
            get { return _fontSize; }
            set
            {
                if (_fontSize != value)
                {
                    _fontSize = value;
                    OnPropertyChanged("FontSize");
                    Settings.Default.FontSize = value;
                    Settings.Default.Save();
                }
            }
        }

        // --- appSettings (hot-reload on the next trigger). Each writes hap.exe.config. ---

        public string HintSource
        {
            get { return Get("HintSource", "Grid"); }
            set { Set("HintSource", value); OnPropertyChanged("HintSource"); }
        }

        public string HintBoundsSource
        {
            get { return Get("HintBoundsSource", "Screen"); }
            set { Set("HintBoundsSource", value); OnPropertyChanged("HintBoundsSource"); }
        }

        public string OverlayTriggerMode
        {
            get { return Get("OverlayTriggerMode", "OneClick"); }
            set { Set("OverlayTriggerMode", value); OnPropertyChanged("OverlayTriggerMode"); }
        }

        public string HintCharacters
        {
            get { return Get("HintCharacters", "ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890"); }
            set { Set("HintCharacters", value); OnPropertyChanged("HintCharacters"); }
        }

        public string GridEdgeStep { get { return Get("GridEdgeStep", "30"); } set { Set("GridEdgeStep", value); OnPropertyChanged("GridEdgeStep"); } }
        public string GridCenterStep { get { return Get("GridCenterStep", "50"); } set { Set("GridCenterStep", value); OnPropertyChanged("GridCenterStep"); } }
        public string GridInset { get { return Get("GridInset", "10"); } set { Set("GridInset", value); OnPropertyChanged("GridInset"); } }
        public string GridEdgeBandPercent { get { return Get("GridEdgeBandPercent", "15"); } set { Set("GridEdgeBandPercent", value); OnPropertyChanged("GridEdgeBandPercent"); } }
        public string MaxEnumerationDepth { get { return Get("MaxEnumerationDepth", "0"); } set { Set("MaxEnumerationDepth", value); OnPropertyChanged("MaxEnumerationDepth"); } }
        public string GridDenseRegions { get { return Get("GridDenseRegions", "Left,Top,TR,BR,Center"); } set { Set("GridDenseRegions", value); OnPropertyChanged("GridDenseRegions"); } }
        public string ClickModeOrder { get { return Get("ClickModeOrder", "Left,Right,Double,Move"); } set { Set("ClickModeOrder", value); OnPropertyChanged("ClickModeOrder"); } }

        public string NudgeStep { get { return Get("NudgeStep", "3"); } set { Set("NudgeStep", value); OnPropertyChanged("NudgeStep"); } }
        public string NudgeStepFast { get { return Get("NudgeStepFast", "15"); } set { Set("NudgeStepFast", value); OnPropertyChanged("NudgeStepFast"); } }

        public bool TimingLogEnabled
        {
            get { return GetBool("TimingLogEnabled"); }
            set { Set("TimingLogEnabled", value ? "true" : "false"); OnPropertyChanged("TimingLogEnabled"); }
        }

        // Hotkey is read once at startup, so editing it here needs a hap.exe restart.
        public string HotkeyKey { get { return Get("HotkeyKey", "M"); } set { Set("HotkeyKey", value); OnPropertyChanged("HotkeyKey"); } }
        public string HotkeyModifier { get { return Get("HotkeyModifier", "Control,Shift"); } set { Set("HotkeyModifier", value); OnPropertyChanged("HotkeyModifier"); } }

        private static string Get(string key, string fallback)
        {
            return OverlayActionConfig.ReadRawString(key) ?? fallback;
        }

        private static bool GetBool(string key)
        {
            bool b;
            return bool.TryParse(OverlayActionConfig.ReadRawString(key), out b) && b;
        }

        private static void Set(string key, string value)
        {
            OverlayActionConfig.WriteSetting(key, value);
        }

        private void OnSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "FontSize")
            {
                FontSize = Settings.Default.FontSize;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
