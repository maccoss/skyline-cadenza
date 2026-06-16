using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// One filtered row from a DIA-NN <c>report.parquet</c>, before parsimony and
/// fragment annotation. Each unique <see cref="PrecursorId"/> appears at most
/// once - the loader keeps the highest-<see cref="PrecursorQuantity"/>
/// observation across GPF runs.
/// </summary>
public sealed class DiannRow
{
    public required string PrecursorId { get; init; }
    public required string ModifiedSequence { get; init; }
    public required string StrippedSequence { get; init; }
    public required int PrecursorCharge { get; init; }
    public required double PrecursorMz { get; init; }
    public required double RtApex { get; init; }
    public required double RtStart { get; init; }
    public required double RtStop { get; init; }
    public required double PrecursorQuantity { get; init; }
    public required int Proteotypic { get; init; }
    public required string ProteinIds { get; init; }
    public required string Genes { get; init; }
    public required string Run { get; init; }

    /// <summary>Peptide-precursor q-value (DIA-NN <c>Q.Value</c>).</summary>
    public required double QValue { get; init; }

    /// <summary>Protein-group q-value (<c>PG.Q.Value</c>); NaN if the report doesn't carry the column.</summary>
    public required double ProteinQValue { get; init; }

    /// <summary>
    /// Up to <see cref="Candidate.FragmentLimit"/> fragment ions
    /// (m/z + intensity + charge) extracted from DIA-NN's per-row
    /// <c>Fr.0.Id</c>..<c>Fr.11.Id</c> columns and ranked by
    /// <c>Fr.N.Quantity</c> intensity descending. The fragment IDs
    /// encode charge as <c>&lt;ion&gt;^&lt;charge&gt;/&lt;mz&gt;</c>
    /// (e.g. <c>y13^1/957.511230</c>).
    /// </summary>
    public required FragmentIon[] Fragments { get; init; }
}
