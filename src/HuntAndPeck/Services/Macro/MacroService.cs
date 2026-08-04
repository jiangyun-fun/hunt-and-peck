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
                        string err = WindowFinder.FocusByTitle(s.Title, s.Match);
                        if (err != null)
                        {
                            throw new InvalidOperationException(err);
                        }
                        _targetWindow = User32.GetForegroundWindow();
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
            User32.mouse_event(User32.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            User32.mouse_event(User32.MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }

        // Fail loudly: a step that threw (e.g. ambiguous window, unimplemented overlay
        // step) surfaces a MessageBox on the UI thread so the user sees why it stopped.
        private static void Report(MacroDef macro, MacroStep step, Exception ex)
        {
            string msg = "Macro '" + (macro.Name ?? macro.Hotkey) + "' aborted at step '"
                         + (step.Type ?? "?") + "':\n" + ex.Message;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                MessageBox.Show(msg, "hap macro", MessageBoxButton.OK, MessageBoxImage.Warning)));
        }
    }
}
