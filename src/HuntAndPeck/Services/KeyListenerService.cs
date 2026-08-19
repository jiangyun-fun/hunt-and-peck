using HuntAndPeck.NativeMethods;
using System;
using System.Windows.Forms;
using HuntAndPeck.Services.Interfaces;

namespace HuntAndPeck.Services
{
    internal class KeyListenerService : Form, IKeyListenerService, IDisposable
    {
        public event EventHandler OnHotKeyActivated;
        public event EventHandler OnTaskbarHotKeyActivated;
        public event EventHandler OnDebugHotKeyActivated;
        public event EventHandler OnMacroHotKeyActivated;

        /// <summary>Quadrant hotkey (Ctrl+Shift+F1..F4): carries the quadrant index 0..3 (TL/TR/BL/BR).</summary>
        public event Action<int> OnQuadrantHotKeyActivated;

        /// <summary>
        /// Global counter for assigning ids to identiy the hot key registration
        /// </summary>
        private int _hotkeyIdCounter = 0;

        private HotKey _hotKey;
        private HotKey _taskbarHotKey;
        private HotKey _debugHotKey;
        private HotKey _macroHotKey;
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

            TryRegister(hotKey);
        }

        /// <summary>
        /// Registers a hotkey and SURFACES failure. RegisterHotKey returns false when
        /// another process already owns the combo, and the return was previously
        /// ignored -- so a conflicting tool (or a stale hap instance started before
        /// the mutex guarded startup) silently left a hotkey dead, which on-box
        /// masqueraded as "hap stopped working". One MessageBox per failing chord.
        /// </summary>
        private void TryRegister(HotKey hotKey)
        {
            hotKey.RegistrationId = _hotkeyIdCounter++;
            if (User32.RegisterHotKey(Handle, hotKey.RegistrationId, (uint)hotKey.Modifier, (uint)hotKey.Keys))
            {
                return;
            }
            string chord = hotKey.Modifier + "+" + hotKey.Keys;
            MessageBox.Show(
                "hap could not register the hotkey " + chord + ".\n\n"
                + "Another program (or another hap instance) already owns it.\n"
                + "If a hap tray icon is already running, exit it first (tray menu:\n"
                + "Exit), then start hap again. Otherwise check which app uses\n"
                + "this combo and change HotkeyKey/HotkeyModifier in the options.",
                "hap hotkey conflict",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
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

        /// <summary>Macro picker hotkey (default Ctrl+Shift+;): opens the macro palette.</summary>
        public HotKey MacroHotKey
        {
            get { return _macroHotKey; }
            set
            {
                _macroHotKey = value;
                ReRegisterHotKey(_macroHotKey);
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
                        TryRegister(hk);
                    }
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
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

                // Macro hotkey (opens the macro picker palette)
                if (_macroHotKey != null &&
                    e.Key == _macroHotKey.Keys &&
                    e.Modifiers == _macroHotKey.Modifier &&
                    OnMacroHotKeyActivated != null)
                {
                    OnMacroHotKeyActivated(this, new EventArgs());
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
    }
}
