using System;
using System.Diagnostics;
using System.IO;

namespace HuntAndPeck
{
    /// <summary>
    /// Minimal phase timer that appends a wall-clock breakdown to a file, used to
    /// locate where the hotkey-to-buttons latency goes. The Stopwatch already in
    /// EnumHints is Debug-only (stripped from Release), so this works in Release
    /// builds. Log path: %TEMP%\hap-timing.log
    /// </summary>
    internal static class PerfLog
    {
        private static Stopwatch _sw;
        private static string _path;

        public static void Start()
        {
            _sw = Stopwatch.StartNew();
            _path = Path.Combine(Path.GetTempPath(), "hap-timing.log");
            File.AppendAllText(_path, "---- " + DateTimeOffset.Now.ToString("O") + " ----" + Environment.NewLine);
        }

        public static void Mark(string label)
        {
            if (_sw == null || _path == null)
            {
                return;
            }

            File.AppendAllText(_path, label + ": " + _sw.ElapsedMilliseconds + " ms" + Environment.NewLine);
        }
    }
}
