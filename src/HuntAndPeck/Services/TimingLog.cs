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
