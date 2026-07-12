using HuntAndPeck.Services;
using Xunit;
using System;
using System.Linq;

namespace HuntAndPeck.Tests.Services
{
    public class HintLabelServiceTest
    {
        [Fact]
        public void GetHintStrings_UniqueStrings()
        {
            // Arrange
            const int hintCount = 256;
            var hintService = new HintLabelService();

            // Act
            var hints = hintService.GetHintStrings(hintCount);

            // Assert
            Assert.Equal(hintCount, hints.Distinct().Count());
        }

        [Fact]
        public void GetHintStrings_AreSortedSoFirstCharGroupsConsecutively()
        {
            // Consecutive labels should share their first char (AA, AB, AC, ..., BA, BB,
            // ...), not their last char (AA, BA, CA, ...). Equivalent to: the list is in
            // ordinal order.
            var hintService = new HintLabelService();

            var hints = hintService.GetHintStrings(50);

            var sorted = hints.OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(sorted, hints);
        }

        [Fact]
        public void GetHintStrings_ArePrefixFree()
        {
            // No label may be a prefix of another, otherwise prefix matching is
            // ambiguous (typing the short one could also be the start of the long one).
            // Sorting must preserve this Vimium invariant.
            var hintService = new HintLabelService();

            var hints = hintService.GetHintStrings(50);

            for (int i = 0; i < hints.Count; i++)
            {
                for (int j = 0; j < hints.Count; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }
                    Assert.False(
                        hints[j].StartsWith(hints[i], StringComparison.Ordinal),
                        $"label '{hints[i]}' is a prefix of '{hints[j]}'");
                }
            }
        }
    }
}
