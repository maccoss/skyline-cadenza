#nullable enable

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Streams the Carafe AI-predicted spectral library (TSV, ~2.6 GB / 22.8M
/// rows on the reference dataset) and returns the top-<c>n</c> fragment m/z
/// values per precursor for the subset of precursors the caller is
/// interested in.
/// </summary>
/// <remarks>
/// Port of the cell that produces <c>carafe_top4.parquet</c> in
/// <c>targeted-modeling/gpf_coverage.ipynb</c> (cell <c>dfead2c9</c>).
///
/// Implementation notes:
/// <list type="bullet">
/// <item>
/// We do not use any CSV library. CsvHelper at this scale buffers every
/// row into a POCO and allocates a fresh string per column, which becomes
/// minutes of GC at 22.8M rows. A hand-rolled <see cref="StreamReader"/>
/// line loop with column-index parsing reaches ~150-200 MB/s on a NVMe.
/// </item>
/// <item>
/// We resolve column indices by reading the header line once, so the
/// reader doesn't break when Carafe adds new columns or shuffles them.
/// </item>
/// <item>
/// Early rejection: the first column is <c>ModifiedPeptide</c>. After
/// parsing it we check membership in <paramref name="allowedKeys"/> and
/// skip the rest of the line if it's not interesting. This rejects the
/// vast majority of rows with no allocation beyond a substring per line.
/// </item>
/// </list>
/// </remarks>
public sealed record FragmentEntry(double FragmentMz, double RelativeIntensity);

public static class CarafeTsvReader
{
    private const string ModifiedPeptideCol = "ModifiedPeptide";
    private const string PrecursorChargeCol = "PrecursorCharge";
    private const string FragmentMzCol = "FragmentMz";
    private const string RelativeIntensityCol = "RelativeIntensity";

    /// <summary>
    /// Reports progress through the file every <c>~1%</c> of bytes consumed.
    /// </summary>
    public sealed class Progress
    {
        public long BytesRead { get; init; }
        public long TotalBytes { get; init; }
        public long RowsScanned { get; init; }
        public long RowsKept { get; init; }
    }

    /// <summary>
    /// Streams the TSV at <paramref name="tsvPath"/> and returns the top
    /// <paramref name="topN"/> fragment m/z values per precursor for any
    /// precursor whose Carafe-format modified sequence appears in
    /// <paramref name="allowedKeys"/>.
    /// </summary>
    /// <remarks>
    /// Returned fragment arrays are <b>sorted by m/z ascending</b>, ready to
    /// hand to <see cref="Scheduling.FragmentClash.AnyWithin"/>.
    /// </remarks>
    public static Dictionary<FragmentKey, double[]> ExtractTopFragments(
        string tsvPath,
        HashSet<string> allowedKeys,
        int topN,
        IProgress<Progress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tsvPath))
            throw new FileNotFoundException("Carafe library TSV not found", tsvPath);

        var fi = new FileInfo(tsvPath);
        long totalBytes = fi.Length;

        // First pass: parse header to find column indices.
        using var fs = File.OpenRead(tsvPath);
        using var reader = new StreamReader(fs);
        string? headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("Carafe TSV is empty.");
        var headers = headerLine.Split('\t');
        int idxPep = IndexOf(headers, ModifiedPeptideCol);
        int idxCharge = IndexOf(headers, PrecursorChargeCol);
        int idxMz = IndexOf(headers, FragmentMzCol);
        int idxIntensity = IndexOf(headers, RelativeIntensityCol);
        int maxIdx = Math.Max(Math.Max(idxPep, idxCharge), Math.Max(idxMz, idxIntensity));

        var perKey = new Dictionary<FragmentKey, List<FragmentEntry>>(allowedKeys.Count);
        long rowsScanned = 0, rowsKept = 0;
        long nextReportAt = Math.Max(totalBytes / 100, 1);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            rowsScanned++;
            if ((rowsScanned & 0xFFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            // Cheap early reject: extract the modified-peptide token (first
            // field) and check membership. Most rows fail this check.
            int firstTab = line.IndexOf('\t');
            if (firstTab < 0) continue;
            string pep = line.Substring(0, firstTab);
            if (!allowedKeys.Contains(pep)) continue;

            // Now do the full split. We need at most maxIdx+1 fields.
            var fields = line.Split('\t', maxIdx + 2);
            if (fields.Length <= maxIdx) continue;

            if (!int.TryParse(fields[idxCharge], out int charge)) continue;
            if (!double.TryParse(fields[idxMz], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double mz)) continue;
            if (!double.TryParse(fields[idxIntensity], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double intensity)) continue;

            var key = new FragmentKey(pep, charge);
            if (!perKey.TryGetValue(key, out var list))
            {
                list = new List<FragmentEntry>(12);
                perKey[key] = list;
            }
            list.Add(new FragmentEntry(mz, intensity));
            rowsKept++;

            if (progress != null && fs.Position >= nextReportAt)
            {
                progress.Report(new Progress
                {
                    BytesRead = fs.Position,
                    TotalBytes = totalBytes,
                    RowsScanned = rowsScanned,
                    RowsKept = rowsKept,
                });
                nextReportAt += Math.Max(totalBytes / 100, 1);
            }
        }

        progress?.Report(new Progress
        {
            BytesRead = totalBytes,
            TotalBytes = totalBytes,
            RowsScanned = rowsScanned,
            RowsKept = rowsKept,
        });

        // Reduce each (peptide, charge) bucket to its top-N fragments by
        // intensity, then return their m/z values sorted ascending so they
        // can feed directly into the fragment-clash two-pointer check.
        var result = new Dictionary<FragmentKey, double[]>(perKey.Count);
        foreach (var (key, entries) in perKey)
        {
            entries.Sort(static (x, y) => y.RelativeIntensity.CompareTo(x.RelativeIntensity));
            int take = Math.Min(topN, entries.Count);
            var mzs = new double[take];
            for (int i = 0; i < take; i++) mzs[i] = entries[i].FragmentMz;
            Array.Sort(mzs);
            result[key] = mzs;
        }
        return result;
    }

    private static int IndexOf(string[] headers, string name)
    {
        for (int i = 0; i < headers.Length; i++)
            if (headers[i] == name) return i;
        throw new InvalidDataException(
            $"Carafe TSV is missing required column '{name}'. Found: {string.Join(", ", headers)}");
    }
}
