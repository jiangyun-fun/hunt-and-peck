using System;
using System.IO;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// Appends overlay timing lines to %TEMP%\hap-timing.log so latency can be
    /// measured on the target machine. Best-effort; never throws.
    /// </summary>
    internal static class TimingLog
    {
        public static readonly string LogPath = Path.Combine(Path.GetTempPath(), "hap-timing.log");

        public static void Log(string message)
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
    }
}
