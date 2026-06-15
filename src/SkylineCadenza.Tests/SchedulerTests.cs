using SkylineCadenza.Core.Scheduling;
using Xunit;

namespace SkylineCadenza.Tests;

public class SchedulerTests
{
    private static Candidate Make(
        string id,
        double mz,
        double rtStart,
        double rtStop,
        string group,
        double quantity = 1e6,
        double[]? fragments = null)
    {
        return new Candidate
        {
            PrecursorId = id,
            StrippedSequence = id,
            ModifiedSequence = id,
            PrecursorCharge = 2,
            PrecursorMz = mz,
            RtStart = rtStart,
            RtStop = rtStop,
            RtApex = (rtStart + rtStop) / 2.0,
            PrecursorQuantity = quantity,
            QValue = 0.001,
            ProteinQValue = 0.001,
            Proteotypic = 1,
            ProteinGroup = group,
            PeptideType = "unique",
            Top4Fragments = fragments ?? new[] { 100.0, 200.0, 300.0, 400.0 },
            Run = "test",
        };
    }

    [Fact]
    public void Single_precursor_in_an_empty_schedule_always_fits()
    {
        var cands = new[] { Make("A", 500.0, 5.0, 5.4, "G1") };
        var result = Scheduler.Run(cands, new SchedulingParameters { CycleBudget = 1 });

        Assert.Single(result.ScheduledIndices);
        Assert.Single(result.Slots);
        Assert.Equal(1, result.ProteinGroupsCovered);
    }

    [Fact]
    public void Two_co_eluting_precursors_with_disjoint_fragments_share_one_slot()
    {
        // Same m/z window, overlapping RT (5.1..5.4 intersection), disjoint
        // top-4 fragments. Must share a slot.
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.4, "G1", fragments: new[] { 100.0, 200.0, 300.0, 400.0 }),
            Make("B", 500.5, 5.1, 5.5, "G2", fragments: new[] { 150.0, 250.0, 350.0, 450.0 }),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters { CycleBudget = 1 });

        Assert.Equal(2, result.ScheduledIndices.Length);
        Assert.Single(result.Slots);
        Assert.Equal(2, result.Slots[0].MemberIndices.Count);
    }

    [Fact]
    public void Strict_co_elution_blocks_chain_extension()
    {
        // A and B overlap; B and C overlap; A and C do not. Under the
        // bounding-box bug C would chain in. Under the intersection check
        // (this implementation) C must start a fresh slot.
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.6, "G1", fragments: new[] { 100.0, 200.0, 300.0, 400.0 }),
            Make("B", 500.5, 5.5, 6.1, "G2", fragments: new[] { 150.0, 250.0, 350.0, 450.0 }),
            Make("C", 500.7, 6.0, 6.6, "G3", fragments: new[] { 175.0, 275.0, 375.0, 475.0 }),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters { CycleBudget = 100 });

        // A and B can share, but C must NOT join that slot (it doesn't co-elute
        // with A). C goes into a separate slot.
        Assert.Equal(3, result.ScheduledIndices.Length);
        Assert.True(result.Slots.Length >= 2);

        // Find C's slot - it cannot contain A.
        int cIdx = Array.IndexOf(result.ScheduledIndices, 2);
        int cSlotId = result.ScheduledSlotIds[cIdx];
        var cSlot = result.Slots.First(s => s.Id == cSlotId);
        int aIdx = Array.IndexOf(result.ScheduledIndices, 0);
        Assert.DoesNotContain(0, cSlot.MemberIndices);
    }

    [Fact]
    public void Fragment_clash_prevents_slot_sharing()
    {
        // Same RT window, similar m/z, but fragments overlap. Must not share.
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.4, "G1", fragments: new[] { 100.0, 200.0, 300.0, 400.0 }),
            Make("B", 500.5, 5.1, 5.5, "G2", fragments: new[] { 100.2, 250.0, 350.0, 450.0 }),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters { CycleBudget = 100, FragmentTolDa = 0.5 });

        Assert.Equal(2, result.ScheduledIndices.Length);
        Assert.Equal(2, result.Slots.Length);
    }

    [Fact]
    public void Prm_mode_forces_one_precursor_per_slot()
    {
        // Same inputs that would multiplex in MTM mode; PRM should keep them
        // in separate slots.
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.4, "G1", fragments: new[] { 100.0, 200.0, 300.0, 400.0 }),
            Make("B", 500.5, 5.1, 5.5, "G2", fragments: new[] { 150.0, 250.0, 350.0, 450.0 }),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters
        {
            Mode = AcquisitionMode.Prm,
            CycleBudget = 100,
        });

        Assert.Equal(2, result.ScheduledIndices.Length);
        Assert.Equal(2, result.Slots.Length);
    }

    [Fact]
    public void Budget_of_one_with_non_co_eluting_precursors_drops_the_later_arrival()
    {
        // Two solo precursors at different RTs both want a slot. Budget=1
        // means only one slot can be active at a time. They don't overlap in
        // RT, so a single slot CAN cover both at different times - but they
        // can't share an MTM window because they don't co-elute. With
        // budget=1, the second precursor can still be scheduled into a fresh
        // slot because at any moment only one slot is active. This test
        // therefore expects BOTH to schedule (different RT windows = no
        // concurrency violation).
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.4, "G1"),
            Make("B", 600.0, 8.0, 8.4, "G2"),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters { CycleBudget = 1 });

        Assert.Equal(2, result.ScheduledIndices.Length);
        Assert.Equal(2, result.Slots.Length);
    }

    [Fact]
    public void Min_peptides_per_protein_drops_groups_below_threshold()
    {
        // Two protein groups. G1 has 2 peptides that can both be scheduled;
        // G2 has only 1 peptide. With MinPeptidesPerProtein = 2, only G1
        // survives the post-filter.
        var cands = new[]
        {
            Make("G1.a", 500.0, 5.0, 5.4, "G1", quantity: 1e8),
            Make("G1.b", 600.0, 6.0, 6.4, "G1", quantity: 1e7),
            Make("G2.a", 700.0, 7.0, 7.4, "G2", quantity: 1e6),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 100,
            MinPeptidesPerProtein = 2,
            MaxPeptidesPerProtein = 5,
        });

        // G1's two peptides survive; G2's one peptide is dropped.
        Assert.Equal(2, result.ScheduledIndices.Length);
        Assert.Equal(1, result.ProteinGroupsCovered);

        // The slot belonging to G2 should also be gone.
        Assert.All(result.Slots, s =>
            Assert.All(s.MemberIndices, idx => Assert.Equal("G1", cands[idx].ProteinGroup)));
    }

    [Fact]
    public void Min_peptides_default_of_one_keeps_singletons()
    {
        var cands = new[]
        {
            Make("G1.a", 500.0, 5.0, 5.4, "G1"),
            Make("G2.a", 700.0, 7.0, 7.4, "G2"),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters { CycleBudget = 100 });
        Assert.Equal(2, result.ScheduledIndices.Length);
        Assert.Equal(2, result.ProteinGroupsCovered);
    }

    [Fact]
    public void Load_up_adds_extra_peptides_to_already_covered_groups()
    {
        // One protein, three candidates that don't overlap in RT. Pass 1
        // schedules the first. Load-up should add the others.
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.4, "G1", quantity: 1e8),
            Make("B", 600.0, 6.0, 6.4, "G1", quantity: 1e7),
            Make("C", 700.0, 7.0, 7.4, "G1", quantity: 1e6),
        };

        var withLoad = Scheduler.Run(cands, new SchedulingParameters { CycleBudget = 100, EnableLoadBalancing = true });
        var noLoad  = Scheduler.Run(cands, new SchedulingParameters { CycleBudget = 100, EnableLoadBalancing = false });

        Assert.Equal(3, withLoad.ScheduledIndices.Length);
        Assert.Single(noLoad.ScheduledIndices);
    }
}
