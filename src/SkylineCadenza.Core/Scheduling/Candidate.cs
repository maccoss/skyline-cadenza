namespace SkylineCadenza.Core.Scheduling;

/// <summary>
/// One scheduling candidate. Represents a single precursor (peptide + charge)
/// already assigned to a parsimonious protein group with its top-4 fragment
/// m/z values from the spectral library.
/// </summary>
/// <remarks>
/// Mirrors the row layout of the <c>candidates</c> DataFrame in the
/// targeted-modeling notebook (cell <c>20730103</c>). The unpadded
/// <see cref="RtStart"/> / <see cref="RtStop"/> drive the co-elution
/// intersection check; the scheduler pads them by
/// <see cref="SchedulingParameters.FiringPadMin"/> when computing the slot's
/// firing window for the budget.
/// </remarks>
public sealed class Candidate
{
    public required string PrecursorId { get; init; }
    public required string StrippedSequence { get; init; }
    public required string ModifiedSequence { get; init; }
    public required int PrecursorCharge { get; init; }
    public required double PrecursorMz { get; init; }

    /// <summary>Unpadded peak start (min).</summary>
    public required double RtStart { get; init; }

    /// <summary>Unpadded peak stop (min).</summary>
    public required double RtStop { get; init; }

    /// <summary>Apex retention time (min) - written into the Skyline target list.</summary>
    public required double RtApex { get; init; }

    public required double PrecursorQuantity { get; init; }

    /// <summary>Peptide-precursor q-value (DIA-NN <c>Q.Value</c>).</summary>
    public required double QValue { get; init; }

    /// <summary>Protein-group q-value (DIA-NN <c>PG.Q.Value</c>) or NaN if unavailable.</summary>
    public required double ProteinQValue { get; init; }

    /// <summary>1 if the peptide is unique to a single protein in the database.</summary>
    public required int Proteotypic { get; init; }

    /// <summary>Canonical protein-group id after parsimony (e.g. UniProt accession).</summary>
    public required string ProteinGroup { get; init; }

    /// <summary><c>"unique"</c> or <c>"razor"</c> per the parsimony assignment.</summary>
    public required string PeptideType { get; init; }

    /// <summary>
    /// Top-4 predicted fragment m/z values sorted ascending. May be empty for
    /// precursors with no library entry - those become solo-slot only.
    /// </summary>
    public required double[] Top4Fragments { get; init; }

    /// <summary>DIA-NN <c>Run</c> name. Used to split per-run / per-GPF views.</summary>
    public required string Run { get; init; }
}
