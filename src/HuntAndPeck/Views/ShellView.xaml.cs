using System;
using System.Windows;
using System.Windows.Interop;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.ViewModels;

namespace HuntAndPeck.Views
{
    /// <summary>
    /// Interaction logic for ShellView.xaml — hosts the tray TaskbarIcon. The tray
    /// menu is a native Win32 popup (not a WPF ContextMenu): WPF menus opened from the
    /// shell tray never receive keyboard focus, so arrows/mnemonics don't work. A native
    /// menu runs its own modal loop and is fully keyboard-navigable (arrows, mnemonics,
    /// Enter), matching how AutoHotkey's tray menu behaves.
    /// </summary>
    public partial class ShellView : Window
    {
        private const uint CmdOptions = 1;
        private const uint CmdExit = 2;

        public ShellView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Shows a native popup menu (Options / Exit) at the cursor and dispatches the
        /// selection. Invoked on both right-click and the shell's keyboard context-menu
        /// request (Shift+F10). Mnemonics: O = Options, E = Exit.
        /// </summary>
        private void TaskbarIcon_ContextMenuOpen(object sender, RoutedEventArgs e)
        {
            var vm = DataContext as ShellViewModel;
            var hwnd = new WindowInteropHelper(this).Handle;

            var hMenu = User32.CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                User32.AppendMenu(hMenu, User32.MF_STRING, CmdOptions, "&Options");
                User32.AppendMenu(hMenu, User32.MF_SEPARATOR, 0, null);
                User32.AppendMenu(hMenu, User32.MF_STRING, CmdExit, "&Exit");

                User32.GetCursorPos(out var pt);

                // MSDN KB135788: SetForegroundWindow before TrackPopupMenuEx and
                // PostMessage(WM_NULL) after, so the menu dismisses on an outside click.
                // Keyboard navigation works regardless (the menu runs its own loop).
                User32.SetForegroundWindow(hwnd);
                int selected = User32.TrackPopupMenuEx(hMenu,
                    User32.TPM_RETURNCMD | User32.TPM_NONOTIFY | User32.TPM_RIGHTALIGN | User32.TPM_BOTTOMALIGN,
                    pt.X, pt.Y, hwnd, IntPtr.Zero);
                User32.PostMessage(hwnd, User32.WM_NULL, IntPtr.Zero, IntPtr.Zero);

                if (selected == CmdOptions && vm != null)
                {
                    vm.ShowOptionsCommand.Execute(null);
                }
                else if (selected == CmdExit && vm != null)
                {
                    vm.ExitCommand.Execute(null);
                }
            }
            finally
            {
                User32.DestroyMenu(hMenu);
            }
        }
    }
}
