using System.Security.Cryptography;
using Parquet;
using Parquet.Schema;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Reads and writes a compact per-precursor top-N fragment table in the
/// same parquet schema the Python notebook produces as
/// <c>carafe_top4.parquet</c> (cell <c>dfead2c9</c>): one row per fragment
/// with columns <c>ModifiedPeptide</c>, <c>PrecursorCharge</c>,
/// <c>FragmentMz</c>, <c>RelativeIntensity</c>.
/// </summary>
/// <remarks>
/// The cache exists so we don't re-stream the 2.6 GB Carafe TSV on every
/// launch. The notebook produces these files routinely; reading them here
/// lets users reuse a cache built by either tool.
/// </remarks>
public static class Top4Cache
{
    /// <summary>
    /// Builds a deterministic cache key from a source TSV's path, byte length
    /// and last-write timestamp. Different libraries get different cache
    /// files; an in-place re-export to the same path invalidates the cache.
    /// </summary>
    public static string ComputeKey(string sourceTsvPath)
    {
        var fi = new FileInfo(sourceTsvPath);
        if (!fi.Exists)
            throw new FileNotFoundException("Cannot hash missing file", sourceTsvPath);

        string mat = $"{Path.GetFullPath(sourceTsvPath)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        var hash = SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(mat));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Default cache directory at <c>%LOCALAPPDATA%\SkylineCadenza\cache\</c>.
    /// </summary>
    public static string DefaultCacheDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "SkylineCadenza", "cache");
    }

    /// <summary>
    /// Reads a top-N parquet (as produced by <see cref="SaveAsync"/>) into
    /// the <c>(peptide, charge) -&gt; FragmentIon[]</c> shape downstream
    /// consumers expect. The parquet must carry <c>FragmentIntensity</c>
    /// and <c>FragmentCharge</c> columns alongside <c>FragmentMz</c>;
    /// older single-column caches written before the FragmentIon model
    /// will fail to load and need to be regenerated.
    /// </summary>
    public static async Task<Dictionary<FragmentKey, FragmentIon[]>> LoadAsync(
        string parquetPath, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(parquetPath);
        using var reader = await ParquetReader.CreateAsync(stream, cancellationToken: cancellationToken);

        var schema = reader.Schema;
        var fPep = GetField(schema, "ModifiedPeptide");
        var fCharge = GetField(schema, "PrecursorCharge");
        var fMz = GetField(schema, "FragmentMz");
        var fIntensity = GetField(schema, "FragmentIntensity");
        var fFragCharge = GetField(schema, "FragmentCharge");

        var perKey = new Dictionary<FragmentKey, List<FragmentIon>>();
        for (int g = 0; g < reader.RowGroupCount; g++)
        {
            using var rg = reader.OpenRowGroupReader(g);
            var pep = (string[])(await rg.ReadColumnAsync(fPep, cancellationToken)).Data;
            var charge = ReadLongArray(await rg.ReadColumnAsync(fCharge, cancellationToken));
            var mz = ReadDoubleArray(await rg.ReadColumnAsync(fMz, cancellationToken));
            var intensity = ReadDoubleArray(await rg.ReadColumnAsync(fIntensity, cancellationToken));
            var fragCharge = ReadLongArray(await rg.ReadColumnAsync(fFragCharge, cancellationToken));

            for (int i = 0; i < pep.Length; i++)
            {
                var key = new FragmentKey(pep[i], (int)charge[i]);
                if (!perKey.TryGetValue(key, out var list))
                {
                    list = new List<FragmentIon>(Candidate.FragmentLimit);
                    perKey[key] = list;
                }
                list.Add(new FragmentIon(mz[i], intensity[i], (int)fragCharge[i]));
            }
        }

        var result = new Dictionary<FragmentKey, FragmentIon[]>(perKey.Count);
        foreach (var (key, vals) in perKey)
        {
            // Sort by intensity descending so downstream consumers can
            // take the prefix they need (top-4 for clash, top-6 for write).
            vals.Sort(static (a, b) => b.Intensity.CompareTo(a.Intensity));
            result[key] = vals.ToArray();
        }
        return result;
    }

    /// <summary>
    /// Writes a long-format parquet for the given fragment map. Columns:
    /// <c>ModifiedPeptide</c>, <c>PrecursorCharge</c>, <c>FragmentMz</c>,
    /// <c>FragmentIntensity</c>, <c>FragmentCharge</c>. Row order is
    /// grouped by <c>(peptide, charge)</c> in the input dictionary's
    /// enumeration order.
    /// </summary>
    public static async Task SaveAsync(
        string parquetPath,
        IReadOnlyDictionary<FragmentKey, FragmentIon[]> fragments,
        CancellationToken cancellationToken = default)
    {
        int totalRows = 0;
        foreach (var (_, arr) in fragments) totalRows += arr.Length;

        var peps = new string[totalRows];
        var charges = new int[totalRows];
        var mzs = new double[totalRows];
        var intensities = new double[totalRows];
        var fragCharges = new int[totalRows];
        int idx = 0;
        foreach (var (key, arr) in fragments)
        {
            foreach (var ion in arr)
            {
                peps[idx] = key.ModifiedPeptide;
                charges[idx] = key.PrecursorCharge;
                mzs[idx] = ion.Mz;
                intensities[idx] = ion.Intensity;
                fragCharges[idx] = ion.Charge;
                idx++;
            }
        }

        var fPep = new DataField<string>("ModifiedPeptide");
        var fCharge = new DataField<int>("PrecursorCharge");
        var fMz = new DataField<double>("FragmentMz");
        var fIntensity = new DataField<double>("FragmentIntensity");
        var fFragCharge = new DataField<int>("FragmentCharge");
        var schema = new ParquetSchema(fPep, fCharge, fMz, fIntensity, fFragCharge);

        Directory.CreateDirectory(Path.GetDirectoryName(parquetPath)!);
        using var stream = File.Create(parquetPath);
        using var writer = await ParquetWriter.CreateAsync(schema, stream, cancellationToken: cancellationToken);
        using var rg = writer.CreateRowGroup();
        await rg.WriteColumnAsync(new Parquet.Data.DataColumn(fPep, peps), cancellationToken);
        await rg.WriteColumnAsync(new Parquet.Data.DataColumn(fCharge, charges), cancellationToken);
        await rg.WriteColumnAsync(new Parquet.Data.DataColumn(fMz, mzs), cancellationToken);
        await rg.WriteColumnAsync(new Parquet.Data.DataColumn(fIntensity, intensities), cancellationToken);
        await rg.WriteColumnAsync(new Parquet.Data.DataColumn(fFragCharge, fragCharges), cancellationToken);
    }

    private static DataField GetField(ParquetSchema schema, string name)
    {
        foreach (var df in schema.DataFields)
            if (df.Name == name) return df;
        throw new InvalidDataException($"Top-4 parquet is missing column '{name}'.");
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
