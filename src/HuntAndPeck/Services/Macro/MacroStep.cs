using System.Collections.Generic;

namespace HuntAndPeck.Services.Macro
{
    /// <summary>
    /// One authorable macro: a single-char hotkey (typed in the picker), a human
    /// description, and an ordered list of steps executed left-to-right when run.
    /// </summary>
    public sealed class MacroDef
    {
        public string Hotkey { get; set; }
        public string Name { get; set; }
        public List<MacroStep> Steps { get; set; } = new List<MacroStep>();
    }

    /// <summary>
    /// The persisted macros file (~/AppData/Roaming/hap/macros.json):
    /// <code>{ "macros": [ { "hotkey":"a", "name":"...", "steps":[ ... ] } ] }</code>
    /// </summary>
    public sealed class MacroFile
    {
        public List<MacroDef> Macros { get; set; } = new List<MacroDef>();
    }

    /// <summary>
    /// A single step in a macro. Flat shape (one class, a Type discriminator plus
    /// optional fields) so it serializes trivially and a hand-author only fills the
    /// fields relevant to the step type. Unknown step types fail loudly at run time.
    ///
    /// B1 implements send/wait/focusWindow/clickAbs/clickRel/rawReplay.
    /// openOverlay/overlayType/nudge arrive with overlay integration (B2) and throw
    /// until then.
    /// </summary>
    public sealed class MacroStep
    {
        public string Type { get; set; }

        // send
        public string Mods { get; set; }   // "Ctrl,Shift" (Ctrl/Shift/Alt/Win)
        public string Key { get; set; }    // System.Windows.Forms.Keys name, e.g. "Q","N","F1"

        // wait
        public int Ms { get; set; }

        // focusWindow
        public string Title { get; set; }
        public string Match { get; set; }   // "exact" (default) or "contains"

        // clickAbs
        public int X { get; set; }
        public int Y { get; set; }

        // clickRel (relative to the focused window's rect)
        public int Dx { get; set; }
        public int Dy { get; set; }

        // overlayType
        public string Label { get; set; }

        // nudge
        public string Dir { get; set; }    // Left/Right/Up/Down
        public string Tier { get; set; }   // Small/Medium/Large

        // rawReplay
        public List<RawEvent> Events { get; set; }
    }

    /// <summary>One recorded key event for rawReplay (phase-2 recorder output).</summary>
    public sealed class RawEvent
    {
        public int Vk { get; set; }       // virtual-key code
        public bool Down { get; set; }    // true=keydown, false=keyup
        public int Ms { get; set; }       // delay AFTER this event before the next
    }
}
