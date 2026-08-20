using System;
using System.Configuration;
using System.IO;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// Appends overlay timing lines to %TEMP%\hap-timing.log so latency can be
    /// measured on the target machine. Gated by the TimingLogEnabled appSetting
    /// (default false) so it is silent in normal use; set it to "true" to
    /// re-measure. Best-effort; never throws.
    /// </summary>
    internal static class TimingLog
    {
        public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "hap-timing.log");

        public static void Log(string message)
        {
            if (!IsEnabled())
            {
                return;
            }
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Best-effort logging; never break the app over a log write.
            }
        }

        /// <summary>
        /// Ungated variant for lines that must exist EXACTLY when things go wrong on
        /// the box: hotkey-registration outcomes (once per chord at startup) and
        /// quadrant-hotkey presses (1-2 lines each). "Ctrl+Shift+F1 does nothing"
        /// cannot be triaged through the gated log -- the user only enables
        /// TimingLogEnabled to measure latency, never while debugging a dead hotkey --
        /// so these bypass the gate. Volume is negligible; best-effort, never throws.
        /// </summary>
        public static void LogAlways(string message)
        {
            try
            {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch (Exception)
            {
                // Best-effort logging; never break the app over a log write.
            }
        }

        private static bool IsEnabled()
        {
            try
            {
                OverlayActionConfig.EnsureFresh();
                bool enabled;
                return bool.TryParse(ConfigurationManager.AppSettings["TimingLogEnabled"], out enabled) && enabled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
