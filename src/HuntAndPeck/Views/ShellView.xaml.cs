using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HuntAndPeck.Views
{
    /// <summary>
    /// Interaction logic for ShellView.xaml — hosts the tray TaskbarIcon and its menu.
    /// </summary>
    public partial class ShellView : Window
    {
        public ShellView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// The tray context menu opens from the shell tray, not a WPF window, so it does
        /// not receive keyboard focus by default and arrow keys would not move between
        /// items. Move keyboard focus to the first item on open so Up/Down navigate,
        /// Enter activates, and the mnemonics (O for Options, E for Exit) work.
        /// </summary>
        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            var menu = sender as ContextMenu;
            if (menu == null)
            {
                return;
            }
            var first = menu.Items.OfType<MenuItem>().FirstOrDefault();
            if (first != null)
            {
                Keyboard.Focus(first);
            }
        }
    }
}
