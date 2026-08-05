using System;
using System.Windows;
using System.Windows.Input;
using HuntAndPeck.Services.Macro;
using HuntAndPeck.ViewModels;

namespace HuntAndPeck.Views
{
    /// <summary>
    /// A small topmost palette of macros. Shown when the macro hotkey is pressed.
    /// Single-char select: typing a macro's hotkey (case-insensitive) runs it; Esc or
    /// losing focus cancels. This is a plain activated Window (NOT the non-activating
    /// overlay) -- opening it is a deliberate action, so momentary foreground is fine.
    /// The chosen macro (if any) is left in <see cref="Result"/> for the Closed handler.
    /// </summary>
    public partial class MacroPickerView : Window
    {
        private readonly MacroPickerViewModel _vm;

        // Guards Close() against re-entry: selecting a macro sets this before Close(),
        // so the Deactivated handler (which fires DURING Close) cannot wipe Result or
        // re-enter Close -- which previously cancelled the run AND crashed on teardown.
        private bool _closing;

        /// <summary>The macro the user chose, or null if cancelled. Read on Closed.</summary>
        public MacroDef Result { get; private set; }

        public MacroPickerView(MacroFile file)
        {
            InitializeComponent();

            var macros = file != null ? file.Macros : null;
            string status = (macros == null || macros.Count == 0)
                ? "no macros yet. Edit %APPDATA%\\hap\\macros.json, then re-open. Esc to close."
                : "type a macro key to run it; Esc to cancel";

            _vm = new MacroPickerViewModel(macros, status);
            DataContext = _vm;

            Loaded += (s, e) => Focus();
            // Cancel if the user clicks away (loses focus). Guarded by _closing so the
            // close fired by a selection (which also deactivates) does not re-enter here.
            Deactivated += (s, e) => CloseWithCancel();
        }

        private void MacroPickerView_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true; // swallow every key so focus never leaves the picker

            if (e.Key == Key.Escape)
            {
                CloseWithCancel();
                return;
            }

            string ch = KeyToChar(e.Key);
            if (ch == null)
            {
                return;
            }
            foreach (var m in _vm.Macros)
            {
                if (!string.IsNullOrEmpty(m.Hotkey) &&
                    m.Hotkey.Trim().Equals(ch, StringComparison.OrdinalIgnoreCase))
                {
                    Result = m;
                    _closing = true;
                    Close();
                    return;
                }
            }
            // No match: stay up, ignore.
        }

        /// <summary>Cancels the picker (Result=null) and closes, unless it is already closing.</summary>
        private void CloseWithCancel()
        {
            if (_closing) return;
            _closing = true;
            Result = null;
            Close();
        }

        // Map a letter/digit key to its lower-case single char (macros use single-char hotkeys).
        private static string KeyToChar(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                return ((char)('a' + (key - Key.A))).ToString();
            }
            if (key >= Key.D0 && key <= Key.D9)
            {
                return ((char)('0' + (key - Key.D0))).ToString();
            }
            return null;
        }
    }
}
