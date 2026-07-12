using System;
using System.Drawing;
using System.Windows;
using HuntAndPeck.ViewModels;
using WinForms = System.Windows.Forms;

namespace HuntAndPeck.Views
{
    /// <summary>
    /// Hidden window hosting the tray icon. The tray uses a WinForms NotifyIcon with a
    /// ContextMenuStrip (not a WPF ContextMenu): WPF context menus opened from the shell
    /// tray never receive keyboard focus, so arrow keys and mnemonics did not work. The
    /// WinForms ContextMenuStrip is a native-menu wrapper, so on both right-click and the
    /// shell's Shift+F10 context-menu request it is fully keyboard-navigable (arrows,
    /// mnemonics O/E, Enter).
    /// </summary>
    public partial class ShellView : Window
    {
        private WinForms.NotifyIcon _notifyIcon;

        public ShellView()
        {
            InitializeComponent();
            Loaded += ShellView_Loaded;
        }

        private void ShellView_Loaded(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ShellViewModel;

            var strip = new WinForms.ContextMenuStrip();
            strip.Items.Add("&Options", null, (s, a) => vm.ShowOptionsCommand.Execute(null));
            strip.Items.Add(new WinForms.ToolStripSeparator());
            strip.Items.Add("&Exit", null, (s, a) => vm.ExitCommand.Execute(null));

            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = LoadEmbeddedIcon(),
                Visible = true,
                Text = "Hunt and Peck",
                ContextMenuStrip = strip,
            };
        }

        /// <summary>Loads the embedded Resources/originalbird.ico as a System.Drawing.Icon.</summary>
        private static Icon LoadEmbeddedIcon()
        {
            var uri = new Uri("pack://application:,,,/Resources/originalbird.ico", UriKind.Absolute);
            using (var stream = Application.GetResourceStream(uri).Stream)
            {
                return new Icon(stream);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            base.OnClosed(e);
        }
    }
}
