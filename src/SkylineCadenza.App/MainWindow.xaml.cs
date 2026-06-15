using System.ComponentModel;
using System.Windows;
using ScottPlot;
using SkylineCadenza.App.ViewModels;
using SkylineCadenza.Core.Scheduling;
using CoverageCurves = SkylineCadenza.Core.Scheduling.CoverageCurves;

namespace SkylineCadenza.App;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null)
            {
                _vm.ScheduleUpdated -= RefreshSchedulePlots;
                _vm.LibraryLoaded -= RefreshLibraryPlots;
                _vm.HeatmapViewChanged -= RebuildHeatmap;
            }
            _vm = e.NewValue as MainViewModel;
            if (_vm is not null)
            {
                _vm.ScheduleUpdated += RefreshSchedulePlots;
                _vm.LibraryLoaded += RefreshLibraryPlots;
                _vm.HeatmapViewChanged += RebuildHeatmap;
            }
        };
        if (DataContext is MainViewModel vm)
        {
            _vm = vm;
            vm.ScheduleUpdated += RefreshSchedulePlots;
            vm.LibraryLoaded += RefreshLibraryPlots;
            vm.HeatmapViewChanged += RebuildHeatmap;
        }
    }

    private void RefreshSchedulePlots()
    {
        if (_vm is null) return;
        RebuildLoadCurve(_vm.ScheduleResult, _vm.CycleBudget);
        RebuildCoveragePlot();
        RebuildOccupancyPlot();
        // The scheduled heatmap depends on the schedule; re-render if the
        // user has the scheduled view selected.
        if (_vm.HeatmapView == HeatmapView.ScheduledAssay) RebuildHeatmap();
    }

    private void RefreshLibraryPlots()
    {
        if (_vm is null) return;
        RebuildPerRunCurves();
        RebuildHeatmap();
    }

    private void RebuildLoadCurve(ScheduleResult? result, int budget)
    {
        var plt = LoadCurvePlot.Plot;
        plt.Clear();
        if (result is null || result.RtGrid.Length == 0)
        {
            LoadCurvePlot.Refresh();
            return;
        }

        var ys = new double[result.SlotCountCurve.Length];
        for (int i = 0; i < ys.Length; i++) ys[i] = result.SlotCountCurve[i];
        var line = plt.Add.Scatter(result.RtGrid, ys);
        line.LegendText = "Scheduled slots";
        line.LineWidth = 2.0f;

        var budgetX = new[] { result.RtGrid[0], result.RtGrid[^1] };
        var budgetY = new double[] { budget, budget };
        var budgetLine = plt.Add.Scatter(budgetX, budgetY);
        budgetLine.LegendText = $"Budget ({budget})";
        budgetLine.LinePattern = LinePattern.Dashed;
        budgetLine.Color = Colors.Red;
        budgetLine.LineWidth = 2.0f;

        // Scale the axes to the data + budget so the curve is always
        // visible. yMax = max(peak load, budget) * 1.1 with a sensible
        // floor; xMax/xMin track the RT range.
        double peak = 0;
        for (int i = 0; i < ys.Length; i++) if (ys[i] > peak) peak = ys[i];
        double yMax = Math.Max(peak, budget) * 1.1;
        if (yMax < 10) yMax = 10;
        plt.Axes.SetLimitsX(result.RtGrid[0], result.RtGrid[^1]);
        plt.Axes.SetLimitsY(0, yMax);

        plt.XLabel("Retention time (min)");
        plt.YLabel("Concurrent MTM slots");
        ApplyPlotStyle(plt);
        var legend = plt.ShowLegend(ScottPlot.Alignment.UpperRight);
        legend.FontSize = 16;
        LoadCurvePlot.Refresh();
    }

    /// <summary>
    /// Consistent axis / title font sizes across every plot.
    /// </summary>
    private static void ApplyPlotStyle(ScottPlot.Plot plt)
    {
        plt.Axes.Bottom.Label.FontSize = 20;
        plt.Axes.Left.Label.FontSize = 20;
        plt.Axes.Bottom.TickLabelStyle.FontSize = 15;
        plt.Axes.Left.TickLabelStyle.FontSize = 15;
        plt.Axes.Title.Label.FontSize = 18;
    }

    private void RebuildCoveragePlot()
    {
        var plt = CoveragePlot.Plot;
        plt.Clear();
        if (_vm is null || _vm.ProteinCoverage.Count == 0)
        {
            CoveragePlot.Refresh();
            return;
        }

        int n = _vm.ProteinCoverage.Count;

        // Color ramp keyed by peptides scheduled.
        Color For(int peptides) => peptides switch
        {
            0 => Color.FromHex("#cccccc"),
            1 => Color.FromHex("#b04c3a"),
            2 => Color.FromHex("#d68f3a"),
            3 => Color.FromHex("#a5b03a"),
            4 => Color.FromHex("#5fa83a"),
            _ => Color.FromHex("#1f6b3a"),
        };
        string Label(int peptides) => peptides switch
        {
            0 => "Not scheduled",
            5 => "5+ peptides",
            _ => $"{peptides} peptide{(peptides == 1 ? "" : "s")}",
        };

        // Bucket by peptide count so we can draw one scatter series per
        // color. Bar plots at this density (7k+ proteins) merge into a
        // solid mass; a scatter with small markers shows each point's
        // colour even when they overlap.
        var bucketX = new Dictionary<int, List<double>>();
        var bucketY = new Dictionary<int, List<double>>();
        double yMax = double.NegativeInfinity, yMin = double.PositiveInfinity;
        for (int i = 0; i < n; i++)
        {
            var row = _vm.ProteinCoverage[i];
            int bucket = Math.Min(row.PeptidesScheduled, 5);
            double y = row.SummedIntensity > 0 ? Math.Log10(row.SummedIntensity) : 0;
            if (!bucketX.TryGetValue(bucket, out var xs))
            {
                xs = new List<double>();
                bucketX[bucket] = xs;
                bucketY[bucket] = new List<double>();
            }
            xs.Add(i + 1);
            bucketY[bucket].Add(y);
            if (y > yMax) yMax = y;
            if (y < yMin) yMin = y;
        }

        // Draw in ascending bucket order so higher peptide counts paint on
        // top of the "not scheduled" greys (more visible).
        for (int bucket = 0; bucket <= 5; bucket++)
        {
            if (!bucketX.TryGetValue(bucket, out var xs)) continue;
            var ys = bucketY[bucket];
            var sc = plt.Add.ScatterPoints(xs.ToArray(), ys.ToArray());
            sc.Color = For(bucket);
            sc.MarkerSize = 7;
            sc.LegendText = $"{Label(bucket)} (n = {xs.Count:n0})";
        }

        plt.XLabel("Protein group rank (sorted by intensity)");
        plt.YLabel("log10(sum of peptide intensity)");
        plt.Title($"Protein coverage by peptides scheduled (n = {n:n0} groups)");

        ApplyPlotStyle(plt);

        plt.Axes.SetLimitsX(0, n + 1);
        double yLo = double.IsPositiveInfinity(yMin) ? 0 : Math.Max(0, yMin - 0.2);
        double yHi = double.IsNegativeInfinity(yMax) ? 1 : yMax + 0.2;
        plt.Axes.SetLimitsY(yLo, yHi);

        // Place the legend in the upper right - the curve runs from
        // upper-left to lower-right, so the upper-right corner is the
        // empty zone above the curve.
        var legend = plt.ShowLegend(ScottPlot.Alignment.UpperRight);
        legend.FontSize = 16;

        CoveragePlot.Refresh();
    }

    private void RebuildPerRunCurves()
    {
        var plt = PerRunPlot.Plot;
        plt.Clear();
        if (_vm is null || _vm.PerRunCurves.Length == 0)
        {
            PerRunPlot.Refresh();
            return;
        }

        // Color the curves via the default viridis-ish palette ScottPlot
        // exposes; when multiple GPF runs are present they read like a
        // gradient sorted by m/z window. For a single-run library only one
        // curve is drawn.
        var palette = new ScottPlot.Palettes.Category10();
        double yMax = 0, xMin = double.PositiveInfinity, xMax = double.NegativeInfinity;
        for (int i = 0; i < _vm.PerRunCurves.Length; i++)
        {
            var series = _vm.PerRunCurves[i];
            var ys = new double[series.Counts.Length];
            for (int j = 0; j < ys.Length; j++) ys[j] = series.Counts[j];
            var line = plt.Add.Scatter(series.Rt, ys);
            line.LegendText = series.Label;
            line.LineWidth = 1.4f;
            line.MarkerSize = 0;
            line.Color = palette.GetColor(i);
            foreach (var y in ys) if (y > yMax) yMax = y;
            if (series.Rt.Length > 0)
            {
                if (series.Rt[0] < xMin) xMin = series.Rt[0];
                if (series.Rt[^1] > xMax) xMax = series.Rt[^1];
            }
        }

        // Sum across all runs (the "all runs together" curve).
        if (_vm.PerRunCurves.Length > 1)
        {
            var ref0 = _vm.PerRunCurves[0];
            int len = ref0.Rt.Length;
            var sum = new double[len];
            for (int s = 0; s < _vm.PerRunCurves.Length; s++)
            {
                var cs = _vm.PerRunCurves[s].Counts;
                int common = Math.Min(len, cs.Length);
                for (int j = 0; j < common; j++) sum[j] += cs[j];
            }
            var total = plt.Add.Scatter(ref0.Rt, sum);
            total.LegendText = "All runs (sum)";
            total.LineWidth = 1.2f;
            total.LinePattern = LinePattern.Dashed;
            total.Color = Colors.Black;
            total.MarkerSize = 0;
            foreach (var y in sum) if (y > yMax) yMax = y;
        }

        plt.XLabel("Retention time (min)");
        plt.YLabel("Co-eluting precursors");
        plt.Title(_vm.PerRunCurves.Length == 1
            ? "Precursor elution coverage (single run)"
            : $"Precursor elution coverage by run ({_vm.PerRunCurves.Length} runs)");
        if (!double.IsInfinity(xMin) && !double.IsInfinity(xMax))
            plt.Axes.SetLimitsX(xMin, xMax);
        if (yMax > 0) plt.Axes.SetLimitsY(0, yMax * 1.1);
        ApplyPlotStyle(plt);
        var legend = plt.ShowLegend(ScottPlot.Alignment.UpperRight);
        legend.FontSize = 16;
        PerRunPlot.Refresh();
    }

    private void RebuildHeatmap()
    {
        // ScottPlot's ColorBar / Rectangle plottables attach as axis
        // panels or plottables that survive Plot.Clear() unpredictably.
        // Reset() swaps in a brand-new Plot so the rebuild is clean.
        HeatmapPlot.Reset();
        var plt = HeatmapPlot.Plot;
        if (_vm is null)
        {
            HeatmapPlot.Refresh();
            return;
        }

        // Branch on which view the user picked. The "scheduled isolation
        // windows" view shows each slot as a rectangle in (m/z, RT) space;
        // the "library density" view falls through to the binned heatmap.
        if (_vm.HeatmapView == HeatmapView.ScheduledAssay)
        {
            RenderScheduledWindows(plt);
            return;
        }

        if (_vm.Heatmap is null)
        {
            HeatmapPlot.Refresh();
            return;
        }

        var h = _vm.Heatmap;
        int rows = h.Counts.GetLength(0);
        int cols = h.Counts.GetLength(1);
        if (rows == 0 || cols == 0)
        {
            HeatmapPlot.Refresh();
            return;
        }

        // ScottPlot expects double[,]. Flip rows so low m/z is at the bottom.
        var data = new double[rows, cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                data[rows - 1 - r, c] = h.Counts[r, c];

        var heat = plt.Add.Heatmap(data);
        heat.Colormap = new ScottPlot.Colormaps.Viridis();
        heat.Extent = new ScottPlot.CoordinateRect(
            left: h.RtLow,
            right: h.RtLow + cols * h.RtBinMin,
            bottom: h.MzLow,
            top: h.MzLow + rows * h.MzBinTh);

        plt.Add.ColorBar(heat);
        plt.XLabel("Retention time (min)");
        plt.YLabel("Precursor m/z");
        plt.Title(_vm?.HeatmapTitle ?? "Precursors per spectrum");
        ApplyPlotStyle(plt);
        plt.Axes.SetLimits(h.RtLow, h.RtLow + cols * h.RtBinMin, h.MzLow, h.MzLow + rows * h.MzBinTh);
        HeatmapPlot.Refresh();
    }

    private void RenderScheduledWindows(ScottPlot.Plot plt)
    {
        if (_vm is null) return;
        var result = _vm.ScheduleResult;
        var slots = result?.Slots ?? Array.Empty<SkylineCadenza.Core.Scheduling.Slot>();
        if (slots.Length == 0)
        {
            plt.Title("Scheduled isolation windows (no schedule)");
            plt.XLabel("Retention time (min)");
            plt.YLabel("Precursor m/z");
            HeatmapPlot.Refresh();
            return;
        }

        // Color the rectangles by occupancy so the user can see MTM slot
        // sharing at a glance. PRM mode is always 1 member / slot so its
        // rectangles render almost opaque - the user wants distinct
        // windows, not a density wash. MTM keeps moderate alphas so
        // overlaps integrate visually.
        bool prmFills = _vm.Mode == SkylineCadenza.Core.Scheduling.AcquisitionMode.Prm;
        Color For(int members) => members switch
        {
            1 => Color.FromHex("#3a6fb0").WithAlpha(prmFills ? 0.95 : 0.55),
            2 => Color.FromHex("#3a8c5c").WithAlpha(0.65),
            3 => Color.FromHex("#d68f3a").WithAlpha(0.70),
            4 => Color.FromHex("#b04c3a").WithAlpha(0.75),
            _ => Color.FromHex("#7a2c8c").WithAlpha(0.80),
        };
        Color OutlineFor(int members) => members switch
        {
            1 => Color.FromHex("#1e3a5f"),
            2 => Color.FromHex("#1f5e3a"),
            3 => Color.FromHex("#8a591f"),
            4 => Color.FromHex("#6f2d20"),
            _ => Color.FromHex("#4a1a59"),
        };

        double prm = _vm.PrmIsolationWidthTh;
        double half = prm / 2.0;

        double mzMin = double.PositiveInfinity, mzMax = double.NegativeInfinity;
        double rtMin = double.PositiveInfinity, rtMax = double.NegativeInfinity;
        // ScottPlot's bulk-rectangle path uses Add.Rectangle per item.
        // For a few thousand slots this is fast enough.
        foreach (var s in slots)
        {
            double leftMz = s.MzMin - half;
            double rightMz = s.MzMax + half;
            double bottomRt = s.RtStart;
            double topRt = s.RtStop;
            var fillColor = For(s.MemberIndices.Count);

            // Use a 4-point Polygon instead of Add.Rectangle - the
            // Rectangle plottable's FillStyle.Color setter doesn't latch
            // reliably in ScottPlot 5.0.34 (it falls back to the default
            // palette and rectangles end up rainbow-coloured). Polygons
            // respect FillStyle.Color cleanly.
            var poly = plt.Add.Polygon(new ScottPlot.Coordinates[]
            {
                new(bottomRt, leftMz),
                new(topRt, leftMz),
                new(topRt, rightMz),
                new(bottomRt, rightMz),
            });
            poly.FillStyle.Color = fillColor;
            poly.LineStyle.Color = OutlineFor(s.MemberIndices.Count);
            poly.LineStyle.Width = 0.8f;

            if (leftMz < mzMin) mzMin = leftMz;
            if (rightMz > mzMax) mzMax = rightMz;
            if (bottomRt < rtMin) rtMin = bottomRt;
            if (topRt > rtMax) rtMax = topRt;
        }

        // Legend stubs (invisible scatter dots) so the user gets a colour
        // key. Only show occupancy levels that actually appear.
        var occupancyLevels = slots.Select(s => Math.Min(5, s.MemberIndices.Count)).Distinct().OrderBy(x => x);
        foreach (var lvl in occupancyLevels)
        {
            var dot = plt.Add.Marker(double.NaN, double.NaN);
            dot.MarkerStyle.FillColor = For(lvl).WithAlpha(1.0);
            dot.MarkerStyle.OutlineColor = ScottPlot.Colors.Transparent;
            dot.MarkerStyle.Size = 12;
            dot.LegendText = lvl == 5 ? "5+ precursors / slot" : $"{lvl} precursor{(lvl == 1 ? "" : "s")} / slot";
        }

        bool isPrm = _vm.Mode == SkylineCadenza.Core.Scheduling.AcquisitionMode.Prm;
        string titleSuffix = isPrm
            ? $"{slots.Length:n0} slots; window {prm:0.0} Th"
            : $"{slots.Length:n0} slots; up to {_vm.IsolationWindowTh:0.0} Th cap, solo {prm:0.0} Th";
        plt.Title((isPrm ? "PRM" : "MTM") + " isolation windows (" + titleSuffix + ")");
        plt.XLabel("Retention time (min)");
        plt.YLabel("Precursor m/z");
        ApplyPlotStyle(plt);
        plt.Axes.SetLimits(rtMin, rtMax, mzMin - 1, mzMax + 1);
        var legend = plt.ShowLegend(ScottPlot.Alignment.UpperLeft);
        legend.FontSize = 16;
        HeatmapPlot.Refresh();
    }

    private void RebuildOccupancyPlot()
    {
        OccupancyPlot.Reset();
        var plt = OccupancyPlot.Plot;
        if (_vm?.ScheduleResult is null || _vm.ScheduleResult.Slots.Length == 0)
        {
            plt.Title("Slot occupancy (no schedule yet)");
            OccupancyPlot.Refresh();
            return;
        }

        var slots = _vm.ScheduleResult.Slots;
        int total = slots.Length;
        var memberCounts = new int[total];
        int maxMembers = 0;
        for (int i = 0; i < total; i++)
        {
            int n = slots[i].MemberIndices.Count;
            if (n < 1) n = 1; // guard against empty slot
            memberCounts[i] = n;
            if (n > maxMembers) maxMembers = n;
        }
        if (maxMembers < 1) maxMembers = 1;

        // Histogram counts[k] = number of slots with k members.
        var hist = new int[maxMembers + 1];
        for (int i = 0; i < total; i++) hist[memberCounts[i]]++;

        // Stats.
        int statMax = maxMembers;
        double statMedian = ComputeMedian(memberCounts);
        int statMode = 1;
        int statModeFreq = -1;
        for (int k = 1; k <= maxMembers; k++)
        {
            if (hist[k] > statModeFreq) { statModeFreq = hist[k]; statMode = k; }
        }

        Color For(int level)
        {
            return level switch
            {
                1 => Color.FromHex("#3a6fb0"),
                2 => Color.FromHex("#3a8c5c"),
                3 => Color.FromHex("#d68f3a"),
                4 => Color.FromHex("#b04c3a"),
                5 => Color.FromHex("#7a2c8c"),
                _ => DeepenPurple(level, maxMembers),
            };
        }

        var bars = new List<ScottPlot.Bar>();
        for (int k = 1; k <= maxMembers; k++)
        {
            double pct = total == 0 ? 0 : 100.0 * hist[k] / total;
            bars.Add(new ScottPlot.Bar
            {
                Position = k,
                Value = pct,
                FillColor = For(k),
                Size = 0.7,
                Label = $"{hist[k]:n0}",
            });
        }
        var bp = plt.Add.Bars(bars);
        bp.ValueLabelStyle.FontSize = 14;

        plt.XLabel("Precursors per slot");
        plt.YLabel("% of slots");
        string modeWord = _vm.Mode == SkylineCadenza.Core.Scheduling.AcquisitionMode.Prm ? "PRM" : "MTM";
        plt.Title(
            $"{modeWord} slot occupancy (n = {total:n0} slots; "
            + $"max = {statMax}, median = {statMedian:0.0}, mode = {statMode})");
        ApplyPlotStyle(plt);

        // Tick at every integer 1..maxMembers. Bottom-axis labels collapse
        // automatically when too dense; ScottPlot handles thinning.
        var ticks = new ScottPlot.Tick[maxMembers];
        for (int k = 1; k <= maxMembers; k++)
            ticks[k - 1] = new(k, k.ToString());
        plt.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
        plt.Axes.SetLimitsX(0.4, maxMembers + 0.6);
        double yMax = 0;
        for (int k = 1; k <= maxMembers; k++)
        {
            double pct = total > 0 ? 100.0 * hist[k] / total : 0;
            if (pct > yMax) yMax = pct;
        }
        plt.Axes.SetLimitsY(0, Math.Max(yMax * 1.15, 5));

        OccupancyPlot.Refresh();
    }

    /// <summary>
    /// Interpolate between the level-5 purple (#7a2c8c) and a near-black
    /// violet (#2a0c3c) for levels > 5; used so deeply-multiplexed slots
    /// remain visually distinguishable from the level-5 baseline.
    /// </summary>
    private static Color DeepenPurple(int level, int maxLevel)
    {
        int extra = Math.Max(0, level - 5);
        int maxExtra = Math.Max(1, maxLevel - 5);
        double t = Math.Min(1.0, extra / (double)maxExtra);
        int r = (int)Math.Round(0x7a - t * (0x7a - 0x2a));
        int g = (int)Math.Round(0x2c - t * (0x2c - 0x0c));
        int b = (int)Math.Round(0x8c - t * (0x8c - 0x3c));
        return Color.FromHex($"#{r:x2}{g:x2}{b:x2}");
    }

    private static double ComputeMedian(int[] values)
    {
        if (values.Length == 0) return 0;
        var sorted = (int[])values.Clone();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        if (sorted.Length % 2 == 1) return sorted[mid];
        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
