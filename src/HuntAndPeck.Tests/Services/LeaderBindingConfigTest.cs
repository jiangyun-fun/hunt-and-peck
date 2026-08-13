using HuntAndPeck.Services;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class LeaderBindingConfigTest
    {
        [Fact]
        public void Parse_FullDefaultString_YieldsAllBindings()
        {
            var list = LeaderBindingConfig.ParseLeaderBindings(
                "l=Left,r=Right,d=Double,m=Move,q=Close,z=Suspend,g=CycleLayout,i=ToggleDim,s=Snapshot");

            Assert.Equal(9, list.Count);
            AssertBinding(list, 'L', LeaderKind.Mode, ClickAction.Left);
            AssertBinding(list, 'R', LeaderKind.Mode, ClickAction.Right);
            AssertBinding(list, 'D', LeaderKind.Mode, ClickAction.Double);
            AssertBinding(list, 'M', LeaderKind.Mode, ClickAction.Move);
            AssertBinding(list, 'Q', LeaderKind.Close);
            AssertBinding(list, 'Z', LeaderKind.Suspend);
            AssertBinding(list, 'G', LeaderKind.CycleLayout);
            AssertBinding(list, 'I', LeaderKind.ToggleDim);
            AssertBinding(list, 'S', LeaderKind.Snapshot);
        }

        [Fact]
        public void Parse_SnapshotTarget()
        {
            var b = Assert.Single(LeaderBindingConfig.ParseLeaderBindings("s=Snapshot"));
            Assert.Equal('S', b.Key);
            Assert.Equal(LeaderKind.Snapshot, b.Kind);
        }

        [Fact]
        public void Parse_TripleTarget()
        {
            // <leader>t = Triple is a ClickAction mode binding.
            var b = Assert.Single(LeaderBindingConfig.ParseLeaderBindings("t=Triple"));
            Assert.Equal('T', b.Key);
            Assert.Equal(LeaderKind.Mode, b.Kind);
            Assert.Equal(ClickAction.Triple, b.Mode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_NullOrBlank_ReturnsEmpty(string raw)
        {
            Assert.Empty(LeaderBindingConfig.ParseLeaderBindings(raw));
        }

        [Fact]
        public void Parse_MalformedEntries_AreSkipped()
        {
            // "x=" has no target, "=Close" has no key, "ab=Left" has a 2-char key,
            // "y=Bogus" has an unknown target. Only "z=Suspend" survives.
            var list = LeaderBindingConfig.ParseLeaderBindings("x=,=Close,ab=Left,y=Bogus,z=Suspend");

            var z = Assert.Single(list);
            Assert.Equal('Z', z.Key);
            Assert.Equal(LeaderKind.Suspend, z.Kind);
        }

        [Fact]
        public void Parse_KeysAreCaseInsensitive_AndUppercased()
        {
            var list = LeaderBindingConfig.ParseLeaderBindings("L=left,R=RIGHT");

            Assert.Equal(2, list.Count);
            Assert.Equal('L', list[0].Key);
            Assert.Equal(ClickAction.Left, list[0].Mode);
            Assert.Equal('R', list[1].Key);
            Assert.Equal(ClickAction.Right, list[1].Mode);
        }

        [Theory]
        [InlineData("a=Layout", LeaderKind.CycleLayout)]
        [InlineData("b=CYCLELAYOUT", LeaderKind.CycleLayout)]
        [InlineData("c=Dim", LeaderKind.ToggleDim)]
        [InlineData("d=TOGGLEDIM", LeaderKind.ToggleDim)]
        public void Parse_FunctionNameAliases(string raw, LeaderKind expected)
        {
            var list = LeaderBindingConfig.ParseLeaderBindings(raw);
            var b = Assert.Single(list);
            Assert.Equal(expected, b.Kind);
        }

        [Fact]
        public void Parse_AcceptsComma_Semicolon_PipeSeparators()
        {
            var list = LeaderBindingConfig.ParseLeaderBindings("a=Left;b=Right|c=Close");

            Assert.Equal(3, list.Count);
            Assert.Equal('A', list[0].Key);
            Assert.Equal('B', list[1].Key);
            Assert.Equal('C', list[2].Key);
        }

        [Fact]
        public void ReadLeaderBindings_FallsBackToDefault_WhenKeyAbsent()
        {
            // The test App.config does not define LeaderBindings, so the default map
            // (l/r/d/m/q/z/g/i) must be returned and be non-empty.
            var list = LeaderBindingConfig.ReadLeaderBindings();
            Assert.True(list.Count >= 8);
            Assert.Contains(list, b => b.Key == 'L' && b.Kind == LeaderKind.Mode && b.Mode == ClickAction.Left);
            Assert.Contains(list, b => b.Key == 'Q' && b.Kind == LeaderKind.Close);
            Assert.Contains(list, b => b.Key == 'S' && b.Kind == LeaderKind.Snapshot);
            Assert.Contains(list, b => b.Key == 'T' && b.Kind == LeaderKind.Mode && b.Mode == ClickAction.Triple);
        }

        [Fact]
        public void DisplayLabel_DescribesEachBinding()
        {
            var list = LeaderBindingConfig.ParseLeaderBindings(
                "l=Left,r=Right,d=Double,t=Triple,m=Move,q=Close,z=Suspend,g=CycleLayout,i=ToggleDim");

            Assert.Equal("left click", Find(list, 'L').DisplayLabel());
            Assert.Equal("right click", Find(list, 'R').DisplayLabel());
            Assert.Equal("double click", Find(list, 'D').DisplayLabel());
            Assert.Equal("triple click", Find(list, 'T').DisplayLabel());
            Assert.Equal("move only", Find(list, 'M').DisplayLabel());
            Assert.Equal("close", Find(list, 'Q').DisplayLabel());
            Assert.Equal("suspend", Find(list, 'Z').DisplayLabel());
            Assert.Equal("cycle layout", Find(list, 'G').DisplayLabel());
            Assert.Equal("toggle dim", Find(list, 'I').DisplayLabel());
        }

        private static void AssertBinding(System.Collections.Generic.IReadOnlyList<LeaderBinding> list,
            char key, LeaderKind kind, ClickAction mode = ClickAction.Left)
        {
            var b = Find(list, key);
            Assert.Equal(kind, b.Kind);
            Assert.Equal(mode, b.Mode);
        }

        private static LeaderBinding Find(System.Collections.Generic.IReadOnlyList<LeaderBinding> list, char key)
        {
            foreach (var b in list)
            {
                if (b.Key == key)
                {
                    return b;
                }
            }
            throw new System.Exception("No binding for key " + key);
        }
    }
}
