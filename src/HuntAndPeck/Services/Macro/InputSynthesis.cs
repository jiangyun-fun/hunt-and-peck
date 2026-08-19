using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using HuntAndPeck.NativeMethods;

namespace HuntAndPeck.Services.Macro
{
    /// <summary>
    /// Synthesizes keyboard input for macro steps via SendInput. The chord is sent
    /// as ONE atomic SendInput call (modifiers down, key tap, modifiers up). The
    /// parsing helpers are pure and unit-tested.
    /// </summary>
    public static class InputSynthesis
    {
        /// <summary>
        /// Sends a modifier chord (e.g. Ctrl+Shift+Q): presses the modifiers, taps the
        /// key, releases the modifiers in reverse order -- all in one atomic SendInput.
        /// </summary>
        public static void SendChord(IList<string> mods, int vk)
        {
            if (vk <= 0)
            {
                throw new ArgumentException("vk must be a positive key code", "vk");
            }
            var modVks = ParseModifiers(mods);
            var inputs = new List<User32.INPUT>();
            foreach (var mvk in modVks)
            {
                inputs.Add(KeyInput(mvk, false));
            }
            inputs.Add(KeyInput(vk, false));
            inputs.Add(KeyInput(vk, true));
            for (int i = modVks.Count - 1; i >= 0; i--)
            {
                inputs.Add(KeyInput(modVks[i], true));
            }
            Send(inputs);
        }

        /// <summary>Replays a recorded key-event sequence (each event down/up, then its delay).</summary>
        public static void Replay(IEnumerable<RawEvent> events)
        {
            if (events == null)
            {
                return;
            }
            foreach (var ev in events)
            {
                Send(new[] { KeyInput(ev.Vk, !ev.Down) });
                if (ev.Ms > 0)
                {
                    Thread.Sleep(ev.Ms);
                }
            }
        }

        // ---- parsing (pure, unit-tested) ----

        /// <summary>Parses a System.Windows.Forms.Keys name ("Q","F1","Oemcomma") to its vk code.</summary>
        public static int ParseKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("missing Key in a send step", "key");
            }
            Keys k;
            // Single key names only -- Trim keeps "Q ", TryParse is case-insensitive.
            if (!Enum.TryParse(key.Trim(), true, out k))
            {
                throw new ArgumentException(
                    "unknown Key '" + key + "' (use a System.Windows.Forms.Keys name like Q, N, F1)", "key");
            }
            return (int)k;
        }

        /// <summary>Parses "Ctrl,Shift" into vk codes (Ctrl/Shift/Alt/Win).</summary>
        public static IList<int> ParseModifiers(IList<string> mods)
        {
            var list = new List<int>();
            if (mods == null)
            {
                return list;
            }
            foreach (var raw in mods)
            {
                var m = (raw ?? "").Trim();
                if (m.Length == 0)
                {
                    continue;
                }
                switch (m.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control": list.Add(User32.VK_CONTROL); break;
                    case "shift":   list.Add(User32.VK_SHIFT); break;
                    case "alt":
                    case "menu":    list.Add(User32.VK_MENU); break;
                    case "win":
                    case "windows": list.Add(User32.VK_LWIN); break;
                    default:
                        throw new ArgumentException(
                            "unknown modifier '" + m + "' (expected Ctrl/Shift/Alt/Win)", "mods");
                }
            }
            return list;
        }

        // ---- internals ----

        private static User32.INPUT KeyInput(int vk, bool up)
        {
            return new User32.INPUT
            {
                type = User32.INPUT_KEYBOARD,
                u = new User32.INPUTUNION
                {
                    ki = new User32.KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = 0,
                        dwFlags = up ? User32.KEYEVENTF_KEYUP : 0,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private static void Send(IList<User32.INPUT> inputs)
        {
            if (inputs == null || inputs.Count == 0)
            {
                return;
            }
            var arr = new User32.INPUT[inputs.Count];
            for (int i = 0; i < inputs.Count; i++)
            {
                arr[i] = inputs[i];
            }
            User32.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(User32.INPUT)));
        }

        /// <summary>
        /// Injects ONE mouse event (e.g. <see cref="User32.MOUSEEVENTF_LEFTDOWN"/>) via
        /// SendInput and returns the injected event count. A return of 0 means the call
        /// was BLOCKED by UIPI -- the foreground window's integrity level is higher than
        /// hap's (an elevated app while hap runs unelevated); callers surface that.
        /// mouse_event cannot report this (void return), which is why clicks synthesize
        /// through here now.
        /// </summary>
        public static uint SendMouseEvent(uint flags)
        {
            var arr = new[]
            {
                new User32.INPUT
                {
                    type = User32.INPUT_MOUSE,
                    u = new User32.INPUTUNION
                    {
                        mi = new User32.MOUSEINPUT
                        {
                            dx = 0,
                            dy = 0,
                            mouseData = 0,
                            dwFlags = flags,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                }
            };
            return User32.SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(User32.INPUT)));
        }
    }
}
