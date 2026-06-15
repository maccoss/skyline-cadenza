using SkylineCadenza.Core.Parsimony;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Joins the three ingest products - DIA-NN rows, the parsimony assignment,
/// and the Carafe top-4 fragment table - into the
/// <see cref="Candidate"/> list that <see cref="Scheduler.Run"/> consumes.
/// </summary>
public static class CandidateBuilder
{
    /// <summary>
    /// Build the candidate list. Rows whose peptide isn't assigned to any
    /// parsimonious group are dropped (they don't belong to a coverable
    /// protein). Rows whose Carafe key has no fragment match get an empty
    /// fragment array - the scheduler treats them as solo-slot only.
    /// </summary>
    public static List<Candidate> Build(
        IEnumerable<DiannRow> rows,
        IReadOnlyDictionary<string, (string Group, string Type)> parsimony,
        IReadOnlyDictionary<FragmentKey, double[]> fragmentsByKey)
    {
        var candidates = new List<Candidate>();
        foreach (var row in rows)
        {
            if (!parsimony.TryGetValue(row.StrippedSequence, out var assignment))
                continue;

            var carafeKey = new FragmentKey(
                CarafeKey.FromDiann(row.ModifiedSequence),
                row.PrecursorCharge);
            // Prefer the user-supplied Carafe fragments if present.
            // Otherwise fall back to DIA-NN's per-row Fr.N.Id fragments
            // (real measured m/z), which gives a useful interference check
            // without requiring the user to load Carafe.
            if (!fragmentsByKey.TryGetValue(carafeKey, out var top4))
                top4 = row.Top4Fragments;

            candidates.Add(new Candidate
            {
                PrecursorId = row.PrecursorId,
                StrippedSequence = row.StrippedSequence,
                ModifiedSequence = row.ModifiedSequence,
                PrecursorCharge = row.PrecursorCharge,
                PrecursorMz = row.PrecursorMz,
                RtStart = row.RtStart,
                RtStop = row.RtStop,
                RtApex = row.RtApex,
                PrecursorQuantity = row.PrecursorQuantity,
                Proteotypic = row.Proteotypic,
                QValue = row.QValue,
                ProteinQValue = row.ProteinQValue,
                ProteinGroup = assignment.Group,
                PeptideType = assignment.Type,
                Top4Fragments = top4 ?? Array.Empty<double>(),
                Run = row.Run,
            });
        }
        return candidates;
    }

    /// <summary>
    /// Build candidates from the Carafe library when no DIA-NN report is
    /// available. Peak boundaries are synthesised by padding
    /// <c>TrRecalibrated</c> by <paramref name="peakHalfWidthMin"/> on each
    /// side (the SCHEDULER then layers its 30/15 s firing pad on top).
    /// </summary>
    public static List<Candidate> BuildFromCarafe(
        IEnumerable<CarafePrecursor> precursors,
        IReadOnlyDictionary<string, (string Group, string Type)> parsimony,
        double peakHalfWidthMin = 0.10)
    {
        var candidates = new List<Candidate>();
        foreach (var p in precursors)
        {
            if (!parsimony.TryGetValue(p.StrippedPeptide, out var assignment))
                continue;
            string precursorId = $"{p.StrippedPeptide}+{p.PrecursorCharge}";
            candidates.Add(new Candidate
            {
                PrecursorId = precursorId,
                StrippedSequence = p.StrippedPeptide,
                ModifiedSequence = p.ModifiedPeptide,
                PrecursorCharge = p.PrecursorCharge,
                PrecursorMz = p.PrecursorMz,
                RtStart = p.TrRecalibrated - peakHalfWidthMin,
                RtStop = p.TrRecalibrated + peakHalfWidthMin,
                RtApex = p.TrRecalibrated,
                PrecursorQuantity = p.PredictedIntensity,
                QValue = 0.0,
                ProteinQValue = double.NaN,
                Proteotypic = p.ProteinAccessions.Length == 1 ? 1 : 0,
                ProteinGroup = assignment.Group,
                PeptideType = assignment.Type,
                Top4Fragments = p.Top4Fragments,
                Run = "Carafe (predicted)",
            });
        }
        return candidates;
    }

    /// <summary>
    /// Build a peptide -> proteins map from Carafe precursors for parsimony.
    /// Multiple Carafe rows for the same stripped peptide get their
    /// accession sets unioned.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<string>> CarafePeptideProteinMap(
        IEnumerable<CarafePrecursor> precursors)
    {
        var byPeptide = new Dictionary<string, HashSet<string>>();
        foreach (var p in precursors)
        {
            if (!byPeptide.TryGetValue(p.StrippedPeptide, out var set))
            {
                set = new HashSet<string>();
                byPeptide[p.StrippedPeptide] = set;
            }
            foreach (var acc in p.ProteinAccessions) set.Add(acc);
        }
        return byPeptide.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToList());
    }
}
