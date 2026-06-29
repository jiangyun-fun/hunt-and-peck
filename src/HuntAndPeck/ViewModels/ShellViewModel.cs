using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using HuntAndPeck.NativeMethods;
using HuntAndPeck.Services;
using HuntAndPeck.Services.Interfaces;
using Application = System.Windows.Application;

namespace HuntAndPeck.ViewModels
{
    internal class ShellViewModel
    {
        private readonly Action<OverlayViewModel> _showOverlay;
        private readonly Action<DebugOverlayViewModel> _showDebugOverlay;
        private readonly Action<OptionsViewModel> _showOptions;
        private readonly IHintLabelService _hintLabelService;
        private readonly IHintProviderService _hintProviderService;
        private readonly IDebugHintProviderService _debugHintProviderService;

        public ShellViewModel(
            Action<OverlayViewModel> showOverlay,
            Action<DebugOverlayViewModel> showDebugOverlay,
            Action<OptionsViewModel> showOptions,
            IHintLabelService hintLabelService,
            IHintProviderService hintProviderService,
            IDebugHintProviderService debugHintProviderService,
            IKeyListenerService keyListener)
        {
            _showOverlay = showOverlay;
            _showDebugOverlay = showDebugOverlay;
            _showOptions = showOptions;
            _hintLabelService = hintLabelService;
            var keyListener1 = keyListener;
            _hintProviderService = hintProviderService;
            _debugHintProviderService = debugHintProviderService;

            // Main overlay hotkey. Read once at startup from hap.exe.config
            // (HotkeyKey / HotkeyModifier); restart to apply a change, since the
            // global hotkey is registered once. Default: Ctrl+Shift+Alt+F.
            keyListener1.HotKey = new HotKey
            {
                Keys = OverlayActionConfig.ReadHotkeyKey(Keys.F),
                Modifier = OverlayActionConfig.ReadHotkeyModifier(
                    KeyModifier.Control | KeyModifier.Alt | KeyModifier.Shift)
            };

            keyListener1.TaskbarHotKey = new HotKey
            {
                Keys = Keys.OemSemicolon,
                Modifier = KeyModifier.Control
            };

#if DEBUG
            keyListener1.DebugHotKey = new HotKey
            {
                Keys = Keys.OemSemicolon,
                Modifier = KeyModifier.Alt | KeyModifier.Shift
            };
#endif

            keyListener1.OnHotKeyActivated += _keyListener_OnHotKeyActivated;
            keyListener1.OnTaskbarHotKeyActivated += _keyListener_OnTaskbarHotKeyActivated;
            keyListener1.OnDebugHotKeyActivated += _keyListener_OnDebugHotKeyActivated;

            ShowOptionsCommand = new DelegateCommand(ShowOptions);
            ExitCommand = new DelegateCommand(Exit);
        }

        public DelegateCommand ShowOptionsCommand { get; }
        public DelegateCommand ExitCommand { get; }

        private async void _keyListener_OnHotKeyActivated(object sender, EventArgs e)
        {
            // Capture the foreground window on the UI thread, then enumerate off-thread.
            // The per-window cache (in the service) makes repeat presses on the same
            // window instant; the first press on a large tree (e.g. Chromium apps) still
            // takes seconds but runs off-thread so the UI does not freeze.
            var hWnd = User32.GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return;
            }

            var session = await Task.Run(() => _hintProviderService.EnumHints(hWnd));
            if (session != null)
            {
                var vm = new OverlayViewModel(session, _hintLabelService);
                _showOverlay(vm);
            }
        }

        private async void _keyListener_OnTaskbarHotKeyActivated(object sender, EventArgs e)
        {
            var taskbarHWnd = User32.FindWindow("Shell_traywnd", "");
            var session = await Task.Run(() => _hintProviderService.EnumHints(taskbarHWnd));
            if (session != null)
            {
                var vm = new OverlayViewModel(session, _hintLabelService);
                _showOverlay(vm);
            }
        }

        private void _keyListener_OnDebugHotKeyActivated(object sender, EventArgs e)
        {
            var session = _debugHintProviderService.EnumDebugHints();
            if (session != null)
            {
                var vm = new DebugOverlayViewModel(session);
                _showDebugOverlay(vm);
            }
        }

        public void Exit()
        {
            Application.Current.Shutdown();
        }

        public void ShowOptions()
        {
            var vm = new OptionsViewModel();
            _showOptions(vm);
        }
    }
}
