#nullable enable

using SkylineCadenza.Core.Parsimony;
using SkylineCadenza.Core.Scheduling;
using SkylineCadenza.Core.SkylineRpc;
using SkylineTool;

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Loads a peptide candidate pool directly from a running Skyline document
/// over the JSON-RPC interface. Uses a custom report definition so we get
/// the exact columns we need (no built-in report carries RT alongside the
/// fragment-level data).
/// </summary>
/// <remarks>
/// Strategy: ask Skyline for one row per (peptide, charge, fragment) and
/// regroup in memory into candidates. For each (peptide, charge) we keep
/// the top-4 fragments by <c>LibraryIntensity</c>. Retention time comes
/// from <c>PredictedResultRetentionTime</c> when available, with simple
/// fallback column names probed in order. Peak-boundary bounds are
/// synthesised as <c>RT ± peakHalfWidthMin</c> (the scheduler's own RT
/// buffer is layered on top via <c>SchedulingParameters.FiringPadSec</c>).
/// </remarks>
public static class SkylineLibraryLoader
{
    /// <summary>
    /// Column names probed in order for the peptide-level retention time.
    /// PEPTIDE-LEVEL columns only - per-replicate columns like
    /// <c>PredictedResultRetentionTime</c> or <c>BestRetentionTime</c>
    /// multiply every transition by every replicate (tens of millions of
    /// rows on a 100-run document).
    /// </summary>
    /// <remarks>
    /// Skyline will happily ACCEPT some of these column names and return
    /// rows containing empty / zero values - e.g. a document without an
    /// iRT calculator returns 0 for <c>PredictedRetentionTime</c>. The
    /// probe asks for a small sample and walks down the list until it
    /// finds a column whose first non-null parse is &gt; 0.
    /// </remarks>
    private static readonly string[] RtColumnCandidates =
    {
        "PredictedRetentionTime",
        "LibraryRetentionTime",
        "AverageMeasuredRetentionTime",
        "BestRetentionTime",
        "PeptideRetentionTime",
        "RetentionTime",
    };

    public sealed class LoadResult
    {
        public required List<Candidate> Candidates { get; init; }
        public required string RtColumnUsed { get; init; }
        public required int RawRowsFetched { get; init; }
        public required int DistinctPeptides { get; init; }
        public required int DistinctProteinGroups { get; init; }
    }

    public static async Task<LoadResult> LoadAsync(
        SkylineSession session,
        double peakHalfWidthMin = 0.30,
        int pageSize = 10000,
        string? rtColumnOverride = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Find a working RT column. If the caller supplied an explicit
        // column name (e.g. user typed it into the UI override), use that
        // straight away. Otherwise probe the candidate list and require a
        // non-zero value in the sample - Skyline will accept a column
        // name and return all zeros when the document hasn't configured
        // an RT calculator.
        string rtColumn;
        if (!string.IsNullOrWhiteSpace(rtColumnOverride))
        {
            rtColumn = rtColumnOverride!.Trim();
            progress?.Report($"Skyline: using user-supplied RT column '{rtColumn}'.");
        }
        else
        {
            rtColumn = await ProbeRtColumnAsync(session, progress, cancellationToken)
                ?? throw new InvalidOperationException(
                    "Skyline rejected every retention-time column candidate I tried "
                    + $"({string.Join(", ", RtColumnCandidates)}) or every value in the probe "
                    + "sample was 0. Open the document grid in Skyline, find the column with "
                    + "your predicted/library RT, and paste its invariant name into the "
                    + "'RT column override' box.");
        }

        var def = new ReportDefinition
        {
            Select = new[]
            {
                "ProteinName",
                "PeptideModifiedSequence",
                "PrecursorCharge",
                "PrecursorMz",
                "ProductMz",
                "LibraryIntensity",
                rtColumn,
            },
            Uimode = "proteomic",
            // PivotReplicate = true folds per-replicate columns into one
            // column-per-replicate instead of multiplying rows. Even when
            // we don't ask for any replicate column, this prevents a stray
            // setting from blowing the row count up.
            PivotReplicate = true,
        };

        // Fast path: ExportReportFromDefinition writes the whole report
        // to disk in one round-trip instead of paginating GetReportRows
        // (which recomputes the report from scratch on each page). On the
        // SEA-AD-class document this is ~20x faster.
        return await ExportThenReadAsync(session, def, rtColumn, peakHalfWidthMin, progress, cancellationToken);
    }

    private static async Task<LoadResult> ExportThenReadAsync(
        SkylineSession session,
        ReportDefinition def,
        string rtColumn,
        double peakHalfWidthMin,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string tempPath = Path.Combine(Path.GetTempPath(),
            $"cadenza-skyline-{Guid.NewGuid():N}.csv");
        try
        {
            progress?.Report($"Skyline: exporting library to {Path.GetFileName(tempPath)}...");
            var metadata = await Task.Run(() => session.Execute(c =>
                c.ExportReportFromDefinition(def, tempPath, "invariant")), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            int reportedRows = metadata?.RowCount ?? -1;
            progress?.Report($"Skyline: exported {reportedRows:n0} rows. Parsing...");

            // Parse the resulting CSV.
            using var reader = new StreamReader(tempPath);
            string? headerLine = await reader.ReadLineAsync(cancellationToken)
                ?? throw new InvalidDataException("Skyline export CSV was empty.");
            var headers = ParseCsvLine(headerLine);
            var colIdx = new Dictionary<string, int>(headers.Length, StringComparer.Ordinal);
            for (int i = 0; i < headers.Length; i++) colIdx[headers[i]] = i;

            int parsed = 0;
            var buckets = new Dictionary<(string ModSeq, int Charge), PrecursorAcc>();
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                parsed++;
                if ((parsed & 0x3FFF) == 0)
                    progress?.Report($"Skyline: parsed {parsed:n0} rows...");

                var fields = ParseCsvLine(line);
                if (fields.Length < headers.Length) continue;
                try
                {
                    string protein = Cell(fields, colIdx, "ProteinName");
                    string modSeq = Cell(fields, colIdx, "PeptideModifiedSequence");
                    if (!int.TryParse(Cell(fields, colIdx, "PrecursorCharge"), out int charge)) continue;
                    if (!double.TryParse(Cell(fields, colIdx, "PrecursorMz"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double precMz)) continue;
                    if (!double.TryParse(Cell(fields, colIdx, "ProductMz"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double prodMz)) continue;
                    double.TryParse(Cell(fields, colIdx, "LibraryIntensity"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double libInt);
                    double.TryParse(Cell(fields, colIdx, rtColumn),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double rt);

                    var key = (modSeq, charge);
                    if (!buckets.TryGetValue(key, out var acc))
                    {
                        acc = new PrecursorAcc
                        {
                            ProteinName = protein,
                            ModifiedSequence = modSeq,
                            PrecursorCharge = charge,
                            PrecursorMz = precMz,
                            Rt = rt,
                        };
                        buckets[key] = acc;
                    }
                    acc.Fragments.Add((prodMz, libInt));
                }
                catch { /* skip malformed */ }
            }

            return BuildResultFromBuckets(buckets, rtColumn, parsed, peakHalfWidthMin);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    private static LoadResult BuildResultFromBuckets(
        Dictionary<(string ModSeq, int Charge), PrecursorAcc> buckets,
        string rtColumn,
        int rawRowsParsed,
        double peakHalfWidthMin)
    {
        // peptide -> proteins for parsimony.
        var pepToProts = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var acc in buckets.Values)
        {
            string stripped = StripModifications(acc.ModifiedSequence);
            if (!pepToProts.TryGetValue(stripped, out var existing))
            {
                pepToProts[stripped] = new[] { acc.ProteinName };
            }
            else
            {
                var set = new HashSet<string>(existing) { acc.ProteinName };
                pepToProts[stripped] = set.ToList();
            }
        }
        var parsimony = ParsimonyEngine.Assign(pepToProts);

        var candidates = new List<Candidate>(buckets.Count);
        foreach (var acc in buckets.Values)
        {
            string stripped = StripModifications(acc.ModifiedSequence);
            if (!parsimony.TryGetValue(stripped, out var assignment)) continue;

            acc.Fragments.Sort(static (a, b) => b.intensity.CompareTo(a.intensity));
            int take = Math.Min(4, acc.Fragments.Count);
            var top4 = new double[take];
            for (int i = 0; i < take; i++) top4[i] = acc.Fragments[i].mz;
            Array.Sort(top4);

            double summedIntensity = acc.Fragments.Take(take).Sum(f => f.intensity);
            candidates.Add(new Candidate
            {
                PrecursorId = $"{stripped}+{acc.PrecursorCharge}",
                StrippedSequence = stripped,
                ModifiedSequence = acc.ModifiedSequence,
                PrecursorCharge = acc.PrecursorCharge,
                PrecursorMz = acc.PrecursorMz,
                RtStart = acc.Rt - peakHalfWidthMin,
                RtStop = acc.Rt + peakHalfWidthMin,
                RtApex = acc.Rt,
                PrecursorQuantity = summedIntensity,
                QValue = 0.0,
                ProteinQValue = double.NaN,
                Proteotypic = pepToProts[stripped].Count == 1 ? 1 : 0,
                ProteinGroup = assignment.Group,
                PeptideType = assignment.Type,
                Top4Fragments = top4,
                Run = "Skyline document",
            });
        }

        return new LoadResult
        {
            Candidates = candidates,
            RtColumnUsed = rtColumn,
            RawRowsFetched = rawRowsParsed,
            DistinctPeptides = pepToProts.Count,
            DistinctProteinGroups = candidates.Select(c => c.ProteinGroup).Distinct().Count(),
        };
    }

    /// <summary>
    /// Minimal CSV-line splitter: handles double-quoted fields with escaped
    /// embedded quotes; does not handle embedded newlines (Skyline's
    /// invariant-culture CSV export doesn't produce them).
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>(16);
        var sb = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == ',') { result.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"') inQuotes = true;
                else sb.Append(c);
            }
        }
        result.Add(sb.ToString());
        return result.ToArray();
    }

    private static async Task<string?> ProbeRtColumnAsync(
        SkylineSession session,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // Sample 200 rows so we have a decent chance of catching the
        // first non-zero RT even if leading rows happen to be empty.
        const int probeSampleSize = 200;
        foreach (var rt in RtColumnCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Skyline: probing RT column '{rt}'...");
            var def = new ReportDefinition
            {
                Select = new[] { "ProteinName", "PeptideModifiedSequence", "PrecursorCharge", rt },
                Uimode = "proteomic",
                PivotReplicate = true,
            };
            try
            {
                var probe = await Task.Run(() => session.Execute(c =>
                    c.GetReportFromDefinitionRows(def, 0, probeSampleSize, false, "invariant")), cancellationToken);
                if (probe?.Rows is null || probe.Rows.Length == 0)
                {
                    progress?.Report($"Skyline: '{rt}' returned 0 rows. Trying next.");
                    continue;
                }
                int rtIdx = -1;
                if (probe.Columns is not null)
                {
                    for (int i = 0; i < probe.Columns.Length; i++)
                        if (probe.Columns[i].Name == rt) { rtIdx = i; break; }
                }
                if (rtIdx < 0)
                {
                    progress?.Report($"Skyline: '{rt}' missing from schema. Trying next.");
                    continue;
                }

                bool anyValid = false;
                foreach (var row in probe.Rows)
                {
                    if (rtIdx >= row.Length) continue;
                    string cell = row[rtIdx];
                    if (string.IsNullOrWhiteSpace(cell)) continue;
                    if (double.TryParse(cell, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double rtVal)
                        && rtVal > 0)
                    {
                        anyValid = true;
                        break;
                    }
                }
                if (anyValid)
                {
                    progress?.Report($"Skyline: '{rt}' has non-zero data; using it.");
                    return rt;
                }
                progress?.Report($"Skyline: '{rt}' exists but every value in the probe sample was empty or 0. Trying next.");
            }
            catch (Exception ex)
            {
                progress?.Report($"Skyline: '{rt}' rejected ({ex.Message}). Trying next.");
            }
        }
        return null;
    }

    private static Dictionary<string, int> BuildColumnIndex(ReportRowsColumn[] cols)
    {
        var map = new Dictionary<string, int>(cols.Length, StringComparer.Ordinal);
        for (int i = 0; i < cols.Length; i++)
            map[cols[i].Name] = i;
        return map;
    }

    private static string Cell(string[] row, Dictionary<string, int> idx, string name)
    {
        return idx.TryGetValue(name, out int i) && i < row.Length ? row[i] ?? "" : "";
    }

    /// <summary>
    /// Strip modifications from a peptide-modified sequence to recover the
    /// plain amino-acid sequence. Handles both <c>C[+57.0]DIVIEK</c> and
    /// <c>C(UniMod:4)DIVIEK</c> conventions plus flanking underscores.
    /// </summary>
    private static string StripModifications(string modSeq)
    {
        if (string.IsNullOrEmpty(modSeq)) return modSeq;
        var sb = new System.Text.StringBuilder(modSeq.Length);
        int depth = 0;
        foreach (var ch in modSeq)
        {
            if (ch == '_') continue;
            if (ch == '[' || ch == '(') { depth++; continue; }
            if (ch == ']' || ch == ')') { if (depth > 0) depth--; continue; }
            if (depth > 0) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private sealed class PrecursorAcc
    {
        public required string ProteinName { get; init; }
        public required string ModifiedSequence { get; init; }
        public required int PrecursorCharge { get; init; }
        public required double PrecursorMz { get; init; }
        public required double Rt { get; init; }
        public List<(double mz, double intensity)> Fragments { get; } = new(12);
    }
}
