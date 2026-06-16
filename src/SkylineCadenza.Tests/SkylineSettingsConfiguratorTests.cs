using SkylineCadenza.Core.Scheduling;
using SkylineCadenza.Core.SkylineRpc;
using Xunit;

namespace SkylineCadenza.Tests;

public class SkylineSettingsConfiguratorTests
{
    private static Candidate Make(string seq, int z, params FragmentIon[] frags) =>
        new Candidate
        {
            PrecursorId = $"{seq}+{z}",
            StrippedSequence = seq,
            ModifiedSequence = seq,
            PrecursorCharge = z,
            PrecursorMz = 400.0,
            RtStart = 5.0,
            RtStop = 5.4,
            RtApex = 5.2,
            PrecursorQuantity = 1e6,
            QValue = 0.001,
            ProteinQValue = 0.001,
            Proteotypic = 1,
            ProteinGroup = "P1",
            PeptideType = "unique",
            Fragments = frags,
            Top4Fragments = Candidate.DeriveTopMz(frags, 4),
            Run = "test",
        };

    [Fact]
    public void Recommend_DerivesPrecursorChargeUnion_FromAssay()
    {
        var cands = new[]
        {
            Make("PEPONE", 2, new FragmentIon(200, 100, 1)),
            Make("PEPTWO", 3, new FragmentIon(200, 100, 1)),
            Make("PEPTHREE", 4, new FragmentIon(200, 100, 1)),
        };
        var rec = SkylineSettingsConfigurator.Recommend(cands, AcquisitionMode.Mtm);
        Assert.Equal(new[] { 2, 3, 4 }, rec.PrecursorIonCharges.ToArray());
    }

    [Fact]
    public void Recommend_DerivesProductChargeUnion_FromFragments()
    {
        var cands = new[]
        {
            Make("PEPTIDE", 2,
                new FragmentIon(200, 100, 1),
                new FragmentIon(400, 80, 2)),
        };
        var rec = SkylineSettingsConfigurator.Recommend(cands, AcquisitionMode.Mtm);
        Assert.Equal(new[] { 1, 2 }, rec.ProductIonCharges.ToArray());
    }

    [Fact]
    public void Recommend_DefaultsToYbpIonTypes()
    {
        var cands = new[] { Make("PEPTIDE", 2, new FragmentIon(200, 100, 1)) };
        var rec = SkylineSettingsConfigurator.Recommend(cands, AcquisitionMode.Mtm);
        // y + b are the actual fragment families; p (precursor) is
        // included so the precursor m/z stays in the document's
        // transition picker even though Cadenza's pushed transition
        // list only carries y/b rows.
        Assert.Equal(new[] { "y", "b", "p" }, rec.ProductIonTypes.ToArray());
    }

    [Fact]
    public void Recommend_LibraryPickTopN_MatchesBlibWriter()
    {
        var cands = new[] { Make("PEPTIDE", 2, new FragmentIon(200, 100, 1)) };
        var rec = SkylineSettingsConfigurator.Recommend(cands, AcquisitionMode.Mtm);
        Assert.Equal(Core.Output.BlibAssayWriter.PeaksPerSpectrum, rec.LibraryPickTopN);
    }

    [Fact]
    public void Recommend_PeptideLength_BoundsFromAssayExtremes()
    {
        var cands = new[]
        {
            Make("SHORT", 2, new FragmentIon(200, 100, 1)),                       // length 5
            Make("THISISAREALLYLONGPEPTIDE", 2, new FragmentIon(200, 100, 1)),    // length 24
        };
        var rec = SkylineSettingsConfigurator.Recommend(cands, AcquisitionMode.Mtm);
        Assert.Equal(5, rec.PeptideMinLength);
        Assert.Equal(24, rec.PeptideMaxLength);
    }

    [Fact]
    public void Recommend_EmptyAssay_FallsBackToSensibleDefaults()
    {
        var rec = SkylineSettingsConfigurator.Recommend(Array.Empty<Candidate>(), AcquisitionMode.Mtm);
        Assert.Contains(2, rec.PrecursorIonCharges);
        Assert.Contains(3, rec.PrecursorIonCharges);
        Assert.Contains(1, rec.ProductIonCharges);
    }

    [Fact]
    public void ToStatusLine_IncludesAllValues()
    {
        var cands = new[]
        {
            Make("PEPTIDE", 2,
                new FragmentIon(200, 100, 1),
                new FragmentIon(400, 80, 2)),
        };
        var line = SkylineSettingsConfigurator.Recommend(cands, AcquisitionMode.Mtm).ToStatusLine();
        Assert.Contains("precursor charges {2}", line);
        Assert.Contains("product charges {1,2}", line);
        Assert.Contains("y,b", line);
    }
}
