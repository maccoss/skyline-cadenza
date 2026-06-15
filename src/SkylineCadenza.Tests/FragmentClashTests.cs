using SkylineCadenza.Core.Scheduling;
using Xunit;

namespace SkylineCadenza.Tests;

public class FragmentClashTests
{
    [Fact]
    public void Empty_arrays_never_clash()
    {
        Assert.False(FragmentClash.AnyWithin(Array.Empty<double>(), new[] { 100.0, 200.0 }, 0.5));
        Assert.False(FragmentClash.AnyWithin(new[] { 100.0, 200.0 }, Array.Empty<double>(), 0.5));
        Assert.False(FragmentClash.AnyWithin(Array.Empty<double>(), Array.Empty<double>(), 0.5));
    }

    [Fact]
    public void Disjoint_arrays_with_wide_separation_do_not_clash()
    {
        var a = new[] { 100.0, 200.0, 300.0 };
        var b = new[] { 150.0, 250.0, 350.0 };
        Assert.False(FragmentClash.AnyWithin(a, b, 0.5));
    }

    [Fact]
    public void Exact_match_clashes_at_zero_tolerance()
    {
        var a = new[] { 100.0, 200.0 };
        var b = new[] { 200.0, 300.0 };
        Assert.True(FragmentClash.AnyWithin(a, b, 0.0));
    }

    [Fact]
    public void Near_match_inside_tolerance_clashes()
    {
        var a = new[] { 100.0, 200.0 };
        var b = new[] { 200.3, 300.0 };
        Assert.True(FragmentClash.AnyWithin(a, b, 0.5));
    }

    [Fact]
    public void Near_match_outside_tolerance_does_not_clash()
    {
        var a = new[] { 100.0, 200.0 };
        var b = new[] { 200.6, 300.0 };
        Assert.False(FragmentClash.AnyWithin(a, b, 0.5));
    }

    [Fact]
    public void Boundary_diff_equal_to_tolerance_counts_as_clash()
    {
        var a = new[] { 100.0 };
        var b = new[] { 100.5 };
        Assert.True(FragmentClash.AnyWithin(a, b, 0.5));
    }
}
