using Parquet;
using Parquet.Schema;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Loads a DIA-NN <c>report.parquet</c> into <see cref="DiannRow"/>s,
/// filtering by Q-value + decoy and de-duplicating per
/// <c>Precursor.Id</c> (keeping the observation with the highest
/// <c>Precursor.Quantity</c>).
/// </summary>
/// <remarks>
/// Port of the candidate-build setup in
/// <c>targeted-modeling/gpf_coverage.ipynb</c> cell <c>20730103</c>:
/// <code>
/// raw = pd.read_parquet(REPORT_PATH, columns=cand_cols)
/// raw = raw[(raw["Decoy"] == 0) &amp; (raw["Q.Value"] &lt; cutoff)]
/// best_idx = raw.groupby("Precursor.Id")["Precursor.Quantity"].idxmax()
/// candidates = raw.loc[best_idx].reset_index(drop=True)
/// </code>
/// Only projected columns are read; the parquet is small enough (~25 MB on
/// the reference dataset) that we materialise everything in one pass and
/// dedupe in memory.
/// </remarks>
public static class DiannParquetReader
{
    private const string Run = "Run";
    private const string PrecursorId = "Precursor.Id";
    private const string ModifiedSequence = "Modified.Sequence";
    private const string StrippedSequence = "Stripped.Sequence";
    private const string PrecursorCharge = "Precursor.Charge";
    private const string PrecursorMz = "Precursor.Mz";
    private const string Rt = "RT";
    private const string RtStart = "RT.Start";
    private const string RtStop = "RT.Stop";
    private const string PrecursorQuantity = "Precursor.Quantity";
    private const string Proteotypic = "Proteotypic";
    private const string ProteinIds = "Protein.Ids";
    private const string Genes = "Genes";
    private const string QValue = "Q.Value";
    private const string Decoy = "Decoy";
    private const string PgQValue = "PG.Q.Value";
    private const int FragmentColumnCount = 12;
    // Keep up to the full DIA-NN column count so downstream consumers
    // (scheduler top-4 clash check, BLIB writer top-6) can both pull
    // from the same Candidate.Fragments without re-ingesting.
    private const int TopFragmentsKept = Candidate.FragmentLimit;

    public static async Task<List<DiannRow>> LoadAsync(
        string parquetPath,
        double qValueCutoff,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(parquetPath))
            throw new FileNotFoundException("DIA-NN report.parquet not found", parquetPath);

        using var stream = File.OpenRead(parquetPath);
        using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);

        // Map the column names we care about to their DataFields. Missing
        // columns throw a clear error rather than NREing later.
        var schema = reader.Schema;
        var fRun = GetField(schema, Run);
        var fPrecursorId = GetField(schema, PrecursorId);
        var fModSeq = GetField(schema, ModifiedSequence);
        var fStripSeq = GetField(schema, StrippedSequence);
        var fCharge = GetField(schema, PrecursorCharge);
        var fMz = GetField(schema, PrecursorMz);
        var fRt = GetField(schema, Rt);
        var fRtStart = GetField(schema, RtStart);
        var fRtStop = GetField(schema, RtStop);
        var fQuantity = GetField(schema, PrecursorQuantity);
        var fProteotypic = GetField(schema, Proteotypic);
        var fProteinIds = GetField(schema, ProteinIds);
        var fGenes = GetField(schema, Genes);
        var fQValue = GetField(schema, QValue);
        var fDecoy = GetField(schema, Decoy);
        // PG.Q.Value is optional - older DIA-NN runs may omit it.
        DataField fPgQValue = null;
        foreach (var df in schema.DataFields)
        {
            if (df.Name == PgQValue) { fPgQValue = df; break; }
        }

        // DIA-NN per-row fragments: Fr.0.Id..Fr.11.Id (e.g. "y13^1/957.511230")
        // and Fr.0.Quantity..Fr.11.Quantity. Read what's present; missing
        // slots are tolerated (older reports / sparse rows).
        var fFragIds = new DataField[FragmentColumnCount];
        var fFragQty = new DataField[FragmentColumnCount];
        for (int k = 0; k < FragmentColumnCount; k++)
        {
            foreach (var df in schema.DataFields)
            {
                if (df.Name == $"Fr.{k}.Id") fFragIds[k] = df;
                else if (df.Name == $"Fr.{k}.Quantity") fFragQty[k] = df;
            }
        }

        // Dedup state: precursor id -> best (highest-quantity) row so far.
        var best = new Dictionary<string, DiannRow>();
        var bestQty = new Dictionary<string, double>();

        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rg = reader.OpenRowGroupReader(g);
            var run = (string[])(await rg.ReadColumnAsync(fRun, cancellationToken)).Data;
            var precursorIds = (string[])(await rg.ReadColumnAsync(fPrecursorId, cancellationToken)).Data;
            var modSeq = (string[])(await rg.ReadColumnAsync(fModSeq, cancellationToken)).Data;
            var stripSeq = (string[])(await rg.ReadColumnAsync(fStripSeq, cancellationToken)).Data;
            var charge = ReadLongArray(await rg.ReadColumnAsync(fCharge, cancellationToken));
            var mz = ReadDoubleArray(await rg.ReadColumnAsync(fMz, cancellationToken));
            var rt = ReadDoubleArray(await rg.ReadColumnAsync(fRt, cancellationToken));
            var rtStart = ReadDoubleArray(await rg.ReadColumnAsync(fRtStart, cancellationToken));
            var rtStop = ReadDoubleArray(await rg.ReadColumnAsync(fRtStop, cancellationToken));
            var quantity = ReadDoubleArray(await rg.ReadColumnAsync(fQuantity, cancellationToken));
            var proteotypic = ReadLongArray(await rg.ReadColumnAsync(fProteotypic, cancellationToken));
            var proteinIds = (string[])(await rg.ReadColumnAsync(fProteinIds, cancellationToken)).Data;
            var genes = (string[])(await rg.ReadColumnAsync(fGenes, cancellationToken)).Data;
            var qValue = ReadDoubleArray(await rg.ReadColumnAsync(fQValue, cancellationToken));
            var decoy = ReadLongArray(await rg.ReadColumnAsync(fDecoy, cancellationToken));
            double[] pgQValue = fPgQValue is null
                ? new double[qValue.Length]
                : ReadDoubleArray(await rg.ReadColumnAsync(fPgQValue, cancellationToken));
            if (fPgQValue is null) Array.Fill(pgQValue, double.NaN);

            // Read fragment columns once per row group so we don't open
            // each one per row.
            var fragIdCols = new string[FragmentColumnCount][];
            var fragQtyCols = new double[FragmentColumnCount][];
            for (int k = 0; k < FragmentColumnCount; k++)
            {
                fragIdCols[k] = fFragIds[k] is null
                    ? Array.Empty<string>()
                    : (string[])(await rg.ReadColumnAsync(fFragIds[k], cancellationToken)).Data;
                fragQtyCols[k] = fFragQty[k] is null
                    ? Array.Empty<double>()
                    : ReadDoubleArray(await rg.ReadColumnAsync(fFragQty[k], cancellationToken));
            }

            int n = precursorIds.Length;
            for (int i = 0; i < n; i++)
            {
                if (decoy[i] != 0) continue;
                if (qValue[i] >= qValueCutoff) continue;

                string id = precursorIds[i];
                double q = quantity[i];
                if (bestQty.TryGetValue(id, out double prevQty) && prevQty >= q) continue;

                bestQty[id] = q;
                best[id] = new DiannRow
                {
                    PrecursorId = id,
                    ModifiedSequence = modSeq[i],
                    StrippedSequence = stripSeq[i],
                    PrecursorCharge = (int)charge[i],
                    PrecursorMz = mz[i],
                    RtApex = rt[i],
                    RtStart = rtStart[i],
                    RtStop = rtStop[i],
                    PrecursorQuantity = q,
                    Proteotypic = (int)proteotypic[i],
                    ProteinIds = proteinIds[i],
                    Genes = genes[i],
                    Run = run[i],
                    QValue = qValue[i],
                    ProteinQValue = pgQValue[i],
                    Fragments = ExtractTopFragments(fragIdCols, fragQtyCols, i),
                };
            }
        }

        return best.Values.ToList();
    }

    /// <summary>
    /// Builds the peptide -> proteins map needed by
    /// <see cref="Parsimony.ParsimonyEngine.Assign"/>. Each peptide's protein
    /// list is the union of the semicolon-separated <c>Protein.Ids</c> values
    /// across all detections of that peptide.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<string>> BuildPeptideProteinMap(
        IEnumerable<DiannRow> rows)
    {
        var byPeptide = new Dictionary<string, HashSet<string>>();
        foreach (var row in rows)
        {
            if (!byPeptide.TryGetValue(row.StrippedSequence, out var set))
            {
                set = new HashSet<string>();
                byPeptide[row.StrippedSequence] = set;
            }
            foreach (var prot in row.ProteinIds.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                set.Add(prot.Trim());
            }
        }
        return byPeptide.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToList());
    }

    /// <summary>
    /// Parse the up-to-12 DIA-NN fragments for one row, keeping the top-N
    /// by quantity and returning them as <see cref="FragmentIon"/>
    /// records sorted by intensity descending.
    /// </summary>
    /// <remarks>
    /// Fragment ids are formatted <c>&lt;ion&gt;^&lt;charge&gt;/&lt;mz&gt;</c>
    /// (e.g. <c>y13^1/957.511230</c>); the charge is between the caret
    /// and slash, the m/z is everything after the final <c>/</c>. The
    /// fragment quantity from the parallel <c>Fr.N.Quantity</c> column
    /// is the intensity. Empty or unparseable slots are dropped.
    /// </remarks>
    private static FragmentIon[] ExtractTopFragments(string[][] ids, double[][] qtys, int rowIndex)
    {
        Span<double> mzBuf = stackalloc double[FragmentColumnCount];
        Span<double> qBuf = stackalloc double[FragmentColumnCount];
        Span<int> zBuf = stackalloc int[FragmentColumnCount];
        int n = 0;
        for (int k = 0; k < FragmentColumnCount; k++)
        {
            if (k >= ids.Length || ids[k].Length <= rowIndex) continue;
            string id = ids[k][rowIndex];
            if (string.IsNullOrEmpty(id)) continue;
            int slash = id.LastIndexOf('/');
            if (slash < 0 || slash + 1 >= id.Length) continue;
            if (!double.TryParse(id.AsSpan(slash + 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double mz)) continue;
            double qty = qtys.Length > k && qtys[k].Length > rowIndex ? qtys[k][rowIndex] : 0.0;
            if (qty <= 0) continue;

            // Charge: the int between '^' and '/'. Default to 1 if absent
            // or unparseable - safer than dropping the fragment.
            int charge = 1;
            int caret = id.IndexOf('^');
            if (caret >= 0 && caret + 1 < slash)
            {
                if (int.TryParse(id.AsSpan(caret + 1, slash - caret - 1),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int parsedZ) && parsedZ > 0)
                {
                    charge = parsedZ;
                }
            }

            mzBuf[n] = mz;
            qBuf[n] = qty;
            zBuf[n] = charge;
            n++;
        }
        if (n == 0) return Array.Empty<FragmentIon>();

        // Selection sort the top-N by quantity descending - n <= 12 makes
        // this trivial and avoids allocating an index array.
        int take = Math.Min(TopFragmentsKept, n);
        for (int i = 0; i < take; i++)
        {
            int best = i;
            for (int j = i + 1; j < n; j++)
                if (qBuf[j] > qBuf[best]) best = j;
            if (best != i)
            {
                (mzBuf[i], mzBuf[best]) = (mzBuf[best], mzBuf[i]);
                (qBuf[i], qBuf[best]) = (qBuf[best], qBuf[i]);
                (zBuf[i], zBuf[best]) = (zBuf[best], zBuf[i]);
            }
        }
        var top = new FragmentIon[take];
        for (int i = 0; i < take; i++) top[i] = new FragmentIon(mzBuf[i], qBuf[i], zBuf[i]);
        return top;
    }

    private static DataField GetField(ParquetSchema schema, string name)
    {
        foreach (var df in schema.DataFields)
        {
            if (df.Name == name) return df;
        }
        throw new InvalidDataException($"DIA-NN parquet is missing required column '{name}'.");
    }

    private static double[] ReadDoubleArray(Parquet.Data.DataColumn col)
    {
        if (col.Data is double[] d) return d;
        if (col.Data is float[] f)
        {
            var r = new double[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = f[i];
            return r;
        }
        throw new InvalidDataException(
            $"Column '{col.Field.Name}' has type {col.Data.GetType().Name}; expected double or float.");
    }

    private static long[] ReadLongArray(Parquet.Data.DataColumn col)
    {
        if (col.Data is long[] l) return l;
        if (col.Data is int[] i)
        {
            var r = new long[i.Length];
            for (int k = 0; k < i.Length; k++) r[k] = i[k];
            return r;
        }
        throw new InvalidDataException(
            $"Column '{col.Field.Name}' has type {col.Data.GetType().Name}; expected long or int.");
    }
}
