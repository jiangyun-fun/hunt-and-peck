using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace HuntAndPeck.NativeMethods
{
    public static class Kernel32
    {
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // Module handle of the calling process; passed as hMod to SetWindowsHookEx
        // for WH_KEYBOARD_LL / WH_MOUSE_LL (required even though LL hooks ignore it).
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
