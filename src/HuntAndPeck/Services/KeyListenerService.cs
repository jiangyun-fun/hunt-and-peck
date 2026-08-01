using HuntAndPeck.NativeMethods;
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HuntAndPeck.Services.Interfaces;

namespace HuntAndPeck.Services
{
    internal class KeyListenerService : Form, IKeyListenerService, IDisposable
    {
        public event EventHandler OnHotKeyActivated;
        public event EventHandler OnTaskbarHotKeyActivated;
        public event EventHandler OnDebugHotKeyActivated;
        public event EventHandler OnOneShotHotKeyActivated;

        /// <summary>Quadrant hotkey (Ctrl+Shift+F1..F4): carries the quadrant index 0..3 (TL/TR/BL/BR).</summary>
        public event Action<int> OnQuadrantHotKeyActivated;

        /// <summary>
        /// Global counter for assigning ids to identiy the hot key registration
        /// </summary>
        private int _hotkeyIdCounter = 0;

        private HotKey _hotKey;
        private HotKey _taskbarHotKey;
        private HotKey _debugHotKey;
        private HotKey _oneShotHotKey;
        private HotKey[] _quadrantHotKeys;

        /// <summary>
        /// Re-registers the current hotkey, unregistering any previous key
        /// </summary>
        private void ReRegisterHotKey(HotKey hotKey)
        {
            // Already registered, have to unregister first
            if (hotKey.RegistrationId > 0)
            {
                User32.UnregisterHotKey(Handle, hotKey.RegistrationId);
            }

            hotKey.RegistrationId = _hotkeyIdCounter++;
            User32.RegisterHotKey(Handle, hotKey.RegistrationId, (uint)hotKey.Modifier, (uint)hotKey.Keys);
        }

        /// <summary>
        /// Gets/sets the current hotkey
        /// </summary>
        /// <remarks>Changing this will cause the current hotkey to be unregistered</remarks>
        public HotKey HotKey
        {
            get
            {
                return _hotKey;
            }
            set
            {
                _hotKey = value;
                ReRegisterHotKey(_hotKey);
            }
        }

        /// <summary>
        /// Gets/sets the current task bar hotkey
        /// </summary>
        /// <remarks>Changing this will cause the current hotkey to be unregistered</remarks>
        public HotKey TaskbarHotKey
        {
            get
            {
                return _taskbarHotKey;
            }
            set
            {
                _taskbarHotKey = value;
                ReRegisterHotKey(_taskbarHotKey);
            }
        }

        public HotKey DebugHotKey
        {
            get
            {
                return _debugHotKey;
            }
            set
            {
                _debugHotKey = value;
                ReRegisterHotKey(_debugHotKey);
            }
        }

        /// <summary>
        /// Gets/sets the one-shot hotkey (opens the overlay in one-shot mode). Changing this
        /// unregisters the previous key first.
        /// </summary>
        public HotKey OneShotHotKey
        {
            get
            {
                return _oneShotHotKey;
            }
            set
            {
                _oneShotHotKey = value;
                ReRegisterHotKey(_oneShotHotKey);
            }
        }

        /// <summary>
        /// The four quadrant hotkeys (TL/TR/BL/BR). Set once at startup; each is registered
        /// via RegisterHotKey. On press, fires <see cref="OnQuadrantHotKeyActivated"/> with
        /// the index. Null/empty entries are skipped (so a F-key left blank won't register).
        /// </summary>
        public HotKey[] QuadrantHotKeys
        {
            set
            {
                _quadrantHotKeys = value;
                if (_quadrantHotKeys != null)
                {
                    foreach (var hk in _quadrantHotKeys)
                    {
                        if (hk == null)
                        {
                            continue;
                        }
                        hk.RegistrationId = _hotkeyIdCounter++;
                        User32.RegisterHotKey(Handle, hk.RegistrationId, (uint)hk.Modifier, (uint)hk.Keys);
                    }
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == RawInput.WM_INPUT)
            {
                HandleRawInput(m.LParam);
            }
            if (m.Msg == Constants.WM_HOTKEY)
            {
                var e = new HotKeyEventArgs(m.LParam);

                // Normal hotkey
                if (_hotKey != null &&
                    e.Key == _hotKey.Keys &&
                    e.Modifiers == _hotKey.Modifier &&
                    OnHotKeyActivated != null)
                {
                    OnHotKeyActivated(this, new EventArgs());
                }

                // Task bar hotkey
                if (_taskbarHotKey != null &&
                    e.Key == _taskbarHotKey.Keys &&
                    e.Modifiers == _taskbarHotKey.Modifier &&
                    OnHotKeyActivated != null)
                {
                    OnTaskbarHotKeyActivated(this, new EventArgs());
                }

                // Debug hotkey
                if (_debugHotKey != null &&
                    e.Key == _debugHotKey.Keys &&
                    e.Modifiers == _debugHotKey.Modifier &&
                    OnDebugHotKeyActivated != null)
                {
                    OnDebugHotKeyActivated(this, new EventArgs());
                }

                // One-shot hotkey (opens the overlay in one-shot mode)
                if (_oneShotHotKey != null &&
                    e.Key == _oneShotHotKey.Keys &&
                    e.Modifiers == _oneShotHotKey.Modifier &&
                    OnOneShotHotKeyActivated != null)
                {
                    OnOneShotHotKeyActivated(this, new EventArgs());
                }

                // Quadrant hotkeys (Ctrl+Shift+F1..F4): 0=TL, 1=TR, 2=BL, 3=BR.
                if (_quadrantHotKeys != null)
                {
                    for (int i = 0; i < _quadrantHotKeys.Length; i++)
                    {
                        var qk = _quadrantHotKeys[i];
                        if (qk != null && e.Key == qk.Keys && e.Modifiers == qk.Modifier)
                        {
                            OnQuadrantHotKeyActivated?.Invoke(i);
                        }
                    }
                }
            }

            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool value)
        {
            // Ensures that the window will never be displayed
            base.SetVisibleCore(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Register for raw keyboard input (RIDEV_INPUTSINK: receives it even when not
            // foreground). This taps the keyboard driver independently of the low-level hook
            // chain, so physical Alt/Capslock held-state stays accurate even when AutoHotkey
            // fully intercepts Capslock or re-arms its LL hook above ours.
            var dev = new RawInput.RAWINPUTDEVICE
            {
                usUsagePage = RawInput.UsagePageGenericDesktop,
                usUsage = RawInput.UsageKeyboard,
                dwFlags = RawInput.RIDEV_INPUTSINK,
                hwndTarget = this.Handle
            };
            RawInput.RegisterRawInputDevices(new[] { dev }, 1,
                (uint)Marshal.SizeOf(typeof(RawInput.RAWINPUTDEVICE)));
        }

        /// <summary>
        /// Decodes a WM_INPUT keyboard event and updates the overlay hook's physical
        /// Capslock/Alt held-state. Raw input bypasses the LL-hook chain, so this is the
        /// source of truth that survives AutoHotkey intercepting Capslock.
        /// </summary>
        private void HandleRawInput(IntPtr hRawInput)
        {
            RawInput.RAWINPUT ri;
            uint size = (uint)Marshal.SizeOf(typeof(RawInput.RAWINPUT));
            if (RawInput.GetRawInputData(hRawInput, RawInput.RID_INPUT, out ri, ref size,
                    (uint)Marshal.SizeOf(typeof(RawInput.RAWINPUTHEADER))) == 0)
            {
                return;
            }
            if (ri.header.dwType != RawInput.RIM_TYPEKEYBOARD)
            {
                return;
            }
            bool down = (ri.keyboard.Flags & RawInput.RI_KEY_BREAK) == 0;
            ushort vk = ri.keyboard.VKey;
            if (vk == User32.VK_CAPITAL)
            {
                OverlayKeyboardHook.SetCapsHeld(down);
            }
            else if (vk == User32.VK_MENU || vk == User32.VK_LMENU || vk == User32.VK_RMENU)
            {
                OverlayKeyboardHook.SetAltHeld(down);
            }
        }
    }
}
