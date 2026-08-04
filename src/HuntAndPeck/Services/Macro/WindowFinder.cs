using System;
using System.Collections.Generic;
using System.Text;
using HuntAndPeck.NativeMethods;

namespace HuntAndPeck.Services.Macro
{
    /// <summary>
    /// Finds a top-level window by title for the macro focusWindow step. Match is
    /// EXACT by default (the user's safety rule): 0 matches or &gt;1 matches abort the
    /// macro with a clear message, so a stale title never clicks the wrong window.
    /// Set the step's Match to "contains" to match a substring (still aborts on &gt;1).
    /// </summary>
    public static class WindowFinder
    {
        /// <summary>
        /// Focuses a window by title. Returns null on success (focused) or an error
        /// message describing why it aborted (0/&gt;1 matches).
        /// </summary>
        public static string FocusByTitle(string title, string match)
        {
            var hWnds = FindByTitle(title, match);
            if (hWnds.Count == 0)
            {
                return "no window matches title '" + title + "' (" + ModeName(match) + ") -- macro aborted";
            }
            if (hWnds.Count > 1)
            {
                return hWnds.Count + " windows match title '" + title + "' (" + ModeName(match)
                       + ") -- macro aborted (ambiguous)";
            }
            SetForeground(hWnds[0]);
            return null; // success
        }

        /// <summary>Enumerates visible top-level windows whose title matches.</summary>
        public static List<IntPtr> FindByTitle(string title, string match)
        {
            var hits = new List<IntPtr>();
            User32.EnumWindows((h, lp) =>
            {
                if (User32.IsWindowVisible(h) && MatchesTitle(GetText(h), title, match))
                {
                    hits.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            return hits;
        }

        // ---- pure helper (unit-tested) ----

        /// <summary>Title-match predicate: exact (default) or contains, case-insensitive.</summary>
        public static bool MatchesTitle(string windowTitle, string target, string match)
        {
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }
            if (ModeIsContains(match))
            {
                return windowTitle != null
                       && windowTitle.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return string.Equals(windowTitle, target, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetText(IntPtr hWnd)
        {
            int len = User32.GetWindowTextLength(hWnd);
            if (len <= 0)
            {
                return null;
            }
            var sb = new StringBuilder(len + 1);
            User32.GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // SetForegroundWindow refuses when the caller is not already foreground;
        // briefly attach input threads to the target to satisfy it (standard workaround).
        private static void SetForeground(IntPtr hWnd)
        {
            uint fgThread = User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), IntPtr.Zero);
            uint appThread = User32.GetWindowThreadProcessId(hWnd, IntPtr.Zero);
            if (fgThread != appThread)
            {
                User32.AttachThreadInput(fgThread, appThread, true);
            }
            try
            {
                User32.SetForegroundWindow(hWnd);
            }
            finally
            {
                if (fgThread != appThread)
                {
                    User32.AttachThreadInput(fgThread, appThread, false);
                }
            }
        }

        private static bool ModeIsContains(string match)
        {
            return string.Equals(match ?? "", "contains", StringComparison.OrdinalIgnoreCase);
        }

        private static string ModeName(string match)
        {
            return ModeIsContains(match) ? "contains" : "exact";
        }
    }
}
