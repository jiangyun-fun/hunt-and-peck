using System;
using System.Runtime.InteropServices;

namespace HuntAndPeck.NativeMethods
{
    public static class User32
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowRect(IntPtr hWnd, ref RECT rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PhysicalToLogicalPoint(IntPtr hWnd, out POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        // ---- Low-level hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) ----
        // Used by OverlayKeyboardHook so the overlay can capture typed label
        // characters WITHOUT taking foreground (which would dismiss an open
        // context menu). See docs in Services/OverlayKeyboardHook.cs.
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        // Raise z-order above everything (incl. an open context menu) WITHOUT
        // stealing activation (SWP_NOACTIVATE) -> the menu stays open.
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // Hook ids
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int HC_ACTION = 0;

        // MSLLHOOKSTRUCT.flags: the event was injected (mouse_event/SendInput), not
        // from real hardware. Used to ignore our OWN synthesized clicks so they don't
        // dismiss the overlay in continuous mode.
        public const uint LLMHF_INJECTED = 0x0001;

        // KBDLLHOOKSTRUCT.flags: the key is an extended key (dedicated arrow/Nav cluster,
        // right Ctrl/Alt, NumpadDivide, NumpadEnter, etc.). NOT set for the numeric
        // keypad's digit/Nav keys (NumLock off: numpad 8/6/4/2 reuse VK_UP/RIGHT/LEFT/
        // DOWN without this flag). Used to tell real arrows from numpad arrows so a
        // numpad-mouse tool keeps working with the overlay up.
        public const uint LLKHF_EXTENDED = 0x0001;

        // Window messages delivered to the hook wParam
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_XBUTTONDOWN = 0x020B;

        // SetWindowPos
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOACTIVATE = 0x0010;

        // Virtual key codes
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;       // ALT (generic -- what GetAsyncKeyState reports)
        public const int VK_LMENU = 0xA4;      // Left ALT (what the LL hook delivers Alt as)
        public const int VK_RMENU = 0xA5;      // Right ALT
        public const int VK_CAPITAL = 0x14;    // Capslock (held => suspend overlay capture)
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_SPACE = 0x20;
        public const int VK_TAB = 0x09;
        public const int VK_LEFT = 0x25;
        public const int VK_UP = 0x26;
        public const int VK_RIGHT = 0x27;
        public const int VK_DOWN = 0x28;
        public const int VK_0 = 0x30;
        public const int VK_9 = 0x39;
        public const int VK_A = 0x41;
        public const int VK_Z = 0x5A;
        public const int VK_C = 0x43;  // Ctrl+C (regression guard: Ctrl+letter passthrough)
        public const int VK_V = 0x56;  // Ctrl+V (regression guard: Ctrl+letter passthrough)
        // Vim-style label-pan keys: Shift+hjkl = large step, Ctrl+Shift+hjkl = small.
        public const int VK_H = 0x48;
        public const int VK_J = 0x4A;
        public const int VK_K = 0x4B;
        public const int VK_L = 0x4C;
        public const int VK_OEM_1 = 0xBA;  // ; : (semicolon) -- cycle grid layout
        public const int VK_OEM_3 = 0xC0;  // ` ~ (backtick) -- toggle label opacity
        public const int VK_OEM_5 = 0xDC;  // \ | (backslash) -- enter suspend
    }
}
