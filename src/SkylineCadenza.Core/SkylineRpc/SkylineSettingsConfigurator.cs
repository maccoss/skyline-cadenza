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
        int PeptideMaxLength,
        string FullScanAcquisitionMethod)
    {
        /// <summary>
        /// Short single-line description for the status bar.
        /// </summary>
        public string ToStatusLine() =>
            $"acquisition method {FullScanAcquisitionMethod}, "
            + $"precursor charges {{{string.Join(",", PrecursorIonCharges)}}}, "
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
            sb.AppendLine("  Transition Settings > Full-Scan:");
            sb.AppendLine($"    Acquisition method: {FullScanAcquisitionMethod}");
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
    /// Compute the recommendation from the scheduled candidates and the
    /// acquisition mode the assay was scheduled under. PRM mode maps to
    /// Skyline's <c>PRM</c> acquisition method; MTM maps to <c>DIA</c>
    /// since MTM is multiplexed-targeted DIA from Skyline's perspective
    /// (each MS/MS spectrum carries multiple co-isolated precursors,
    /// which is what Skyline's DIA chromatogram extractor expects).
    /// </summary>
    public static Recommendation Recommend(
        IReadOnlyList<Candidate> scheduledCandidates,
        AcquisitionMode mode)
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

        string acquisitionMethod = mode == AcquisitionMode.Prm ? "PRM" : "DIA";

        return new Recommendation(
            PrecursorIonCharges: precursorCharges,
            ProductIonCharges: fragmentCharges,
            ProductIonTypes: new[] { "y", "b" },
            LibraryPickTopN: BlibAssayWriter.PeaksPerSpectrum,
            PeptideMinLength: minLen,
            PeptideMaxLength: maxLen,
            FullScanAcquisitionMethod: acquisitionMethod);
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
        AcquisitionMode mode,
        CancellationToken cancellationToken = default)
    {
        var rec = Recommend(scheduledCandidates, mode);

        // One RunCommand per individual flag. Verified against
        // pwiz_tools/Skyline/CommandArgs.cs:
        //
        //   --pep-min-length / --pep-max-length          (line 1616, 1618)
        //   --tran-precursor-ion-charges                 (... look near 1530s)
        //   --tran-product-ion-charges                   (line 1537)
        //   --tran-product-ion-types                     (line 1540)
        //   --tran-product-start-ion                     (line 1542)
        //   --tran-product-end-ion                       (line 1545)
        //   --library-pick-product-ions=filter           (line 1564, enum: filter)
        //   --library-product-ions=N                     (line 1560)
        //
        // Batches are kept narrow because Skyline's SkylineCmd parser
        // aborts the entire batch on the first unknown argument (it
        // prints "Error: Unexpected argument X" and exits). One flag
        // per batch means any future unknown flag we add by mistake
        // only loses its own setting, not the ones around it.
        string[][] argBatches =
        {
            new[] { "--pep-min-length=" + rec.PeptideMinLength },
            new[] { "--pep-max-length=" + rec.PeptideMaxLength },
            new[] { "--tran-precursor-ion-charges=" + string.Join(",", rec.PrecursorIonCharges) },
            new[] { "--tran-product-ion-charges=" + string.Join(",", rec.ProductIonCharges) },
            new[] { "--tran-product-ion-types=" + string.Join(",", rec.ProductIonTypes) },
            new[] { "--tran-product-start-ion=ion 1" },
            new[] { "--tran-product-end-ion=last ion" },
            new[] { "--library-pick-product-ions=filter" },
            new[] { "--library-product-ions=" + rec.LibraryPickTopN },
            new[] { "--full-scan-acquisition-method=" + rec.FullScanAcquisitionMethod },
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
