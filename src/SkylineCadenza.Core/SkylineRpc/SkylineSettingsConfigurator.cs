#nullable enable

using System.Text;
using SkylineCadenza.Core.Output;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.SkylineRpc;

/// <summary>
/// Computes the peptide and transition-filter settings that Cadenza's
/// assay needs Skyline to be configured with, so the imported
/// transition list lands as a complete target tree.
/// </summary>
/// <remarks>
/// <para>
/// Today the JSON-RPC interface does not expose a way to modify the
/// LIVE document's peptide / transition filter settings - the relevant
/// <c>SkylineCmd</c> flags (<c>--tran-precursor-ion-charges</c>, etc.)
/// operate on a saved document via <c>--in</c> / <c>--out</c>, not on
/// the in-memory document held by the running Skyline UI that we're
/// connected to via the pipe. As a result this class does NOT mutate
/// any settings: it only computes the recommended values from the
/// assay and returns them as user-facing text, so the user can verify
/// or apply them through Skyline's UI before the push.
/// </para>
/// <para>
/// Upstream gap: a JSON-RPC method like <c>SetTransitionFilter(charges,
/// ionTypes, ionRange)</c> would close this. Until that lands, the
/// most reliable workflow is for the user to set transition settings
/// once per Skyline session via the UI (Settings &gt; Transition
/// Settings &gt; Filter) and then run pushes against that baseline.
/// </para>
/// </remarks>
public static class SkylineSettingsConfigurator
{
    /// <summary>
    /// Recommended document settings derived from the scheduled assay.
    /// </summary>
    public sealed record Recommendation(
        IReadOnlyList<int> PrecursorIonCharges,
        IReadOnlyList<int> ProductIonCharges,
        IReadOnlyList<string> ProductIonTypes,
        int LibraryPickTopN,
        int PeptideMinLength,
        int PeptideMaxLength)
    {
        /// <summary>
        /// Short single-line description for the status bar.
        /// </summary>
        public string ToStatusLine() =>
            $"precursor charges {{{string.Join(",", PrecursorIonCharges)}}}, "
            + $"product charges {{{string.Join(",", ProductIonCharges)}}}, "
            + $"ion types {{{string.Join(",", ProductIonTypes)}}}, "
            + $"library pick top {LibraryPickTopN}, "
            + $"peptide length {PeptideMinLength}-{PeptideMaxLength}.";

        /// <summary>
        /// Step-by-step instructions the user can follow in the Skyline UI
        /// to align the document with the recommendation.
        /// </summary>
        public string ToUiInstructions()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Verify in Skyline (Settings menu):");
            sb.AppendLine("  Transition Settings > Filter > Peptides:");
            sb.AppendLine($"    Precursor charges: {string.Join(", ", PrecursorIonCharges)}");
            sb.AppendLine($"    Ion charges:      {string.Join(", ", ProductIonCharges)}");
            sb.AppendLine($"    Ion types:        {string.Join(", ", ProductIonTypes)}");
            sb.AppendLine($"    Product ion selection: ion 1 to last ion");
            sb.AppendLine("  Transition Settings > Library:");
            sb.AppendLine($"    Pick {LibraryPickTopN} product ions");
            sb.AppendLine("    Match tolerance m/z: 0.5 (or your standard)");
            sb.AppendLine("  Peptide Settings > Filter:");
            sb.AppendLine($"    Min length: {PeptideMinLength}");
            sb.AppendLine($"    Max length: {PeptideMaxLength}");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>
    /// Compute the recommendation from the scheduled candidates.
    /// </summary>
    public static Recommendation Recommend(IReadOnlyList<Candidate> scheduledCandidates)
    {
        var precursorCharges = scheduledCandidates
            .Select(c => c.PrecursorCharge)
            .Where(z => z > 0)
            .Distinct()
            .OrderBy(z => z)
            .ToList();
        if (precursorCharges.Count == 0) precursorCharges.AddRange(new[] { 2, 3 });

        var fragmentCharges = scheduledCandidates
            .SelectMany(c => c.Fragments.Select(f => f.Charge))
            .Where(z => z > 0)
            .Distinct()
            .OrderBy(z => z)
            .ToList();
        if (fragmentCharges.Count == 0) fragmentCharges.Add(1);

        var lengths = scheduledCandidates
            .Select(c => c.StrippedSequence?.Length ?? 0)
            .Where(n => n > 0)
            .ToList();
        int minLen = lengths.Count == 0 ? 6 : Math.Max(4, lengths.Min());
        int maxLen = lengths.Count == 0 ? 30 : Math.Min(60, lengths.Max());

        return new Recommendation(
            PrecursorIonCharges: precursorCharges,
            ProductIonCharges: fragmentCharges,
            ProductIonTypes: new[] { "y", "b" },
            LibraryPickTopN: BlibAssayWriter.PeaksPerSpectrum,
            PeptideMinLength: minLen,
            PeptideMaxLength: maxLen);
    }
}
