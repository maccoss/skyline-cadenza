namespace SkylineCadenza.Core.Scheduling;

/// <summary>
/// An MTM acquisition slot. Holds one or more co-eluting precursors that
/// share a single isolation window definition.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RtStart"/> / <see cref="RtStop"/> are the padded firing window
/// (union of member padded ranges). This is what the budget counter tracks -
/// the time the window is on.
/// </para>
/// <para>
/// <see cref="CoStart"/> / <see cref="CoStop"/> are the intersection of all
/// members' UNPADDED peak ranges. This is when every member is actually
/// co-eluting; multiplexed firing produces useful deconvolvable data only
/// during this interval. A non-empty intersection is required for any join.
/// </para>
/// </remarks>
public sealed class Slot
{
    public int Id { get; set; }

    public List<int> MemberIndices { get; } = new();

    public double MzMin { get; set; }
    public double MzMax { get; set; }

    /// <summary>Padded firing window start (min).</summary>
    public double RtStart { get; set; }

    /// <summary>Padded firing window stop (min).</summary>
    public double RtStop { get; set; }

    /// <summary>Unpadded co-elution intersection start (min).</summary>
    public double CoStart { get; set; }

    /// <summary>Unpadded co-elution intersection stop (min).</summary>
    public double CoStop { get; set; }

    /// <summary>
    /// Union of all member top-4 fragments, sorted ascending. New joins must
    /// not clash with any value in here within
    /// <see cref="SchedulingParameters.FragmentTolDa"/>.
    /// </summary>
    public double[] Fragments { get; set; } = Array.Empty<double>();
}
