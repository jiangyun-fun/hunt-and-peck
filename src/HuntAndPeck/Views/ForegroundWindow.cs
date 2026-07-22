using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HuntAndPeck.NativeMethods;

namespace HuntAndPeck.Views
{
    /// <summary>
    /// Window that is always foreground, and closes when it's not. Deriving views
    /// can opt out of either behavior: the hint <see cref="OverlayView"/> shows
    /// non-activating (so it does not steal foreground from / dismiss an open
    /// context menu) and reads input via a global keyboard hook instead, so it
    /// overrides both properties to false.
    /// </summary>
    public class ForegroundWindow : Window
    {
        private bool _closing;
        private bool _initialized;

        /// <summary>
        /// When true (default), the window force-foregrounds itself on first render
        /// so it receives keyboard focus. OverlayView overrides this to false because
        /// stealing foreground dismisses any open context menu.
        /// </summary>
        protected virtual bool ForceForegroundOnRender => true;

        /// <summary>
        /// When true (default), the window closes itself when it loses activation
        /// (click elsewhere, alt-tab). OverlayView overrides this to false: it shows
        /// non-activated, so dismissal is driven by the keyboard/mouse hook instead.
        /// </summary>
        protected virtual bool CloseOnDeactivate => true;

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (!_initialized)
            {
                if (ForceForegroundOnRender)
                {
                    // Always want this on top. SetForegroundWindow has a few conditions:
                    // https://msdn.microsoft.com/en-us/library/ms633539(VS.85).aspx
                    if (!User32.SetForegroundWindow(new WindowInteropHelper(this).Handle))
                    {
                        ForceForeground();
                    }
                }
                _initialized = true;
            }
            base.OnRender(drawingContext);
        }

        protected override void OnDeactivated(EventArgs e)
        {
            // We could have lost focus because we're already closing, make sure this doesn't call close twice
            if (CloseOnDeactivate && _initialized && !_closing)
            {
                Close();
            }
            base.OnDeactivated(e);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            _closing = true;
            base.OnClosing(e);
        }

        /// <summary>
        /// Forces the window to the foreground by attaching to the foreground window thread
        /// </summary>
        private void ForceForeground()
        {
            // This is required as there's a few restrictions on when this can be called
            // Per https://msdn.microsoft.com/en-us/library/windows/desktop/ms633539%28v=vs.85%29.aspx

            var targetThread = User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), IntPtr.Zero);
            var appThread = Kernel32.GetCurrentThreadId();
            var attached = false;

            try
            {
                if (targetThread == appThread)
                {
                    // already attached
                    return;
                }

                attached = User32.AttachThreadInput(targetThread, appThread, true);

                if (!attached)
                {
                    // hmm
                    Close();
                    return;
                }

                var ourHandle = new WindowInteropHelper(this).Handle;

                // force us to the forground
                User32.BringWindowToTop(ourHandle);
                User32.SetFocus(ourHandle);
            }
            finally
            {
                if (attached)
                {
                    // unattach
                    User32.AttachThreadInput(targetThread, appThread, false);
                }
            }
        }
    }
}
