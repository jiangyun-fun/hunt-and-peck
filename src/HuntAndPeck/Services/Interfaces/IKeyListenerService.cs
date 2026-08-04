using System;
using System.Windows.Forms;
using HuntAndPeck.NativeMethods;

namespace HuntAndPeck.Services.Interfaces
{
    internal class HotKey
    {
        public KeyModifier Modifier { get; set; }
        public Keys Keys { get; set; }

        /// <summary>
        /// Id of the hot key registration
        /// </summary>
        public int RegistrationId { get; set; }
    }

    /// <summary>
    /// Service for listening to global keyboard shortcuts
    /// </summary>
    internal interface IKeyListenerService
    {
        event EventHandler OnHotKeyActivated;
        event EventHandler OnTaskbarHotKeyActivated;
        event EventHandler OnDebugHotKeyActivated;

        /// <summary>Quadrant hotkey (Ctrl+Shift+F1..F4): carries the quadrant index 0..3 (TL/TR/BL/BR).</summary>
        event Action<int> OnQuadrantHotKeyActivated;

        HotKey TaskbarHotKey { get; set; }
        HotKey HotKey { get; set; }
        HotKey DebugHotKey { get; set; }

        /// <summary>The four quadrant hotkeys (TL/TR/BL/BR); set once at startup to register.</summary>
        HotKey[] QuadrantHotKeys { set; }
    }
}
