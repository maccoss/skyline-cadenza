using SkylineCadenza.Core.Output;
using SkylineCadenza.Core.Scheduling;
using Xunit;

namespace SkylineCadenza.Tests;

public class PeptideTransitionListBuilderTests
{
    private static Candidate Make(
        string seq, string modSeq, int z, double mz,
        double rtApex, string protein,
        params FragmentIon[] frags) =>
        new Candidate
        {
            PrecursorId = $"{seq}+{z}",
            StrippedSequence = seq,
            ModifiedSequence = modSeq,
            PrecursorCharge = z,
            PrecursorMz = mz,
            RtStart = rtApex - 0.25,
            RtStop = rtApex + 0.25,
            RtApex = rtApex,
            PrecursorQuantity = 1e6,
            QValue = 0.001,
            ProteinQValue = 0.001,
            Proteotypic = 1,
            ProteinGroup = protein,
            PeptideType = "unique",
            Fragments = frags,
            Top4Fragments = Candidate.DeriveTopMz(frags, 4),
            Run = "test",
        };

    private static ScheduleResult OneSlot(IReadOnlyList<Candidate> cands)
    {
        var slot = new Slot
        {
            Id = 0,
            MzMin = cands.Min(c => c.PrecursorMz),
            MzMax = cands.Max(c => c.PrecursorMz),
            RtStart = cands.Min(c => c.RtStart),
            RtStop = cands.Max(c => c.RtStop),
            CoStart = cands.Min(c => c.RtStart),
            CoStop = cands.Max(c => c.RtStop),
            Fragments = Array.Empty<double>(),
        };
        for (int i = 0; i < cands.Count; i++) slot.MemberIndices.Add(i);

        return new ScheduleResult
        {
            ScheduledIndices = Enumerable.Range(0, cands.Count).ToArray(),
            ScheduledSlotIds = new int[cands.Count], // all zeros = slot 0
            Slots = new[] { slot },
            RtGrid = Array.Empty<double>(),
            SlotCountCurve = Array.Empty<int>(),
            ProteinGroupsCovered = cands.Select(c => c.ProteinGroup).Distinct().Count(),
        };
    }

    [Fact]
    public void Build_EmitsRealProductCharge_PerFragment()
    {
        // Mix of +1 and +2 fragments - the previous hardcoded +1 builder
        // would have emitted +1 for both and Skyline would reject the +2
        // m/z values as "no matching product ion".
        var cands = new[]
        {
            Make("ASFNHFDK", "ASFNHFDK", 2, 471.71, 5.0, "P1",
                new FragmentIon(404.1928, 100.0, 2),  // y6+2
                new FragmentIon(447.7089, 80.0, 2)),  // y7+2
        };
        var schedule = OneSlot(cands);
        var csv = PeptideTransitionListBuilder.Build(cands, schedule);
        var lines = csv.Trim().Split('\n');

        // Header + 2 fragment rows.
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Protein Name,Peptide Modified Sequence,", lines[0]);

        // Each fragment row should report Product Charge = 2, not 1.
        var fields1 = lines[1].Split(',');
        var fields2 = lines[2].Split(',');
        Assert.Equal("404.1928", fields1[4]); // Product m/z
        Assert.Equal("2", fields1[5]);        // Product Charge
        Assert.Equal("447.7089", fields2[4]);
        Assert.Equal("2", fields2[5]);
    }

    [Fact]
    public void Build_LimitsToSixFragmentsPerPeptide()
    {
        var frags = Enumerable.Range(1, 10)
            .Select(i => new FragmentIon(100.0 + i, 20.0 - i, i % 2 == 0 ? 2 : 1))
            .ToArray();
        var cands = new[] { Make("MANYFRAG", "MANYFRAG", 2, 400.0, 5.0, "P1", frags) };
        var schedule = OneSlot(cands);
        var csv = PeptideTransitionListBuilder.Build(cands, schedule);

        int rowCount = csv.Trim().Split('\n').Length - 1; // exclude header
        Assert.Equal(PeptideTransitionListBuilder.FragmentsPerPeptide, rowCount);
    }

    [Fact]
    public void Build_FragmentlessPeptide_EmitsOnePrecursorOnlyRow()
    {
        var cands = new[] { Make("NOFRAGS", "NOFRAGS", 2, 400.0, 5.0, "P1") };
        var schedule = OneSlot(cands);
        var csv = PeptideTransitionListBuilder.Build(cands, schedule);
        var lines = csv.Trim().Split('\n');

        Assert.Equal(2, lines.Length); // header + 1 row
        var fields = lines[1].Split(',');
        Assert.Equal(string.Empty, fields[4]); // Product m/z blank
        Assert.Equal(string.Empty, fields[5]); // Product Charge blank
    }
}
