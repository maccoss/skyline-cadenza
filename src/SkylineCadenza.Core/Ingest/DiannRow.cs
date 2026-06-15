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
    /// Top-4 fragment m/z values extracted from DIA-NN's per-row
    /// <c>Fr.0.Id</c>..<c>Fr.11.Id</c> columns, ranked by <c>Fr.N.Quantity</c>
    /// and sorted ascending. Used as a fallback fragment source when no
    /// Carafe library is provided.
    /// </summary>
    public required double[] Top4Fragments { get; init; }
}
