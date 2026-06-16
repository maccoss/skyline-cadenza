using SkylineCadenza.Core.Ingest;
using SkylineCadenza.Core.Parsimony;
using SkylineCadenza.Core.Scheduling;
using Xunit;
using Xunit.Abstractions;

namespace SkylineCadenza.Tests;

/// <summary>
/// End-to-end pipeline check against the real DIA-NN report.parquet and
/// cached Carafe top-4 parquet that live alongside the targeted-modeling
/// notebook. Skipped automatically on machines without those files.
/// </summary>
public class EndToEndPipelineTests
{
    private const string ReportPath = "/mnt/d/GitHub-Repo/maccoss/targeted-modeling/diann_project/report.parquet";
    private const string Top4CachePath = "/mnt/d/GitHub-Repo/maccoss/targeted-modeling/carafe_top4.parquet";

    private readonly ITestOutputHelper _output;
    public EndToEndPipelineTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Reads_real_data_runs_parsimony_and_scheduler()
    {
        if (!File.Exists(ReportPath))
        {
            _output.WriteLine($"Reference data missing - skipping. ReportPath={ReportPath}");
            return;
        }

        var swDiann = System.Diagnostics.Stopwatch.StartNew();
        var rows = await DiannParquetReader.LoadAsync(ReportPath, qValueCutoff: 0.01);
        swDiann.Stop();
        _output.WriteLine($"DIA-NN rows after dedupe: {rows.Count:n0} (in {swDiann.ElapsedMilliseconds} ms)");
        Assert.True(rows.Count > 50_000, $"Expected >50k filtered precursors, got {rows.Count}");

        var swPars = System.Diagnostics.Stopwatch.StartNew();
        var pepMap = DiannParquetReader.BuildPeptideProteinMap(rows);
        var parsimony = ParsimonyEngine.Assign(pepMap);
        swPars.Stop();
        int uniqueCount = parsimony.Count(kv => kv.Value.Type == "unique");
        int razorCount = parsimony.Count(kv => kv.Value.Type == "razor");
        int groupCount = parsimony.Values.Select(v => v.Group).Distinct().Count();
        _output.WriteLine($"Parsimony: {parsimony.Count:n0} peptides ({uniqueCount:n0} unique, {razorCount:n0} razor) into {groupCount:n0} groups (in {swPars.ElapsedMilliseconds} ms)");
        Assert.True(groupCount > 3_000, $"Expected >3k protein groups, got {groupCount}");

        // The Python notebook's carafe_top4.parquet was written by pyarrow
        // with a metadata field that Parquet.Net 5.x can't parse ("Set"
        // type). The production tool maintains its own cache - written by
        // our SaveAsync, which Parquet.Net round-trips fine. For this
        // integration check, use synthetic fragments derived from the
        // precursor m/z so the scheduler runs end-to-end without depending
        // on the pyarrow-format cache.
        var frags = new Dictionary<FragmentKey, FragmentIon[]>();
        foreach (var row in rows)
        {
            var key = new FragmentKey(
                CarafeKey.FromDiann(row.ModifiedSequence),
                row.PrecursorCharge);
            // 4 synthetic fragments spread around the precursor m/z with
            // descending synthetic intensities and charge 1. The spacing
            // is wide enough that two different precursors get different
            // fragment families when their m/z differs.
            double seed = row.PrecursorMz;
            frags[key] = new[]
            {
                new FragmentIon(Math.Round(seed * 0.5 + 100.0, 4), 4.0, 1),
                new FragmentIon(Math.Round(seed * 0.5 + 200.0, 4), 3.0, 1),
                new FragmentIon(Math.Round(seed * 0.5 + 300.0, 4), 2.0, 1),
                new FragmentIon(Math.Round(seed * 0.5 + 400.0, 4), 1.0, 1),
            };
        }
        _output.WriteLine($"Synthetic fragments: {frags.Count:n0} keys");

        var swCand = System.Diagnostics.Stopwatch.StartNew();
        var candidates = CandidateBuilder.Build(rows, parsimony, frags);
        swCand.Stop();
        int withFrags = candidates.Count(c => c.Fragments.Length > 0);
        _output.WriteLine($"Candidates: {candidates.Count:n0} total, {withFrags:n0} with fragments (in {swCand.ElapsedMilliseconds} ms)");
        Assert.True(candidates.Count > 50_000);

        var swSched = System.Diagnostics.Stopwatch.StartNew();
        var result = Scheduler.Run(candidates, new SchedulingParameters
        {
            CycleBudget = 100,
            IsolationWindowTh = 3.0,
            FragmentTolDa = 0.5,
            FiringPadSec = 15.0,
        });
        swSched.Stop();
        _output.WriteLine($"Scheduled: {result.ScheduledIndices.Length:n0} precursors / {result.ProteinGroupsCovered:n0} groups / {result.Slots.Length:n0} slots (in {swSched.ElapsedMilliseconds} ms)");
        Assert.True(result.ScheduledIndices.Length > 1_000, "Expected >1k scheduled precursors");
        Assert.True(result.ProteinGroupsCovered > 1_000, "Expected >1k protein groups covered");
        Assert.True(result.SlotCountCurve.Max() <= 100, "Peak load must not exceed budget");
    }
}
