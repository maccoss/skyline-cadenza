using System.Text.RegularExpressions;

namespace SkylineCadenza.Core.Scheduling;

/// <summary>
/// Per-RT and per-(m/z, RT) summaries of a candidate library, used by the
/// UI's "Per-run coverage" and "m/z x RT heatmap" tabs.
/// </summary>
/// <remarks>
/// Port of the analyses in cells <c>8b959e76</c> (overlay curve) and
/// <c>750d53aa</c> (heatmap) of the targeted-modeling notebook. Decoupled
/// from <see cref="Scheduler"/> because these views describe the library
/// itself, not the schedule.
/// </remarks>
public static class CoverageCurves
{
    private static readonly Regex GpfPattern = new(@"Chrlib(\d+)-(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// Pretty label for a DIA-NN run name. Returns the
    /// <c>"&lt;low&gt;-&lt;high&gt; m/z"</c> token if the run is a GPF
    /// acquisition, otherwise the run name itself.
    /// </summary>
    public static string RunLabel(string runName)
    {
        var m = GpfPattern.Match(runName ?? "");
        return m.Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value} m/z" : runName ?? "";
    }

    /// <summary>
    /// Sort key for run labels - GPF labels by their low-mz bound, others
    /// alphabetical.
    /// </summary>
    public static IComparable RunSortKey(string label)
    {
        var m = GpfPattern.Match(label ?? "");
        if (m.Success) return int.Parse(m.Groups[1].Value);
        return int.MaxValue;
    }

    public sealed record CurveSeries(string Label, double[] Rt, int[] Counts);

    /// <summary>
    /// Build one coverage curve per distinct <see cref="Candidate.Run"/>. A
    /// candidate contributes to a sample RT <c>t</c> if its peak range
    /// <c>[RtStart, RtStop]</c> covers <c>t</c>.
    /// </summary>
    public static CurveSeries[] PerRunCurves(
        IReadOnlyList<Candidate> candidates,
        double rtGridStepMin = 0.05)
    {
        if (candidates.Count == 0) return Array.Empty<CurveSeries>();

        double rtLo = double.PositiveInfinity, rtHi = double.NegativeInfinity;
        var byRun = new Dictionary<string, (List<double> starts, List<double> stops)>();
        foreach (var c in candidates)
        {
            var label = RunLabel(c.Run);
            if (!byRun.TryGetValue(label, out var lists))
            {
                lists = (new List<double>(), new List<double>());
                byRun[label] = lists;
            }
            lists.starts.Add(c.RtStart);
            lists.stops.Add(c.RtStop);
            if (c.RtStart < rtLo) rtLo = c.RtStart;
            if (c.RtStop > rtHi) rtHi = c.RtStop;
        }
        if (double.IsPositiveInfinity(rtLo)) return Array.Empty<CurveSeries>();

        int n = (int)Math.Ceiling((rtHi - rtLo) / rtGridStepMin) + 1;
        var grid = new double[n];
        for (int i = 0; i < n; i++) grid[i] = rtLo + i * rtGridStepMin;

        var result = new List<CurveSeries>(byRun.Count);
        foreach (var (label, lists) in byRun)
        {
            var starts = lists.starts.ToArray();
            var stops = lists.stops.ToArray();
            Array.Sort(starts);
            Array.Sort(stops);
            var counts = new int[n];
            // Sweep: count = (#starts <= t) - (#stops < t).
            for (int i = 0; i < n; i++)
                counts[i] = UpperBound(starts, grid[i]) - LowerBound(stops, grid[i]);
            result.Add(new CurveSeries(label, grid, counts));
        }
        result.Sort((a, b) => Comparer<IComparable>.Default.Compare(RunSortKey(a.Label), RunSortKey(b.Label)));
        return result.ToArray();
    }

    public sealed record Heatmap(
        double MzLow, double MzBinTh,
        double RtLow, double RtBinMin,
        int[,] Counts);

    /// <summary>
    /// Bin candidates into a 2-D (m/z, RT) grid. A candidate contributes one
    /// count to every RT bin its <c>[RtStart, RtStop]</c> covers at its m/z
    /// row. Matches the notebook's "Precursors per spectrum" heatmap.
    /// </summary>
    public static Heatmap BuildHeatmap(
        IReadOnlyList<Candidate> candidates,
        double mzBinTh = 2.0,
        double rtBinMin = 0.1)
    {
        if (candidates.Count == 0)
            return new Heatmap(0, mzBinTh, 0, rtBinMin, new int[0, 0]);

        double mzLo = double.PositiveInfinity, mzHi = double.NegativeInfinity;
        double rtLo = double.PositiveInfinity, rtHi = double.NegativeInfinity;
        foreach (var c in candidates)
        {
            if (c.PrecursorMz < mzLo) mzLo = c.PrecursorMz;
            if (c.PrecursorMz > mzHi) mzHi = c.PrecursorMz;
            if (c.RtStart < rtLo) rtLo = c.RtStart;
            if (c.RtStop > rtHi) rtHi = c.RtStop;
        }
        mzLo = Math.Floor(mzLo);
        mzHi = Math.Ceiling(mzHi);
        int nMz = Math.Max(1, (int)Math.Ceiling((mzHi - mzLo) / mzBinTh));
        int nRt = Math.Max(1, (int)Math.Ceiling((rtHi - rtLo) / rtBinMin));
        var counts = new int[nMz, nRt];
        foreach (var c in candidates)
        {
            int mzIdx = (int)((c.PrecursorMz - mzLo) / mzBinTh);
            if (mzIdx < 0) mzIdx = 0;
            if (mzIdx >= nMz) mzIdx = nMz - 1;
            int colStart = (int)((c.RtStart - rtLo) / rtBinMin);
            int colStop = (int)((c.RtStop - rtLo) / rtBinMin) + 1;
            if (colStart < 0) colStart = 0;
            if (colStop > nRt) colStop = nRt;
            for (int j = colStart; j < colStop; j++) counts[mzIdx, j]++;
        }
        return new Heatmap(mzLo, mzBinTh, rtLo, rtBinMin, counts);
    }

    private static int UpperBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (sorted[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
