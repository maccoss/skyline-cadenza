#nullable enable

using System.Text;
using SkylineCadenza.Core.Output;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.SkylineRpc;

/// <summary>
/// Aligns the running Skyline document's peptide / transition-filter
/// settings with the shape of the assay Cadenza is about to push.
/// </summary>
/// <remarks>
/// <para>
/// All updates go through <see cref="IJsonToolService.RunCommand"/>.
/// Per Nick Shulman: anything <c>SkylineCmd</c> can do is also
/// reachable via JSON-RPC <c>RunCommand</c> against the live document,
/// so we use the same flags here that the command-line build accepts.
/// Some flags may not be recognised by every Skyline version - those
/// surface as text in the returned report rather than aborting the
/// rest of the configuration.
/// </para>
/// <para>
/// The recommendation is intentionally union-of-assay for charge sets
/// and the broadest sensible ion-type / ion-range filter, so the
/// user's downstream Skyline analysis isn't surprised by a narrower
/// filter than what their library actually contains.
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
        /// to align the document with the recommendation (used as a
        /// fallback when one of the <c>SkylineCmd</c> flags fails).
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

    /// <summary>
    /// Result of running the configurator: the recommendation that was
    /// computed, plus any <c>SkylineCmd</c> output that came back from
    /// the live document (empty / whitespace means Skyline accepted the
    /// settings silently).
    /// </summary>
    public sealed record ConfigureResult(Recommendation Recommendation, string SkylineOutput);

    /// <summary>
    /// Compute the recommendation and apply it to the live document via
    /// <c>RunCommand</c>. Per Nick Shulman, <c>RunCommand</c> against
    /// the JSON-RPC pipe operates on the document held by the running
    /// Skyline UI, so the same flags <c>SkylineCmd</c> would accept on
    /// a saved file also work here.
    /// </summary>
    public static async Task<ConfigureResult> ConfigureAsync(
        SkylineSession session,
        IReadOnlyList<Candidate> scheduledCandidates,
        CancellationToken cancellationToken = default)
    {
        var rec = Recommend(scheduledCandidates);

        // One RunCommand per concern. Unknown flags get echoed back in
        // the output text rather than throwing, so we capture each
        // batch's response and concatenate.
        string[][] argBatches =
        {
            new[]
            {
                "--peptide-min-length=" + rec.PeptideMinLength,
                "--peptide-max-length=" + rec.PeptideMaxLength,
            },
            new[]
            {
                "--tran-precursor-ion-charges=" + string.Join(",", rec.PrecursorIonCharges),
                "--tran-product-ion-charges=" + string.Join(",", rec.ProductIonCharges),
                "--tran-product-ion-types=" + string.Join(",", rec.ProductIonTypes),
            },
            new[]
            {
                "--tran-product-start-ion=ion 1",
                "--tran-product-end-ion=last ion",
            },
            new[]
            {
                "--library-pick-product-ions=filter",
                "--tran-library-pick-N=" + rec.LibraryPickTopN,
            },
        };

        var collected = new List<string>(argBatches.Length);
        foreach (var args in argBatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string output = await Task.Run(
                    () => session.Execute(c => c.RunCommand(args)),
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(output))
                    collected.Add(output.Trim());
            }
            catch (Exception ex)
            {
                collected.Add($"({string.Join(" ", args)}): {ex.Message}");
            }
        }
        return new ConfigureResult(rec, string.Join(" | ", collected));
    }
}
