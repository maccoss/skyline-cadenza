namespace SkylineCadenza.Core.Scheduling;

/// <summary>
/// Fragment m/z proximity test for MTM slot-sharing decisions.
/// </summary>
public static class FragmentClash
{
    /// <summary>
    /// Returns <c>true</c> if any value in <paramref name="a"/> is within
    /// <paramref name="tolDa"/> of any value in <paramref name="b"/>.
    /// </summary>
    /// <remarks>
    /// Both arrays are assumed sorted ascending. Two-pointer merge: advances
    /// whichever pointer points at the smaller value and aborts as soon as
    /// a near-neighbour pair is found. O(|a| + |b|).
    /// </remarks>
    public static bool AnyWithin(ReadOnlySpan<double> a, ReadOnlySpan<double> b, double tolDa)
    {
        if (a.IsEmpty || b.IsEmpty)
            return false;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            double diff = a[i] - b[j];
            double abs = diff < 0 ? -diff : diff;
            if (abs <= tolDa)
                return true;
            if (diff < 0)
                i++;
            else
                j++;
        }
        return false;
    }
}
