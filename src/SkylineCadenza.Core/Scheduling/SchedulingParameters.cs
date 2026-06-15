namespace SkylineCadenza.Core.Scheduling;

/// <summary>
/// User-tunable scheduler knobs. Defaults match the targeted-modeling
/// notebook's "MTM at budget 100" configuration.
/// </summary>
public sealed record SchedulingParameters
{
    /// <summary>PRM (1 precursor per slot) or MTM (multiplex).</summary>
    public AcquisitionMode Mode { get; init; } = AcquisitionMode.Mtm;

    /// <summary>Whether to add additional peptides per protein after every protein has at least one.</summary>
    public bool EnableLoadBalancing { get; init; } = true;

    /// <summary>Maximum MTM isolation window width (Th). Ignored when <see cref="Mode"/> is PRM.</summary>
    public double IsolationWindowTh { get; init; } = 3.0;

    /// <summary>
    /// PRM-mode quadrupole isolation window width (Th), centered on the
    /// precursor m/z. Also used as the floor for solo MTM slots so the
    /// instrument has room to capture the M+1 isotope.
    /// </summary>
    public double PrmIsolationWidthTh { get; init; } = 0.7;

    /// <summary>Fragment m/z tolerance for the non-clash check (Da).</summary>
    public double FragmentTolDa { get; init; } = 0.5;

    /// <summary>Max concurrent acquisition slots per cycle.</summary>
    public int CycleBudget { get; init; } = 100;

    /// <summary>Seconds of padding added to each side of each peak's retention range.</summary>
    public double FiringPadSec { get; init; } = 15.0;

    /// <summary>Width of RT-occupancy bins (min). 0.05 = 3 s.</summary>
    public double RtBinMin { get; init; } = 0.05;

    /// <summary>DIA-NN <c>Q.Value</c> cutoff used at the candidate-build stage.</summary>
    public double QValueCutoff { get; init; } = 0.01;

    /// <summary>
    /// User-supplied set of protein accessions to focus the assay on. Empty
    /// = no filter (the scheduler considers every protein the report sees).
    /// </summary>
    public IReadOnlySet<string> TargetProteins { get; init; } = new HashSet<string>();

    /// <summary>
    /// What to do with proteins that AREN'T in <see cref="TargetProteins"/>.
    /// Has no effect if the target set is empty.
    /// </summary>
    public TargetListMode TargetMode { get; init; } = TargetListMode.FirstThenFill;

    /// <summary>Minimum peptides scheduled per protein group (best effort).</summary>
    public int MinPeptidesPerProtein { get; init; } = 1;

    /// <summary>Maximum peptides scheduled per protein group.</summary>
    public int MaxPeptidesPerProtein { get; init; } = 5;

    /// <summary>How to order the protein groups going into the assay.</summary>
    public ProteinPriority ProteinRanking { get; init; } = ProteinPriority.SummedIntensity;

    /// <summary>How to order peptides within each protein group.</summary>
    public PeptidePriority PeptideRanking { get; init; } = PeptidePriority.PrecursorQuantity;

    /// <summary>
    /// How precursor charge is handled when joining MTM slots. Ignored for
    /// PRM (always 1 precursor / slot).
    /// </summary>
    public ChargeHandling ChargeHandling { get; init; } = ChargeHandling.SameChargePerSlot;

    /// <summary>
    /// Normalized collision energy emitted in the Thermo inclusion CSV.
    /// Not used by the scheduler; only by <c>ThermoCsvWriter</c>.
    /// </summary>
    public double NormalizedCollisionEnergy { get; init; } = 28.0;

    /// <summary>Convenience accessor: <see cref="FiringPadSec"/> in minutes.</summary>
    public double FiringPadMin => FiringPadSec / 60.0;
}

public enum AcquisitionMode
{
    Prm,
    Mtm,
}

public enum TargetListMode
{
    /// <summary>Only proteins in <c>TargetProteins</c> are eligible.</summary>
    Exclusive,
    /// <summary>Target proteins first, then fill with off-list proteins.</summary>
    FirstThenFill,
}

public enum ProteinPriority
{
    /// <summary>Sum of peptide-precursor quantities per group (DIA-NN intensity).</summary>
    SummedIntensity,
    /// <summary>Best (smallest) per-group q-value (<c>PG.Q.Value</c>).</summary>
    ProteinQValue,
    /// <summary>Order proteins by the user's input list (insertion order).</summary>
    ProvidedListOrder,
}

public enum PeptidePriority
{
    /// <summary>DIA-NN <c>Precursor.Quantity</c> (real measured intensity).</summary>
    PrecursorQuantity,
    /// <summary>DIA-NN <c>Q.Value</c> (peptide-precursor q-value, ascending).</summary>
    QValue,
}

public enum ChargeHandling
{
    /// <summary>
    /// MTM slots may only contain precursors of identical
    /// <see cref="Candidate.PrecursorCharge"/>. Cleaner CE assignment and
    /// simpler downstream interpretation; mild coverage cost.
    /// </summary>
    SameChargePerSlot,
    /// <summary>
    /// MTM slots may contain mixed charges. The Thermo CSV writer reports
    /// each slot's majority charge (ties go to the lower z, since +2 is the
    /// canonical tryptic default).
    /// </summary>
    AllowMixed,
}
