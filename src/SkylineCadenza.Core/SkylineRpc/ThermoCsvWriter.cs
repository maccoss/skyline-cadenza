using System.Globalization;
using System.Text;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.SkylineRpc;

/// <summary>
/// Writes a Thermo Method Editor-importable scheduled inclusion CSV.
/// </summary>
/// <remarks>
/// <para>
/// PRM mode: one row per scheduled precursor (PRM has 1 precursor / slot).
/// </para>
/// <para>
/// MTM mode: one row per <i>slot</i>. When a slot is multiplexed the
/// Compound column joins the member peptide identifiers, the m/z column
/// reports the slot's center, the z column reports the slot's majority
/// charge (ties go to the lower charge), and the isolation-window column
/// reports the effective m/z width (member span padded out to the
/// configured PRM / solo-slot width).
/// </para>
/// <para>
/// The slot's <c>t (min)</c> is the unpadded co-elution apex (midpoint of
/// <see cref="Slot.CoStart"/> and <see cref="Slot.CoStop"/>) and
/// <c>Window (min)</c> is the full padded firing window width
/// (<see cref="Slot.RtStop"/> - <see cref="Slot.RtStart"/>) so Thermo
/// keeps the window on for the same interval the scheduler costed.
/// </para>
/// </remarks>
public static class ThermoCsvWriter
{
    private const string CompoundJoinSeparator = " | ";

    public static string Build(
        IReadOnlyList<Candidate> candidates,
        ScheduleResult schedule,
        SchedulingParameters parameters,
        double prmIsolationWidthTh = 0.7)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            "Compound",
            "Formula",
            "Adduct",
            "m/z",
            "z",
            "t (min)",
            "Window (min)",
            "Isolation Window (m/z)",
            "Normalized CE"));

        var inv = CultureInfo.InvariantCulture;
        double prmWidth = parameters.PrmIsolationWidthTh > 0 ? parameters.PrmIsolationWidthTh : prmIsolationWidthTh;
        double nce = parameters.NormalizedCollisionEnergy;

        if (parameters.Mode == AcquisitionMode.Prm)
        {
            // One row per scheduled precursor; PRM never multiplexes.
            var slotsById = schedule.Slots.ToDictionary(s => s.Id);
            for (int i = 0; i < schedule.ScheduledIndices.Length; i++)
            {
                int candIdx = schedule.ScheduledIndices[i];
                int slotId = schedule.ScheduledSlotIds[i];
                var c = candidates[candIdx];
                var slot = slotsById[slotId];
                AppendRow(sb,
                    compound: $"{c.StrippedSequence}+{c.PrecursorCharge}",
                    mz: c.PrecursorMz,
                    z: c.PrecursorCharge,
                    tMin: c.RtApex,
                    windowMin: slot.RtStop - slot.RtStart,
                    isolationWidth: prmWidth,
                    nce: nce, inv: inv);
            }
            return sb.ToString();
        }

        // MTM: group by slot, write one row per slot. Multiplexed slots
        // get joined compound names and the slot's majority charge.
        var byIndex = new Dictionary<int, int>(); // candIdx -> position in ScheduledIndices
        for (int i = 0; i < schedule.ScheduledIndices.Length; i++)
            byIndex[schedule.ScheduledIndices[i]] = i;

        foreach (var slot in schedule.Slots)
        {
            if (slot.MemberIndices.Count == 0) continue;

            var memberNames = new List<string>(slot.MemberIndices.Count);
            int z = MajorityCharge(slot, candidates);
            foreach (var idx in slot.MemberIndices)
            {
                var c = candidates[idx];
                memberNames.Add($"{c.StrippedSequence}+{c.PrecursorCharge}");
            }

            double mzCenter = (slot.MzMin + slot.MzMax) / 2.0;
            double memberSpan = slot.MzMax - slot.MzMin;
            // Slot-edge rule: every member's PrmIsolationWidthTh quadrupole
            // window must fit inside the slot. The instrument isolation we
            // emit is the m/z spread of the members plus PrmIsolationWidthTh
            // so the windows around the edge members are fully contained.
            // Solo slots collapse to prmWidth.
            double isolationWidth = memberSpan + prmWidth;

            // Use the co-elution midpoint when meaningful (CoStart < CoStop
            // is guaranteed by the scheduler's strict-co-elution check),
            // otherwise fall back to the firing-window midpoint.
            double tMin = slot.CoStart < slot.CoStop
                ? (slot.CoStart + slot.CoStop) / 2.0
                : (slot.RtStart + slot.RtStop) / 2.0;

            AppendRow(sb,
                compound: string.Join(CompoundJoinSeparator, memberNames),
                mz: mzCenter,
                z: z,
                tMin: tMin,
                windowMin: slot.RtStop - slot.RtStart,
                isolationWidth: isolationWidth,
                nce: nce, inv: inv);
        }
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb,
        string compound, double mz, int z, double tMin,
        double windowMin, double isolationWidth, double nce,
        CultureInfo inv)
    {
        sb.AppendLine(string.Join(",",
            Csv(compound),
            "",                                  // Formula (peptides are not elemental)
            "[M+H]",
            mz.ToString("0.0000", inv),
            z.ToString(inv),
            tMin.ToString("0.0000", inv),
            windowMin.ToString("0.0000", inv),
            isolationWidth.ToString("0.0000", inv),
            nce.ToString("0.0", inv)));
    }

    /// <summary>
    /// Pick the slot's reporting charge: the most common
    /// <see cref="Candidate.PrecursorCharge"/> among members; ties break to
    /// the lower charge (since +2 is the typical tryptic default).
    /// </summary>
    private static int MajorityCharge(Slot slot, IReadOnlyList<Candidate> candidates)
    {
        var counts = new Dictionary<int, int>();
        foreach (var idx in slot.MemberIndices)
        {
            int z = candidates[idx].PrecursorCharge;
            counts[z] = counts.TryGetValue(z, out int n) ? n + 1 : 1;
        }
        int bestZ = 0, bestCount = -1;
        foreach (var kv in counts.OrderBy(kv => kv.Key))
        {
            if (kv.Value > bestCount)
            {
                bestZ = kv.Key;
                bestCount = kv.Value;
            }
        }
        return bestZ;
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
