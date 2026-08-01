using System;
using System.Runtime.InteropServices;

namespace HuntAndPeck.NativeMethods
{
    /// <summary>
    /// P/Invoke for raw input (WM_INPUT). Used to track physical Alt/Capslock held-state
    /// INDEPENDENTLY of the low-level keyboard hook chain, so an AutoHotkey that fully
    /// remaps/suppresses Capslock (e.g. "CapsLock::Send, _") -- and that re-installs its
    /// LL hook above ours on script reload -- cannot hide the held modifier from us. Raw
    /// input taps the keyboard driver directly and is delivered regardless of LL hooks.
    /// </summary>
    internal static class RawInput
    {
        internal const int WM_INPUT = 0x00FF;
        internal const uint RIM_TYPEKEYBOARD = 1;
        internal const uint RID_INPUT = 0x10000003;
        internal const uint RIDEV_INPUTSINK = 0x00000100; // receive input even when not foreground
        internal const ushort UsagePageGenericDesktop = 0x01;
        internal const ushort UsageKeyboard = 0x06;
        internal const ushort RI_KEY_BREAK = 0x01;        // otherwise MAKE (keydown)

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct RAWINPUT
        {
            public RAWINPUTHEADER header;
            // The Windows RAWINPUT data is a union; we only consume the keyboard member,
            // valid when header.dwType == RIM_TYPEKEYBOARD.
            public RAWKEYBOARD keyboard;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
            out RAWINPUT pData, ref uint pcbSize, uint cbSizeHeader);
    }
}
