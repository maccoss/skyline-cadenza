using SkylineCadenza.Core.Ingest;
using Xunit;

namespace SkylineCadenza.Tests;

public class BlibRetentionTimeReaderTests
{
    // Fixture facts established when testdata/sample.blib was built from
    // the SEA-AD MTG library (see tools/build-blib-fixture.py):
    //
    //   12 peptides, 6 source replicates, 56 RetentionTimes rows.
    //   ScoreType: GENERIC Q-VALUE (lower is better).
    //   Per-replicate score range: 3.8e-5 .. 8.7e-3.
    //   AAAAAAAAAAAAAAAGAGAGAK+2 has 6 replicates, union RT [13.12, 13.37].
    //   AAANFSDR+2 has 2 replicates, union RT [4.82, 5.12].
    private static string FixturePath() =>
        Path.Combine(AppContext.BaseDirectory, "testdata", "sample.blib");

    [Fact]
    public void Read_AggregatesUnionAcrossReplicates_ForKnownPeptide()
    {
        var lib = BlibRetentionTimeReader.Read(FixturePath(), qValueCutoff: 0.01);

        Assert.True(lib.Boundaries.TryGetValue(("AAAAAAAAAAAAAAAGAGAGAK", 2), out var b));
        Assert.NotNull(b);
        // Min start across 6 replicates, max end across 6 replicates.
        Assert.Equal(13.12, b!.RtStart, precision: 2);
        Assert.Equal(13.37, b.RtStop, precision: 2);
        Assert.Equal(6, b.ReplicateCount);
    }

    [Fact]
    public void Read_AggregatesUnionAcrossReplicates_ForLightlyCoveredPeptide()
    {
        var lib = BlibRetentionTimeReader.Read(FixturePath(), qValueCutoff: 0.01);

        Assert.True(lib.Boundaries.TryGetValue(("AAANFSDR", 2), out var b));
        Assert.NotNull(b);
        Assert.Equal(4.82, b!.RtStart, precision: 2);
        Assert.Equal(5.12, b.RtStop, precision: 2);
        Assert.Equal(2, b.ReplicateCount);
    }

    [Fact]
    public void Read_ReturnsTwelveCanonicalPeptides_FromFixture()
    {
        var lib = BlibRetentionTimeReader.Read(FixturePath(), qValueCutoff: 0.01);
        Assert.Equal(12, lib.Boundaries.Count);
    }

    [Fact]
    public void Read_VarianceScoreReflectsMeasuredLibrary()
    {
        var lib = BlibRetentionTimeReader.Read(FixturePath(), qValueCutoff: 0.01);
        // Every peptide in the fixture has end-start spread > 0.01 min;
        // measured DIA-NN library, so variance score == 1.0.
        Assert.Equal(1.0, lib.VarianceScore, precision: 2);
    }

    [Fact]
    public void Read_HighQValueCutoff_KeepsAllReplicates()
    {
        // Cutoff well above the max per-replicate score (8.7e-3): no
        // rows are dropped, so replicate counts equal the unfiltered
        // total.
        var lib = BlibRetentionTimeReader.Read(FixturePath(), qValueCutoff: 0.5);
        Assert.True(lib.Boundaries.TryGetValue(("AAAAAAAAAAAAAAAGAGAGAK", 2), out var b));
        Assert.Equal(6, b!.ReplicateCount);
    }

    [Fact]
    public void Read_TightQValueCutoff_DropsHigherScoreReplicates()
    {
        // Cutoff = 0.001 sits in the middle of the per-replicate score
        // distribution (3.8e-5 .. 8.7e-3). At least one peptide should
        // see a reduced replicate count or tighter union window vs. the
        // permissive case.
        var permissive = BlibRetentionTimeReader.Read(FixturePath(), qValueCutoff: 0.5);
        var tight = BlibRetentionTimeReader.Read(FixturePath(), qValueCutoff: 0.001);

        bool foundTighter = false;
        foreach (var (key, looseB) in permissive.Boundaries)
        {
            if (!tight.Boundaries.TryGetValue(key, out var tightB)) continue;
            if (tightB.ReplicateCount < looseB.ReplicateCount
                || (tightB.RtStop - tightB.RtStart) < (looseB.RtStop - looseB.RtStart) - 1e-9)
            {
                foundTighter = true;
                break;
            }
        }
        Assert.True(foundTighter,
            "Tighter cutoff should reduce replicate count or shrink window for at least one peptide.");
    }

    [Fact]
    public void Read_PathDoesNotExist_Throws()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "testdata", "no-such-file.blib");
        Assert.Throws<FileNotFoundException>(() => BlibRetentionTimeReader.Read(missing, 0.01));
    }
}
