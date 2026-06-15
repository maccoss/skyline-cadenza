#nullable enable

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// One precursor materialised from the Carafe AI-predicted spectral library.
/// Produced when the user runs Cadenza without a DIA-NN report - the
/// library + a target protein list become the candidate pool.
/// </summary>
public sealed class CarafePrecursor
{
    public required string ModifiedPeptide { get; init; }     // Carafe-format, e.g. "_C[UniMod:4]DIVIEK_"
    public required string StrippedPeptide { get; init; }
    public required double PrecursorMz { get; init; }
    public required int PrecursorCharge { get; init; }
    public required double TrRecalibrated { get; init; }      // apex RT (min)
    /// <summary>
    /// Raw <c>ProteinID</c> column value, often UniProt-style and possibly
    /// semicolon-separated for shared peptides. The bare accession is
    /// derived via <see cref="ProteinListParser"/>.
    /// </summary>
    public required string ProteinIdRaw { get; init; }
    /// <summary>Distinct accession ids parsed out of <see cref="ProteinIdRaw"/>.</summary>
    public required string[] ProteinAccessions { get; init; }
    /// <summary>
    /// Sum of the top-N <c>RelativeIntensity</c> values - used as a
    /// predicted-intensity proxy for the peptide-ranking knobs.
    /// </summary>
    public required double PredictedIntensity { get; init; }
    /// <summary>Top-N fragment m/z values sorted ascending.</summary>
    public required double[] Top4Fragments { get; init; }
}

/// <summary>
/// Streams a Carafe TSV and builds <see cref="CarafePrecursor"/>s for any
/// precursor whose <c>ProteinID</c> column maps to an accession in the
/// caller-supplied target set.
/// </summary>
/// <remarks>
/// Same parser shape as <see cref="CarafeTsvReader.ExtractTopFragments"/>:
/// header-indexed column lookup, span-based line splitting, early reject on
/// the protein column to skip the vast majority of rows. The 2.6 GB
/// reference library streams in ~13 s at ~200 MB/s.
/// </remarks>
public static class CarafeLibraryReader
{
    private const string Cols_ModifiedPeptide = "ModifiedPeptide";
    private const string Cols_StrippedPeptide = "StrippedPeptide";
    private const string Cols_PrecursorMz = "PrecursorMz";
    private const string Cols_PrecursorCharge = "PrecursorCharge";
    private const string Cols_Tr = "Tr_recalibrated";
    private const string Cols_ProteinId = "ProteinID";
    private const string Cols_Decoy = "Decoy";
    private const string Cols_FragmentMz = "FragmentMz";
    private const string Cols_RelativeIntensity = "RelativeIntensity";

    public static List<CarafePrecursor> LoadCandidates(
        string tsvPath,
        IReadOnlySet<string> allowedAccessions,
        int topN = 4,
        IProgress<CarafeTsvReader.Progress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(tsvPath))
            throw new FileNotFoundException("Carafe library TSV not found", tsvPath);
        if (allowedAccessions.Count == 0)
            return new List<CarafePrecursor>();

        var fi = new FileInfo(tsvPath);
        long totalBytes = fi.Length;

        using var fs = File.OpenRead(tsvPath);
        using var reader = new StreamReader(fs);
        string? headerLine = reader.ReadLine()
            ?? throw new InvalidDataException("Carafe TSV is empty.");
        var headers = headerLine.Split('\t');
        int idxModPep = Find(headers, Cols_ModifiedPeptide);
        int idxStripPep = Find(headers, Cols_StrippedPeptide);
        int idxMz = Find(headers, Cols_PrecursorMz);
        int idxCharge = Find(headers, Cols_PrecursorCharge);
        int idxTr = Find(headers, Cols_Tr);
        int idxProtein = Find(headers, Cols_ProteinId);
        int idxDecoy = FindOptional(headers, Cols_Decoy);
        int idxFragMz = Find(headers, Cols_FragmentMz);
        int idxRelInt = Find(headers, Cols_RelativeIntensity);
        int maxIdx = new[] { idxModPep, idxStripPep, idxMz, idxCharge, idxTr,
            idxProtein, idxDecoy, idxFragMz, idxRelInt }.Max();

        // Per (modified peptide, charge): accumulating data while we walk
        // the TSV. We only need one set of precursor metadata per key.
        var perKey = new Dictionary<FragmentKey, RowAccumulator>(allowedAccessions.Count);
        long rowsScanned = 0, rowsKept = 0;
        long nextReportAt = Math.Max(totalBytes / 100, 1);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            rowsScanned++;
            if ((rowsScanned & 0xFFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var fields = line.Split('\t', maxIdx + 2);
            if (fields.Length <= maxIdx) continue;

            // Early reject on the protein column. ProteinID may contain
            // multiple accessions (semicolon-separated) and is usually in
            // UniProt form (sp|ACC|NAME); we need to test each.
            string proteinRaw = fields[idxProtein];
            if (string.IsNullOrEmpty(proteinRaw)) continue;
            var accs = ExtractAccessions(proteinRaw);
            bool matches = false;
            foreach (var acc in accs)
            {
                if (allowedAccessions.Contains(acc)) { matches = true; break; }
            }
            if (!matches) continue;

            // Decoys
            if (idxDecoy >= 0 && fields[idxDecoy] != "0") continue;

            string modPep = fields[idxModPep];
            if (!int.TryParse(fields[idxCharge], out int charge)) continue;
            var key = new FragmentKey(modPep, charge);

            if (!perKey.TryGetValue(key, out var acc1))
            {
                if (!double.TryParse(fields[idxMz], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double mz)) continue;
                if (!double.TryParse(fields[idxTr], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double tr)) continue;

                acc1 = new RowAccumulator
                {
                    ModifiedPeptide = modPep,
                    StrippedPeptide = fields[idxStripPep],
                    PrecursorMz = mz,
                    PrecursorCharge = charge,
                    TrRecalibrated = tr,
                    ProteinIdRaw = proteinRaw,
                    Accessions = accs,
                };
                perKey[key] = acc1;
            }

            if (!double.TryParse(fields[idxFragMz], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double fmz)) continue;
            if (!double.TryParse(fields[idxRelInt], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double rint)) continue;
            acc1.Fragments.Add((fmz, rint));
            rowsKept++;

            if (progress != null && fs.Position >= nextReportAt)
            {
                progress.Report(new CarafeTsvReader.Progress
                {
                    BytesRead = fs.Position,
                    TotalBytes = totalBytes,
                    RowsScanned = rowsScanned,
                    RowsKept = rowsKept,
                });
                nextReportAt += Math.Max(totalBytes / 100, 1);
            }
        }

        progress?.Report(new CarafeTsvReader.Progress
        {
            BytesRead = totalBytes, TotalBytes = totalBytes,
            RowsScanned = rowsScanned, RowsKept = rowsKept,
        });

        // Top-N fragment selection + predicted-intensity proxy.
        var result = new List<CarafePrecursor>(perKey.Count);
        foreach (var (key, acc1) in perKey)
        {
            acc1.Fragments.Sort(static (a, b) => b.intensity.CompareTo(a.intensity));
            int take = Math.Min(topN, acc1.Fragments.Count);
            var mzs = new double[take];
            double predIntensity = 0;
            for (int i = 0; i < take; i++)
            {
                mzs[i] = acc1.Fragments[i].mz;
                predIntensity += acc1.Fragments[i].intensity;
            }
            Array.Sort(mzs);
            result.Add(new CarafePrecursor
            {
                ModifiedPeptide = acc1.ModifiedPeptide,
                StrippedPeptide = acc1.StrippedPeptide,
                PrecursorMz = acc1.PrecursorMz,
                PrecursorCharge = acc1.PrecursorCharge,
                TrRecalibrated = acc1.TrRecalibrated,
                ProteinIdRaw = acc1.ProteinIdRaw,
                ProteinAccessions = acc1.Accessions,
                PredictedIntensity = predIntensity,
                Top4Fragments = mzs,
            });
        }
        return result;
    }

    private sealed class RowAccumulator
    {
        public required string ModifiedPeptide { get; init; }
        public required string StrippedPeptide { get; init; }
        public required double PrecursorMz { get; init; }
        public required int PrecursorCharge { get; init; }
        public required double TrRecalibrated { get; init; }
        public required string ProteinIdRaw { get; init; }
        public required string[] Accessions { get; init; }
        public List<(double mz, double intensity)> Fragments { get; } = new(12);
    }

    /// <summary>
    /// Extract bare accession ids from a Carafe <c>ProteinID</c> value. Handles
    /// semicolon-separated lists and UniProt-style <c>sp|ACC|NAME</c> ids.
    /// </summary>
    private static string[] ExtractAccessions(string raw)
    {
        var parts = raw.Split(';');
        var result = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length > 3 && (part.StartsWith("sp|", StringComparison.Ordinal) ||
                                    part.StartsWith("tr|", StringComparison.Ordinal)))
            {
                int firstPipe = part.IndexOf('|');
                int secondPipe = part.IndexOf('|', firstPipe + 1);
                if (secondPipe > firstPipe + 1)
                {
                    result[i] = part.Substring(firstPipe + 1, secondPipe - firstPipe - 1);
                    continue;
                }
            }
            result[i] = part;
        }
        return result;
    }

    private static int Find(string[] headers, string name)
    {
        for (int i = 0; i < headers.Length; i++)
            if (headers[i] == name) return i;
        throw new InvalidDataException(
            $"Carafe TSV is missing required column '{name}'. Found: {string.Join(", ", headers)}");
    }

    private static int FindOptional(string[] headers, string name)
    {
        for (int i = 0; i < headers.Length; i++)
            if (headers[i] == name) return i;
        return -1;
    }
}
