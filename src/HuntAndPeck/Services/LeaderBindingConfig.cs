using System;
using System.Collections.Generic;
using System.Configuration;

namespace HuntAndPeck.Services
{
    /// <summary>
    /// What a leader (&lt;Space&gt;) binding does. <see cref="LeaderKind.Mode"/> carries a
    /// <see cref="ClickAction"/>; the rest are discrete overlay functions that previously
    /// lived on dedicated keys (digits / backtick).
    /// </summary>
    public enum LeaderKind
    {
        /// <summary>Set the click mode (Left/Right/Double/Move).</summary>
        Mode,
        /// <summary>Close the overlay (Esc / 1 alias).</summary>
        Close,
        /// <summary>Enter persistent suspend.</summary>
        Suspend,
        /// <summary>Cycle the grid layout preset (Grid + GridLayouts).</summary>
        CycleLayout,
        /// <summary>Toggle label dim.</summary>
        ToggleDim,
        /// <summary>Enter snapshot-region mode (2-pick; captures the rectangle to the clipboard).</summary>
        Snapshot,
        /// <summary>Enter text-span selection (2-pick; selects the text between two labels).</summary>
        SelectText,
        /// <summary>Toggle the group view (dotted first-char group boxes vs. full labels).</summary>
        ToggleGroupView,
        /// <summary>Toggle the persistent quadrant guide (cross + quadrant letters).</summary>
        ToggleQuadrantGuide
    }

    /// <summary>
    /// One leader binding: the single key pressed after &lt;Space&gt;, and the action it
    /// fires. Immutable value type.
    /// </summary>
    public readonly struct LeaderBinding
    {
        public char Key { get; }
        public LeaderKind Kind { get; }
        /// <summary>Valid only when <see cref="Kind"/> == <see cref="LeaderKind.Mode"/>.</summary>
        public ClickAction Mode { get; }

        public LeaderBinding(char key, LeaderKind kind, ClickAction mode = ClickAction.Left)
        {
            Key = key;
            Kind = kind;
            Mode = mode;
        }

        /// <summary>Human-readable label for the leader popup, e.g. "left click".</summary>
        public string DisplayLabel()
        {
            switch (Kind)
            {
                case LeaderKind.Mode:
                    switch (Mode)
                    {
                        case ClickAction.Left: return "left click";
                        case ClickAction.Right: return "right click";
                        case ClickAction.Double: return "double click";
                        case ClickAction.Triple: return "triple click";
                        case ClickAction.Move: return "move only";
                        default: return Mode.ToString().ToLowerInvariant();
                    }
                case LeaderKind.Close: return "close";
                case LeaderKind.Suspend: return "suspend";
                case LeaderKind.CycleLayout: return "cycle layout";
                case LeaderKind.ToggleDim: return "toggle dim";
                case LeaderKind.Snapshot: return "snapshot region";
                case LeaderKind.SelectText: return "select text";
                case LeaderKind.ToggleGroupView: return "toggle group view";
                case LeaderKind.ToggleQuadrantGuide: return "toggle quadrant guide";
                default: return Kind.ToString().ToLowerInvariant();
            }
        }
    }

    /// <summary>
    /// Parses the <c>LeaderBindings</c> setting from <c>App.config</c>: a
    /// comma/semicolon/pipe-separated list of <c>key=target</c> pairs, where target is
    /// either a <see cref="ClickAction"/> name (Left/Right/Double/Move) or a function name
    /// (Close/Suspend/CycleLayout/ToggleDim). Keys are uppercased. Hot-reload via
    /// <see cref="OverlayActionConfig.EnsureFresh"/>. Malformed entries are skipped; a
    /// missing/empty/all-invalid value falls back to the default map. The order of the
    /// returned list is the popup display order.
    /// </summary>
    public static class LeaderBindingConfig
    {
        /// <summary>The App.config key for the leader binding list.</summary>
        public const string AppSettingKey = "LeaderBindings";

        // Default leader map. No `q` binding: Q is the direct Esc/close alias in the
        // keyboard hook (it classifies as Escape before label input, so a <leader>q
        // binding could never fire). No `i` binding either: plain I is insert mode
        // (it classifies as InsertToggle before label/leader input). Dim therefore
        // lives on `x`. `s` = snapshot region, `v` = select text span, `p` = toggle
        // group view (dotted first-char boxes vs. full labels), `c` = toggle the
        // quadrant guide (cross; live + persisted, no restart).
        private static readonly LeaderBinding[] DefaultBindings =
        {
            new LeaderBinding('L', LeaderKind.Mode, ClickAction.Left),
            new LeaderBinding('R', LeaderKind.Mode, ClickAction.Right),
            new LeaderBinding('D', LeaderKind.Mode, ClickAction.Double),
            new LeaderBinding('T', LeaderKind.Mode, ClickAction.Triple),
            new LeaderBinding('M', LeaderKind.Mode, ClickAction.Move),
            new LeaderBinding('Z', LeaderKind.Suspend),
            new LeaderBinding('G', LeaderKind.CycleLayout),
            new LeaderBinding('C', LeaderKind.ToggleQuadrantGuide),
            new LeaderBinding('X', LeaderKind.ToggleDim),
            new LeaderBinding('S', LeaderKind.Snapshot),
            new LeaderBinding('V', LeaderKind.SelectText),
            new LeaderBinding('P', LeaderKind.ToggleGroupView),
        };

        /// <summary>
        /// Reads and parses <c>LeaderBindings</c>, falling back to the default map on a
        /// missing/empty/all-invalid value or a malformed config (keeps the app usable).
        /// </summary>
        public static IReadOnlyList<LeaderBinding> ReadLeaderBindings()
        {
            try
            {
                OverlayActionConfig.EnsureFresh();
                string raw = ConfigurationManager.AppSettings[AppSettingKey];
                var parsed = ParseLeaderBindings(raw);
                return parsed.Count > 0 ? parsed : DefaultList();
            }
            catch (Exception)
            {
                // Deliberate fallback so a malformed config keeps the app usable.
                return DefaultList();
            }
        }

        /// <summary>
        /// Parse "l=Left,r=Right,d=Double,m=Move,q=Close,z=Suspend,g=CycleLayout,i=ToggleDim".
        /// Later duplicates of the same key overwrite earlier ones for lookup, but keep
        /// their first-seen position in the returned list (display order). Returns an empty
        /// list (never null) when nothing parses.
        /// </summary>
        public static IReadOnlyList<LeaderBinding> ParseLeaderBindings(string raw)
        {
            var list = new List<LeaderBinding>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return list;
            }

            char[] separators = { ',', ';', '|' };
            string[] tokens = raw.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                string t = token.Trim();
                int eq = t.IndexOf('=');
                if (eq <= 0 || eq == t.Length - 1)
                {
                    continue; // malformed: missing key= or empty target
                }
                string keyStr = t.Substring(0, eq).Trim();
                string target = t.Substring(eq + 1).Trim();
                if (keyStr.Length != 1)
                {
                    continue; // a leader binding key is a single char
                }
                char key = char.ToUpperInvariant(keyStr[0]);
                LeaderBinding? b = TryBuild(key, target);
                if (b.HasValue)
                {
                    list.Add(b.Value);
                }
            }
            return list;
        }

        private static LeaderBinding? TryBuild(char key, string target)
        {
            // A ClickAction mode name (Left/Right/Double/Move)?
            ClickAction mode;
            if (Enum.TryParse(target, true, out mode))
            {
                return new LeaderBinding(key, LeaderKind.Mode, mode);
            }

            // A function name?
            switch (target.ToUpperInvariant())
            {
                case "CLOSE": return new LeaderBinding(key, LeaderKind.Close);
                case "SUSPEND": return new LeaderBinding(key, LeaderKind.Suspend);
                case "CYCLELAYOUT":
                case "LAYOUT": return new LeaderBinding(key, LeaderKind.CycleLayout);
                case "TOGGLEDIM":
                case "DIM": return new LeaderBinding(key, LeaderKind.ToggleDim);
                case "SNAPSHOT": return new LeaderBinding(key, LeaderKind.Snapshot);
                case "SELECTTEXT":
                case "SELECT": return new LeaderBinding(key, LeaderKind.SelectText);
                case "GROUPVIEW":
                case "GROUPS": return new LeaderBinding(key, LeaderKind.ToggleGroupView);
                case "QUADRANTGUIDE":
                case "GUIDE": return new LeaderBinding(key, LeaderKind.ToggleQuadrantGuide);
                default: return null; // unknown target -> skip
            }
        }

        private static IReadOnlyList<LeaderBinding> DefaultList()
        {
            var list = new List<LeaderBinding>(DefaultBindings.Length);
            foreach (LeaderBinding b in DefaultBindings)
            {
                list.Add(b);
            }
            return list;
        }
    }
}
