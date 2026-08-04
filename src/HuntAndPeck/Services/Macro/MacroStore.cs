using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace HuntAndPeck.Services.Macro
{
    /// <summary>
    /// Loads/saves the user's macros from %APPDATA%\hap\macros.json. Personal data
    /// (window titles, screen coords, labels) -- intentionally NOT tracked in git.
    /// On first access (file missing) seeds a small sample so the picker is not empty.
    /// The pure (de)serialization is split out (Deserialize/Serialize) for unit tests
    /// so they need not touch real AppData.
    /// </summary>
    public static class MacroStore
    {
        public static string DirectoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hap");

        public static string FilePath => Path.Combine(DirectoryPath, "macros.json");

        private static readonly JsonSerializerSettings Indented = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        /// <summary>Loads macros, seeding a default file on first use. Never returns null.</summary>
        public static MacroFile Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var file = Deserialize(File.ReadAllText(FilePath));
                    if (file != null && file.Macros != null)
                    {
                        return file;
                    }
                }
            }
            catch
            {
                // Corrupt file: fall through to the seed WITHOUT overwriting the user's
                // file (so they can recover it). The picker simply shows the sample.
            }

            var seeded = Seed();
            try { Save(seeded); } catch { /* non-fatal: picker still works in-memory */ }
            return seeded;
        }

        public static void Save(MacroFile file)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, Serialize(file));
        }

        // ---- pure (de)serialization (unit-tested) ----

        public static MacroFile Deserialize(string json)
        {
            var f = JsonConvert.DeserializeObject<MacroFile>(json ?? "");
            return f ?? new MacroFile();
        }

        public static string Serialize(MacroFile file)
        {
            return JsonConvert.SerializeObject(file ?? new MacroFile(), Indented);
        }

        /// <summary>A small, documented sample so the first launch is not an empty list.</summary>
        private static MacroFile Seed()
        {
            return new MacroFile
            {
                Macros = new List<MacroDef>
                {
                    new MacroDef
                    {
                        Hotkey = "a",
                        Name = "sample: focus Feishu + Ctrl+N (edit me in macros.json)",
                        Steps = new List<MacroStep>
                        {
                            new MacroStep { Type = "focusWindow", Title = "Feishu", Match = "contains" },
                            new MacroStep { Type = "wait", Ms = 300 },
                            new MacroStep { Type = "send", Mods = "Ctrl", Key = "N" },
                        }
                    }
                }
            };
        }
    }
}
