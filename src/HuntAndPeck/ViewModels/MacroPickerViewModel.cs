using System.Collections.Generic;
using System.Collections.ObjectModel;
using HuntAndPeck.Services.Macro;

namespace HuntAndPeck.ViewModels
{
    /// <summary>
    /// Backs the macro picker window. Holds the macro list (single-char hotkey +
    /// description) and a one-line status. Immutable after construction, so it needs
    /// no change notifications: WPF reads the values when the DataContext is set and
    /// the ObservableCollection keeps the list bindings live.
    /// </summary>
    public sealed class MacroPickerViewModel
    {
        public ObservableCollection<MacroDef> Macros { get; } = new ObservableCollection<MacroDef>();

        public string Status { get; }

        public MacroPickerViewModel(IEnumerable<MacroDef> macros, string status)
        {
            Status = status ?? "";
            if (macros != null)
            {
                foreach (var m in macros)
                {
                    Macros.Add(m);
                }
            }
        }
    }
}
