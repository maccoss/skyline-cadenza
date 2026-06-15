namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Identity of one library precursor: Carafe-style modified sequence (with
/// flanking underscores and bracketed mods, e.g. <c>_C[UniMod:4]DIVIEK_</c>)
/// plus its precursor charge.
/// </summary>
public readonly record struct FragmentKey(string ModifiedPeptide, int PrecursorCharge);

/// <summary>
/// Helpers for converting DIA-NN modified-sequence syntax to Carafe syntax.
/// </summary>
public static class CarafeKey
{
    /// <summary>
    /// Convert a DIA-NN <c>Modified.Sequence</c> like <c>C(UniMod:4)DIVIEK</c>
    /// to Carafe's <c>_C[UniMod:4]DIVIEK_</c> by swapping parens for square
    /// brackets and wrapping the whole sequence in underscores.
    /// </summary>
    public static string FromDiann(string modifiedSequence)
    {
        // Two single-pass replacements - the DIA-NN syntax never escapes
        // parentheses, so there's no ambiguity.
        var sb = new System.Text.StringBuilder(modifiedSequence.Length + 2);
        sb.Append('_');
        foreach (var c in modifiedSequence)
        {
            sb.Append(c switch
            {
                '(' => '[',
                ')' => ']',
                _ => c,
            });
        }
        sb.Append('_');
        return sb.ToString();
    }
}
