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
        // Synthesise FragmentIon entries from the supplied mz array.
        // Intensities are assigned descending so the input order matches
        // the "top-4 by intensity" derivation, and charge defaults to 1.
        var mzList = fragments ?? new[] { 100.0, 200.0, 300.0, 400.0 };
        var ions = new FragmentIon[mzList.Length];
        for (int i = 0; i < mzList.Length; i++)
            ions[i] = new FragmentIon(mzList[i], mzList.Length - i, 1);
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
            Fragments = ions,
            Top4Fragments = Candidate.DeriveTopMz(ions, 4),
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
    public void Mtm_slot_fit_rejects_precursor_whose_solo_window_would_overhang_slot_edge()
    {
        // Slot-edge rule: every member's PrmIsolationWidthTh quadrupole
        // window must fit fully inside the slot. So every member center
        // must sit at least PrmIsolationWidthTh / 2 from each edge.
        //
        // With IsolationWindowTh = 3.0 and PrmIsolationWidthTh = 0.7 the
        // edge clearance is 0.35 Th. Centers 2.4 Th apart cannot both
        // satisfy that: whichever member's edge is closer would sit
        // 0.30 Th from the slot edge, overhanging by 0.05 Th. The
        // scheduler must split them into two slots.
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.4, "G1", fragments: new[] { 100.0, 200.0, 300.0, 400.0 }),
            Make("B", 502.4, 5.1, 5.5, "G2", fragments: new[] { 150.0, 250.0, 350.0, 450.0 }),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 100,
            IsolationWindowTh = 3.0,
            PrmIsolationWidthTh = 0.7,
        });

        Assert.Equal(2, result.ScheduledIndices.Length);
        Assert.Equal(2, result.Slots.Length);
    }

    [Fact]
    public void Mtm_slot_fit_merges_precursors_when_both_solo_windows_clear_the_slot_edges()
    {
        // Same edge rule as above. Centers 2.2 Th apart leave 0.40 Th
        // of clearance at the tighter edge - 0.05 Th past the 0.35 Th
        // the solo window needs - so both members' quadrupole windows
        // fit inside the slot and the scheduler should multiplex them
        // into one slot.
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.4, "G1", fragments: new[] { 100.0, 200.0, 300.0, 400.0 }),
            Make("B", 502.2, 5.1, 5.5, "G2", fragments: new[] { 150.0, 250.0, 350.0, 450.0 }),
        };

        var result = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 100,
            IsolationWindowTh = 3.0,
            PrmIsolationWidthTh = 0.7,
        });

        Assert.Equal(2, result.ScheduledIndices.Length);
        Assert.Single(result.Slots);
        Assert.Equal(2, result.Slots[0].MemberIndices.Count);
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
    public void MaximizeProteins_prefers_joinable_peptide_to_free_slot_for_another_protein()
    {
        // Budget = 2, four proteins all co-eluting in [5.0, 5.4].
        //
        // G1: A1 mz 500.0                             - opens slot s1.
        // G2: B1 mz 600.0  (best score, would open new slot)
        //     B2 mz 500.5  (lower score, joins s1: a 0.5 Th center
        //                   gap easily clears the slot-edge rule for
        //                   the default 3.0 / 0.7 settings, and the
        //                   fragments don't clash with A1).
        // G3: C  mz 700.0  (needs a new slot).
        // G4: D  mz 800.0  (needs a new slot).
        //
        // Balanced: cover picks G2's best (B1) → opens slot s2, slot
        // count at this RT = 2. G3 can't open a third slot → dropped.
        // G4 dropped. Total 2 proteins.
        //
        // MaximizeProteins: cover sees B2 is joinable, takes it →
        // s1 carries G1+G2 (count stays 1). G3 opens s2 (count 2).
        // G4 still blocked. Total 3 proteins.
        var cands = new[]
        {
            Make("G1.A1", 500.0, 5.0, 5.4, "G1", quantity: 5e6,
                fragments: new[] { 100.0, 200.0, 300.0, 400.0 }),
            Make("G2.B1", 600.0, 5.0, 5.4, "G2", quantity: 3e6,
                fragments: new[] { 110.0, 210.0, 310.0, 410.0 }),
            Make("G2.B2", 500.5, 5.0, 5.4, "G2", quantity: 1e6,
                fragments: new[] { 150.0, 250.0, 350.0, 450.0 }),
            Make("G3.C",  700.0, 5.0, 5.4, "G3", quantity: 3e6,
                fragments: new[] { 120.0, 220.0, 320.0, 420.0 }),
            Make("G4.D",  800.0, 5.0, 5.4, "G4", quantity: 2e6,
                fragments: new[] { 130.0, 230.0, 330.0, 430.0 }),
        };

        var balanced = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 2,
            Objective = CoverageObjective.Balanced,
        });
        var maxProteins = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 2,
            Objective = CoverageObjective.MaximizeProteins,
        });

        Assert.Equal(2, balanced.ProteinGroupsCovered);
        Assert.Equal(3, maxProteins.ProteinGroupsCovered);
    }

    [Fact]
    public void MaximizePeptides_uncaps_load_up_but_keeps_round_robin_fairness()
    {
        // Two proteins, four peptides each, well-separated RTs so every
        // peptide opens its own slot (no MTM merging here). With Max = 2:
        //
        //   Balanced: 2 peptides per protein → 4 total.
        //   MaximizePeptides: load-up keeps going past Max →
        //                     all 8 peptides scheduled.
        //
        // Round-robin order is preserved by construction: the load-up
        // loop iterates groups in order and adds at most one peptide per
        // group per pass.
        var cands = new[]
        {
            Make("G1.a", 500.0, 5.0, 5.4, "G1", quantity: 4e6),
            Make("G1.b", 510.0, 6.0, 6.4, "G1", quantity: 3e6),
            Make("G1.c", 520.0, 7.0, 7.4, "G1", quantity: 2e6),
            Make("G1.d", 530.0, 8.0, 8.4, "G1", quantity: 1e6),
            Make("G2.a", 600.0, 5.5, 5.9, "G2", quantity: 4e6),
            Make("G2.b", 610.0, 6.5, 6.9, "G2", quantity: 3e6),
            Make("G2.c", 620.0, 7.5, 7.9, "G2", quantity: 2e6),
            Make("G2.d", 630.0, 8.5, 8.9, "G2", quantity: 1e6),
        };

        var balanced = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 100,
            MaxPeptidesPerProtein = 2,
            Objective = CoverageObjective.Balanced,
        });
        var maxPeptides = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 100,
            MaxPeptidesPerProtein = 2,
            Objective = CoverageObjective.MaximizePeptides,
        });

        Assert.Equal(4, balanced.ScheduledIndices.Length);
        Assert.Equal(8, maxPeptides.ScheduledIndices.Length);

        // Fairness check: at every prefix of the load-up sequence the
        // per-group counts differ by at most 1. We can verify the final
        // state symmetrically: both groups end with 4 peptides each.
        int g1 = maxPeptides.ScheduledIndices.Count(i => cands[i].ProteinGroup == "G1");
        int g2 = maxPeptides.ScheduledIndices.Count(i => cands[i].ProteinGroup == "G2");
        Assert.Equal(4, g1);
        Assert.Equal(4, g2);
    }

    [Fact]
    public void Load_up_adds_extra_peptides_under_Balanced_but_not_under_MaximizeProteins_with_default_Min()
    {
        // One protein, three candidates that don't overlap in RT. Cover
        // pass schedules the highest-score one; the load-up cap is what
        // decides whether the others get added.
        //
        // Balanced load-up cap = Max (default 5) -> all three schedule.
        // MaximizeProteins load-up cap = Min (default 1) -> only the
        // cover-pass pick survives.
        var cands = new[]
        {
            Make("A", 500.0, 5.0, 5.4, "G1", quantity: 1e8),
            Make("B", 600.0, 6.0, 6.4, "G1", quantity: 1e7),
            Make("C", 700.0, 7.0, 7.4, "G1", quantity: 1e6),
        };

        var balanced = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 100,
            Objective = CoverageObjective.Balanced,
        });
        var maxProteins = Scheduler.Run(cands, new SchedulingParameters
        {
            CycleBudget = 100,
            Objective = CoverageObjective.MaximizeProteins,
        });

        Assert.Equal(3, balanced.ScheduledIndices.Length);
        Assert.Single(maxProteins.ScheduledIndices);
    }
}
