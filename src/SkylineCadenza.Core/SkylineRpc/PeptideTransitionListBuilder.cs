using System.Globalization;
using System.Text;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.SkylineRpc;

/// <summary>
/// Writes a peptide-style transition list CSV in the column layout
/// Skyline's <c>--import-transition-list=</c> expects for proteomics
/// documents. Emit one row per (scheduled precursor, top-4 fragment).
/// </summary>
/// <remarks>
/// <para>
/// The previous transition list builder used the small-molecule column
/// set (MoleculeGroup, PrecursorName, ...), which Skyline routes into the
/// small-molecule node of the document. For a proteomics document, those
/// entries land in a hidden tree the user never sees. This builder
/// emits the proteomic-side column names so Skyline puts the imported
/// peptides in the protein/peptide tree.
/// </para>
/// <para>
/// The DIA-NN style modification syntax <c>C(UniMod:4)</c> and the
/// Carafe style <c>_C[UniMod:4]DIVIEK_</c> are both normalised to
/// Skyline's preferred form <c>C[UniMod:4]DIVIEK</c> (square brackets,
/// no flanking underscores).
/// </para>
/// </remarks>
public static class PeptideTransitionListBuilder
{
    public static string Build(
        IReadOnlyList<Candidate> candidates,
        ScheduleResult schedule)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            "Protein Name",
            "Peptide Modified Sequence",
            "Precursor Charge",
            "Precursor m/z",
            "Product m/z",
            "Product Charge",
            "Explicit Retention Time",
            "Explicit Retention Time Window",
            "Note"));

        var inv = CultureInfo.InvariantCulture;
        var slotsById = schedule.Slots.ToDictionary(s => s.Id);

        for (int i = 0; i < schedule.ScheduledIndices.Length; i++)
        {
            int candIdx = schedule.ScheduledIndices[i];
            int slotId = schedule.ScheduledSlotIds[i];
            var c = candidates[candIdx];
            var slot = slotsById[slotId];

            double windowMin = slot.RtStop - slot.RtStart;
            string note = $"slot={slotId};type={c.PeptideType}";
            string protein = c.ProteinGroup;
            string peptide = NormalizeModifiedSequence(c.ModifiedSequence);

            if (c.Top4Fragments.Length == 0)
            {
                AppendRow(sb, inv, protein, peptide, c.PrecursorCharge,
                    c.PrecursorMz, productMz: null, productCharge: null,
                    c.RtApex, windowMin, note);
                continue;
            }

            foreach (var fragMz in c.Top4Fragments)
            {
                AppendRow(sb, inv, protein, peptide, c.PrecursorCharge,
                    c.PrecursorMz, fragMz, productCharge: 1,
                    c.RtApex, windowMin, note);
            }
        }
        return sb.ToString();
    }

    private static void AppendRow(
        StringBuilder sb, CultureInfo inv,
        string protein, string peptide, int precursorCharge,
        double precursorMz, double? productMz, int? productCharge,
        double rtApex, double windowMin, string note)
    {
        sb.AppendLine(string.Join(",",
            Csv(protein),
            Csv(peptide),
            precursorCharge.ToString(inv),
            precursorMz.ToString("0.0000", inv),
            productMz.HasValue ? productMz.Value.ToString("0.0000", inv) : "",
            productCharge.HasValue ? productCharge.Value.ToString(inv) : "",
            rtApex.ToString("0.0000", inv),
            windowMin.ToString("0.0000", inv),
            Csv(note)));
    }

    /// <summary>
    /// Convert a DIA-NN <c>C(UniMod:4)</c> or Carafe <c>_C[UniMod:4]_</c>
    /// style modified sequence to Skyline's preferred <c>C[UniMod:4]</c>
    /// form. Strips flanking underscores and swaps parentheses for square
    /// brackets.
    /// </summary>
    private static string NormalizeModifiedSequence(string modSeq)
    {
        if (string.IsNullOrEmpty(modSeq)) return modSeq;
        var sb = new StringBuilder(modSeq.Length);
        foreach (var ch in modSeq)
        {
            switch (ch)
            {
                case '_': break;
                case '(': sb.Append('['); break;
                case ')': sb.Append(']'); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
