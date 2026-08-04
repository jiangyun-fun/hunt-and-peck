using System.Collections.Generic;
using HuntAndPeck.Services.Macro;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class MacroStoreTest
    {
        [Fact]
        public void SerializeDeserialize_RoundTripsAllStepFields()
        {
            var file = new MacroFile
            {
                Macros = new List<MacroDef>
                {
                    new MacroDef
                    {
                        Hotkey = "a", Name = "test",
                        Steps = new List<MacroStep>
                        {
                            new MacroStep { Type = "focusWindow", Title = "Feishu", Match = "contains" },
                            new MacroStep { Type = "wait", Ms = 250 },
                            new MacroStep { Type = "send", Mods = "Ctrl,Shift", Key = "Q" },
                            new MacroStep { Type = "clickAbs", X = 10, Y = 20 },
                            new MacroStep { Type = "clickRel", Dx = 1, Dy = 2 },
                        }
                    }
                }
            };

            var json = MacroStore.Serialize(file);
            var back = MacroStore.Deserialize(json);

            Assert.Single(back.Macros);
            var m = back.Macros[0];
            Assert.Equal("a", m.Hotkey);
            Assert.Equal("test", m.Name);
            Assert.Equal(5, m.Steps.Count);

            Assert.Equal("focusWindow", m.Steps[0].Type);
            Assert.Equal("Feishu", m.Steps[0].Title);
            Assert.Equal("contains", m.Steps[0].Match);

            Assert.Equal(250, m.Steps[1].Ms);

            Assert.Equal("send", m.Steps[2].Type);
            Assert.Equal("Ctrl,Shift", m.Steps[2].Mods);
            Assert.Equal("Q", m.Steps[2].Key);

            Assert.Equal(10, m.Steps[3].X);
            Assert.Equal(20, m.Steps[3].Y);
            Assert.Equal(1, m.Steps[4].Dx);
            Assert.Equal(2, m.Steps[4].Dy);
        }

        [Fact]
        public void Deserialize_EmptyJsonReturnsEmptyFile()
        {
            var back = MacroStore.Deserialize("{}");
            Assert.NotNull(back.Macros);
            Assert.Empty(back.Macros);
        }

        [Fact]
        public void Deserialize_NullJsonReturnsEmptyFile()
        {
            var back = MacroStore.Deserialize(null);
            Assert.NotNull(back.Macros);
            Assert.Empty(back.Macros);
        }
    }
}
