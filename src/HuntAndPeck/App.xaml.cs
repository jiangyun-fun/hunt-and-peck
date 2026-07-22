using System;
using System.Windows;
using HuntAndPeck.ViewModels;
using System.Linq;
using HuntAndPeck.Services;
using HuntAndPeck.Views;
using HuntAndPeck.NativeMethods;

namespace HuntAndPeck
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private readonly SingleLaunchMutex _singleLaunchMutex = new SingleLaunchMutex();
        private readonly UiAutomationHintProviderService _hintProviderService = new UiAutomationHintProviderService();
        private readonly HintLabelService _hintLabelService = new HintLabelService();
        private KeyListenerService _keyListenerService;
        private OverlayView _currentOverlayView;
        private OverlayViewModel _currentVm;

        /// <summary>True while the hint overlay is showing (a 2nd hotkey press then toggles mode).</summary>
        private bool IsOverlayActive()
        {
            return _currentOverlayView != null;
        }

        /// <summary>
        /// Main hotkey while the overlay is up: resume if suspended, else toggle
        /// one-click &lt;-&gt; continuous.
        /// </summary>
        private void ToggleOverlayMode()
        {
            var vm = _currentVm;
            if (vm == null)
            {
                return;
            }
            if (vm.Suspended)
            {
                vm.Suspended = false;
            }
            else
            {
                vm.ToggleContinuous();
            }
        }

        private void ShowOverlay(OverlayViewModel vm)
        {
            // One overlay at a time. The overlay shows NON-activated (OverlayView.xaml
            // ShowActivated=False) so the hotkey does NOT steal foreground and dismiss
            // an open context menu; typed label chars are captured by a global
            // low-level hook (OverlayKeyboardHook) instead of WPF focus.
            if (_currentOverlayView != null)
            {
                return;
            }

            var view = new OverlayView
            {
                DataContext = vm
            };
            _currentOverlayView = view;
            _currentVm = vm;

            var hook = new OverlayKeyboardHook();
            bool closed = false;
            // Single idempotent close path: match success, Esc, or any mouse click
            // (the latter two via the hook) all funnel through here.
            Action close = () =>
            {
                if (closed) return;
                closed = true;
                hook.Disarm();
                view.Close();
            };

            vm.CloseOverlay = close;
            hook.Arm(vm, close);

            view.Closed += (s, e) =>
            {
                if (!closed)
                {
                    closed = true;
                    hook.Disarm();
                }
                _currentOverlayView = null;
                _currentVm = null;
            };

            view.Show();
        }

        private void ShowDebugOverlay(DebugOverlayViewModel vm)
        {
            var view = new DebugOverlayView
            {
                DataContext = vm
            };
            view.ShowDialog();
        }

        private void ShowOptions(OptionsViewModel vm)
        {
            var view = new OptionsView
            {
                DataContext = vm
            };
            view.ShowDialog();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            if (e.Args.Contains("/hint"))
            {
                // support headless mode
                var session = _hintProviderService.EnumHints();
                ShowOverlay(new OverlayViewModel(session, _hintLabelService));
            }
            else if (e.Args.Contains("/tray"))
            {
                // support headless tray mode
                var taskbarHWnd = User32.FindWindow("Shell_traywnd", "");
                var session = _hintProviderService.EnumHints(taskbarHWnd);
                ShowOverlay(new OverlayViewModel(session, _hintLabelService));
            }
            else
            {
                // Prevent multiple startup in non-headless mode
                if (_singleLaunchMutex.AlreadyRunning)
                {
                    Current.Shutdown();
                    return;
                }

                // Create this as late as possible as it has a window
                _keyListenerService = new KeyListenerService();

                var shellViewModel = new ShellViewModel(
                    ShowOverlay,
                    ShowDebugOverlay,
                    ShowOptions,
                    IsOverlayActive,
                    ToggleOverlayMode,
                    _hintLabelService,
                    _hintProviderService,
                    _hintProviderService,
                    _keyListenerService);

                var shellView = new ShellView
                {
                    DataContext = shellViewModel
                };
                shellView.Show();
            }
            base.OnStartup(e);
        }
    }
}
