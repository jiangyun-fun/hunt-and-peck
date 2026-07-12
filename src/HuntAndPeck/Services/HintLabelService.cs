using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using HuntAndPeck.Extensions;
using HuntAndPeck.Services.Interfaces;

namespace HuntAndPeck.Services
{
    internal class HintLabelService : IHintLabelService
    {
        /// <summary>
        /// Gets available hint strings
        /// </summary>
        /// <remarks>Adapted from vimium to give a consistent experience, see https://github.com/philc/vimium/blob/master/content_scripts/link_hints.js </remarks>
        /// <param name="hintCount">The number of hints</param>
        /// <returns>A list of hint strings</returns>
        public IList<string> GetHintStrings(int hintCount)
        {
            var hintStrings = new List<string>();
            if (hintCount <= 0)
            {
                return hintStrings;
            }

            var hintCharacters = ReadHintCharacters();
            var digitsNeeded = (int)Math.Ceiling(Math.Log(hintCount) / Math.Log(hintCharacters.Length));

            var wholeHintCount = (int)Math.Pow(hintCharacters.Length, digitsNeeded);
            var shortHintCount = (wholeHintCount - hintCount) / hintCharacters.Length;
            var longHintCount = hintCount - shortHintCount;

            var longHintPrefixCount = wholeHintCount / hintCharacters.Length - shortHintCount;
            for (int i = 0, j = 0; i < longHintCount; ++i, ++j)
            {
                hintStrings.Add(new string(NumberToHintString(j, hintCharacters, digitsNeeded).Reverse().ToArray()));
                if (longHintPrefixCount > 0 && (i + 1) % longHintPrefixCount == 0)
                {
                    j += shortHintCount;
                }
            }

            if (digitsNeeded > 1)
            {
                for (var i = 0; i < shortHintCount; ++i)
                {
                    hintStrings.Add(new string(NumberToHintString(i + longHintPrefixCount, hintCharacters, digitsNeeded - 1).Reverse().ToArray()));
                }
            }

            // Sort ordinally so consecutive labels share their FIRST character
            // (AA, AB, AC, ..., BA, BB, ...). The Vimium order emitted above alternates
            // the first char fastest (AA, BA, CA, ...), grouping by the LAST char, which
            // reads as messy and scatters each first-char group across the whole screen.
            // Sorting is a pure permutation of the prefix-free Vimium set, so uniqueness
            // and the no-label-is-a-prefix-of-another property are both preserved.
            hintStrings.Sort(StringComparer.Ordinal);
            return hintStrings;
        }

        /// <summary>
        /// Reads the hint character set from hap.exe.config (hot-reload). More characters
        /// means more short labels: 14 chars gives ~196 two-char labels, 26 (A-Z) gives
        /// ~676. Defaults to the Vimium-style home-row set.
        /// </summary>
        private static char[] ReadHintCharacters()
        {
            try
            {
                OverlayActionConfig.EnsureFresh();
                var raw = ConfigurationManager.AppSettings["HintCharacters"];
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var chars = raw.Trim().ToUpperInvariant().Distinct().ToArray();
                    if (chars.Length > 0)
                    {
                        return chars;
                    }
                }
            }
            catch (Exception)
            {
                // fall through to default
            }

            return new[] { 'S', 'A', 'D', 'F', 'J', 'K', 'L', 'E', 'W', 'C', 'M', 'P', 'G', 'H' };
        }

        /// <summary>
        /// Converts a number like "8" into a hint string like "JK". This is used to sequentially generate all of the
        /// hint text. The hint string will be "padded with zeroes" to ensure its length is >= numHintDigits.
        /// </summary>
        /// <remarks>Adapted from vimium to give a consistent experience, see https://github.com/philc/vimium/blob/master/content_scripts/link_hints.js</remarks>
        /// <param name="number">The number</param>
        /// <param name="characterSet">The set of characters</param>
        /// <param name="noHintDigits">The number of hint digits</param>
        /// <returns>A hint string</returns>
        private string NumberToHintString(int number, char[] characterSet, int noHintDigits = 0)
        {
            var divisor = characterSet.Length;
            var hintString = new StringBuilder();

            do
            {
                var remainder = number % divisor;
                hintString.Insert(0, characterSet[remainder]);
                number -= remainder;
                number /= (int)Math.Floor((double)divisor);
            } while (number > 0);

            // Pad the hint string we're returning so that it matches numHintDigits.
            // Note: the loop body changes hintString.length, so the original length must be cached!
            var length = hintString.Length;
            for (var i = 0; i < (noHintDigits - length); ++i)
            {
                hintString.Insert(0, characterSet[0]);
            }

            return hintString.ToString();
        }
    }
}
