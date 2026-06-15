using System.Globalization;
using System.Text;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.SkylineRpc;

/// <summary>
/// Builds a small-molecule transition-list CSV in the format consumed by
/// <c>SkylineJsonToolClient.InsertSmallMoleculeTransitionList</c>.
/// </summary>
/// <remarks>
/// The columns match the layout that Skyline's importer expects for a
/// scheduled inclusion list. Each scheduled (peptide, charge) generates one
/// row per top-4 fragment - so up to 4 rows per precursor.
///
/// <para>
/// <c>MoleculeGroup</c> = the parsimonious protein group's canonical id.
/// <c>PrecursorName</c> = <c>&lt;stripped_seq&gt;+&lt;charge&gt;</c>.
/// <c>PrecursorRT</c> = the unpadded apex retention time (min) - Skyline
/// schedules an explicit retention window around this.
/// <c>Note</c> = <c>slot=&lt;id&gt;;type=&lt;unique|razor&gt;</c> so the
/// user can see MTM grouping inside Skyline.
/// </para>
/// </remarks>
public static class TransitionListBuilder
{
    public static string Build(
        IReadOnlyList<Candidate> candidates,
        ScheduleResult schedule)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",",
            "MoleculeGroup",
            "PrecursorName",
            "PrecursorFormula",
            "PrecursorAdduct",
            "PrecursorMz",
            "PrecursorCharge",
            "ProductFormula",
            "ProductAdduct",
            "ProductMz",
            "ProductCharge",
            "PrecursorRT",
            "Note"));

        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < schedule.ScheduledIndices.Length; i++)
        {
            int candIdx = schedule.ScheduledIndices[i];
            int slotId = schedule.ScheduledSlotIds[i];
            var c = candidates[candIdx];
            string note = $"slot={slotId};type={c.PeptideType}";
            string precursorName = $"{c.StrippedSequence}+{c.PrecursorCharge}";
            string mzStr = c.PrecursorMz.ToString("0.0000", inv);
            string rtStr = c.RtApex.ToString("0.0000", inv);

            if (c.Top4Fragments.Length == 0)
            {
                // No library fragments - emit a single precursor-only row.
                sb.AppendLine(string.Join(",",
                    Csv(c.ProteinGroup),
                    Csv(precursorName),
                    "",
                    "[M+H]",
                    mzStr,
                    c.PrecursorCharge.ToString(inv),
                    "",
                    "",
                    "",
                    "1",
                    rtStr,
                    Csv(note)));
                continue;
            }

            foreach (var frag in c.Top4Fragments)
            {
                string fragStr = frag.ToString("0.0000", inv);
                sb.AppendLine(string.Join(",",
                    Csv(c.ProteinGroup),
                    Csv(precursorName),
                    "",
                    "[M+H]",
                    mzStr,
                    c.PrecursorCharge.ToString(inv),
                    "",
                    "[M+H]",
                    fragStr,
                    "1",
                    rtStr,
                    Csv(note)));
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
