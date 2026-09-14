using System.Text;
#nullable enable

namespace MCDSaveEdit.Logic
{
    /// <summary>
    /// Loose comparison for item and enchantment display names crossing between this app
    /// and MCD Builder.
    ///
    /// The two vocabularies disagree in three small ways and none of them can be resolved
    /// by picking a "correct" spelling:
    ///
    ///   apostrophes  the builder lists "Winters Touch" but keeps "Archer's Armor"
    ///   brackets     this app disambiguates variants as "Void Strike (Melee)"
    ///   casing       "Luck Of The Sea" against "Luck of the Sea"
    ///
    /// Normalising both sides to the same key makes a lookup tolerant of all three. The
    /// matching TypeScript lives in the builder at src/lib/items.ts; keep the two in step.
    /// </summary>
    public static class NameMatching
    {
        public static string normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) { return string.Empty; }

            var builder = new StringBuilder(name!.Length);
            bool pendingSpace = false;
            int bracketDepth = 0;

            foreach (var character in name!)
            {
                //Anything bracketed goes, brackets included.
                if (character == '(') { bracketDepth++; continue; }
                if (character == ')') { if (bracketDepth > 0) { bracketDepth--; } continue; }
                if (bracketDepth > 0) { continue; }

                //Apostrophes vanish rather than becoming separators, so "Winter's" and
                //"Winters" collapse to the same key instead of "winter s".
                if (character == '\'' || character == '‘' || character == '’') { continue; }

                if (char.IsLetterOrDigit(character))
                {
                    if (pendingSpace && builder.Length > 0) { builder.Append(' '); }
                    builder.Append(char.ToLowerInvariant(character));
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = true;
                }
            }

            return builder.ToString();
        }
    }
}
