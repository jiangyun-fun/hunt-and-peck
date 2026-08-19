using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using HuntAndPeck.NativeMethods;

namespace HuntAndPeck.Services.Macro
{
    /// <summary>
    /// Runs a macro's steps in order on a background thread (waits use Thread.Sleep;
    /// SendInput works from any thread). B1 implements the non-overlay steps;
    /// openOverlay/overlayType/nudge throw until B2 wires the overlay-ready and
    /// click-fired signals. Any step failure aborts the macro and surfaces a message.
    /// </summary>
    internal sealed class MacroService
    {
        // HWND set by focusWindow; clickRel resolves its point against this window's
        // rect (falls back to the foreground window if no focusWindow preceded it).
        private IntPtr _targetWindow = IntPtr.Zero;

        /// <summary>Runs the macro on a background thread. Awaitable; fire-and-forget if not awaited.</summary>
        public Task RunAsync(MacroDef macro)
        {
            if (macro == null)
            {
                throw new ArgumentNullException("macro");
            }
            return Task.Run(() => Run(macro));
        }

        private void Run(MacroDef macro)
        {
            try
            {
                _targetWindow = IntPtr.Zero;
                foreach (var step in macro.Steps ?? new List<MacroStep>())
                {
                    try
                    {
                        RunStep(step);
                    }
                    catch (Exception ex)
                    {
                        Report(macro, step, ex);
                        return; // abort on first failure (fail loudly)
                    }
                }
            }
            catch (Exception ex)
            {
                // Last-resort: never let the background Task fault -- an unobserved task
                // exception could otherwise take down the process. Report and swallow.
                Report(macro, null, ex);
            }
        }

        private void RunStep(MacroStep s)
        {
            switch ((s.Type ?? "").ToLowerInvariant())
            {
                case "send":
                    {
                        var mods = (s.Mods ?? "").Split(',');
                        InputSynthesis.SendChord(mods, InputSynthesis.ParseKey(s.Key));
                        break;
                    }
                case "wait":
                    if (s.Ms > 0)
                    {
                        Thread.Sleep(s.Ms);
                    }
                    break;
                case "focuswindow":
                    {
                        // SetForegroundWindow needs the calling thread to own a message loop
                        // (foreground privilege), so dispatch the focus onto the UI thread.
                        string err = null;
                        Application.Current.Dispatcher.Invoke(new Action(() =>
                        {
                            err = WindowFinder.FocusByTitle(s.Title, s.Match);
                            _targetWindow = User32.GetForegroundWindow();
                        }));
                        if (err != null)
                        {
                            throw new InvalidOperationException(err);
                        }
                        break;
                    }
                case "clickabs":
                    ClickPoint(s.X, s.Y);
                    break;
                case "clickrel":
                    {
                        var hwnd = _targetWindow != IntPtr.Zero ? _targetWindow : User32.GetForegroundWindow();
                        var rect = new RECT();
                        User32.GetWindowRect(hwnd, ref rect);
                        ClickPoint(rect.left + s.Dx, rect.top + s.Dy);
                        break;
                    }
                case "rawreplay":
                    InputSynthesis.Replay(s.Events);
                    break;
                case "openoverlay":
                case "overlaytype":
                case "nudge":
                    throw new NotImplementedException(
                        "macro step '" + s.Type + "' is not implemented yet (arrives with overlay integration).");
                default:
                    throw new ArgumentException("unknown macro step type '" + s.Type + "'");
            }
        }

        private static void ClickPoint(int x, int y)
        {
            User32.SetCursorPos(x, y);
            // SendInput (not mouse_event) so a UIPI block is detectable: 0 injected
            // events = the target window is more elevated than hap.
            if (InputSynthesis.SendMouseEvent(User32.MOUSEEVENTF_LEFTDOWN) == 0
                || InputSynthesis.SendMouseEvent(User32.MOUSEEVENTF_LEFTUP) == 0)
            {
                TimingLog.Log("macro click SendInput blocked at " + x + "," + y
                    + " (0 events injected) -- elevated target? start hap as administrator");
            }
        }

        // Fail loudly: a step that threw (e.g. ambiguous window, unimplemented overlay
        // step) surfaces a MessageBox on the UI thread so the user sees why it stopped.
        private static void Report(MacroDef macro, MacroStep step, Exception ex)
        {
            string stepName = step != null ? (step.Type ?? "?") : "?";
            string msg = "Macro '" + (macro.Name ?? macro.Hotkey) + "' aborted at step '"
                         + stepName + "':\n" + ex.Message;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                MessageBox.Show(msg, "hap macro", MessageBoxButton.OK, MessageBoxImage.Warning)));
        }
    }
}
