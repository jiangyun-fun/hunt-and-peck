using HuntAndPeck.Services.Macro;
using Xunit;

namespace HuntAndPeck.Tests.Services
{
    public class WindowFinderTest
    {
        // MatchesTitle(windowTitle, target, match) -- the pure predicate tested here;
        // FindByTitle/FocusByTitle need a real desktop and are not unit-tested.

        [Theory]
        [InlineData("Feishu", "exact", true)]       // exact match
        [InlineData("feishu", "exact", true)]       // exact, case-insensitive
        [InlineData("Feish", "exact", false)]       // exact rejects substring
        [InlineData("Feish", "contains", true)]     // contains matches substring
        [InlineData("xyz", "contains", false)]      // contains, no match
        [InlineData("Feishu", null, true)]          // null match = default exact
        [InlineData("Feishu", "", true)]            // empty match = default exact
        public void MatchesTitle_Rules(string target, string match, bool expected)
        {
            Assert.Equal(expected, WindowFinder.MatchesTitle("Feishu", target, match));
        }

        [Fact]
        public void MatchesTitle_NullWindowIsNoMatch()
        {
            Assert.False(WindowFinder.MatchesTitle(null, "Feishu", "contains"));
        }

        [Fact]
        public void MatchesTitle_EmptyTargetIsNoMatch()
        {
            Assert.False(WindowFinder.MatchesTitle("Feishu", "", "contains"));
        }
    }
}
