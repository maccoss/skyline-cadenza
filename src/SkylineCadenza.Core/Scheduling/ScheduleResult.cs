namespace SkylineCadenza.Core.Scheduling;

/// <summary>
/// Output of <see cref="Scheduler.Run(IReadOnlyList{Candidate}, SchedulingParameters, System.Threading.CancellationToken)"/>.
/// </summary>
public sealed class ScheduleResult
{
    /// <summary>Indexes into the input <c>candidates</c> list of every scheduled precursor.</summary>
    public required int[] ScheduledIndices { get; init; }

    /// <summary>Slot id assigned to each scheduled precursor (parallel array to <see cref="ScheduledIndices"/>).</summary>
    public required int[] ScheduledSlotIds { get; init; }

    /// <summary>All slots created during scheduling.</summary>
    public required Slot[] Slots { get; init; }

    /// <summary>Centers of the RT-occupancy grid (min).</summary>
    public required double[] RtGrid { get; init; }

    /// <summary>Number of concurrent slots active in each RT bin.</summary>
    public required int[] SlotCountCurve { get; init; }

    /// <summary>Distinct protein groups represented in the schedule.</summary>
    public required int ProteinGroupsCovered { get; init; }
}
