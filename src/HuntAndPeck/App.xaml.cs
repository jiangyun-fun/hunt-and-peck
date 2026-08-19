using System;
using System.Collections.Generic;
using System.Windows;
using HuntAndPeck.ViewModels;
using System.Linq;
using HuntAndPeck.Services;
using HuntAndPeck.Services.Macro;
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
        private readonly MacroService _macroService = new MacroService();
        private KeyListenerService _keyListenerService;
        private OverlayView _currentOverlayView;
        private OverlayViewModel _currentVm;
        private readonly List<QuadrantGuideWindow> _quadrantGuideWindows = new List<QuadrantGuideWindow>();

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

        /// <summary>
        /// Closes the current overlay (if any) via its own idempotent close path. Used by a
        /// quadrant hotkey to replace whatever overlay is open with the requested quadrant.
        /// </summary>
        private void CloseCurrentOverlay()
        {
            _currentVm?.CloseOverlay?.Invoke();
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
            vm.CaptureRegion = rect => CaptureRegionToClipboard(view, rect);
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

        /// <summary>
        /// Captures a screen-pixel rectangle to the clipboard: hides the overlay (so its
        /// labels/badges do not appear in the shot), flushes the render queue + a short wait
        /// for the compositor to drop the overlay frame, CopyFromScreen, flattens to 24bpp
        /// (avoids the WPF Clipboard.SetImage alpha-black gotcha), places it on the
        /// clipboard, and restores the overlay. Runs on the UI (STA) thread via the hook's
        /// BeginInvoke. The ~40ms wait is the one piece not verifiable off-Windows -- tune
        /// on the box if labels bleed into the shot.
        /// </summary>
        private static void CaptureRegionToClipboard(OverlayView view, Rect rect)
        {
            int x = (int)Math.Round(rect.X);
            int y = (int)Math.Round(rect.Y);
            int w = (int)Math.Round(rect.Width);
            int h = (int)Math.Round(rect.Height);
            if (w <= 0 || h <= 0)
            {
                return;
            }

            double prevOpacity = view.Opacity;
            view.Opacity = 0;
            view.UpdateLayout();
            // Flush the render queue so the opacity change is composited before capture...
            view.Dispatcher.Invoke(new Action(() => { }), System.Windows.Threading.DispatcherPriority.Render);
            // ...then give the compositor a moment to actually drop the overlay frame.
            System.Threading.Thread.Sleep(40);
            try
            {
                using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                    }
                    // Flatten to 24bpp (no alpha) to dodge the Clipboard.SetImage alpha-black
                    // gotcha. Nested inside the bmp scope so bmp is still alive for DrawImage.
                    using (var flat = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        using (var gf = System.Drawing.Graphics.FromImage(flat))
                        {
                            gf.DrawImage(bmp, 0, 0, w, h);
                        }
                        IntPtr hb = flat.GetHbitmap();
                        try
                        {
                            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                                hb, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                            System.Windows.Clipboard.SetImage(src);
                        }
                        finally
                        {
                            DeleteObject(hb);
                        }
                    }
                }
            }
            finally
            {
                view.Opacity = prevOpacity;
            }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

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

        private void ShowMacroPicker()
        {
            var file = MacroStore.Load();
            var view = new MacroPickerView(file);
            view.Closed += (s, e) =>
            {
                var macro = view.Result;
                if (macro != null)
                {
                    // Run on a background thread; the first step is usually focusWindow,
                    // which moves foreground off the (closing) picker before any keys land.
                    _ = _macroService.RunAsync(macro);
                }
            };
            view.Show();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Install an inert, app-lifetime Alt/Capslock tracker (above AutoHotkey) so the
            // overlay knows a modifier held BEFORE it opened (e.g. holding Capslock and tapping
            // quadrant hotkeys). Must run on the UI thread (this one), which owns the message
            // loop that delivers LL-hook callbacks.
            OverlayKeyboardHook.EnsurePersistentTracker();

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
                    CloseCurrentOverlay,
                    ShowMacroPicker,
                    _hintLabelService,
                    _hintProviderService,
                    _hintProviderService,
                    _keyListenerService);

                var shellView = new ShellView
                {
                    DataContext = shellViewModel
                };
                shellView.Show();

                // Persistent quadrant guide (QuadrantGuideEnabled/Labels, read once
                // here like the hotkeys -- restart to apply): a faint always-on-top
                // click-through cross + quadrant letters on every monitor, marking
                // the Ctrl+Shift+F1..F4 regions during normal use. The hint overlay
                // re-asserts topmost and paints above it while open.
                _quadrantGuideWindows.AddRange(QuadrantGuideWindow.CreateForAllScreens());
            }
            base.OnStartup(e);
        }
    }
}
