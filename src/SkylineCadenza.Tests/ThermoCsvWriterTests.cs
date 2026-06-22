using SkylineCadenza.Core.Scheduling;
using SkylineCadenza.Core.SkylineRpc;
using Xunit;

namespace SkylineCadenza.Tests;

public class ThermoCsvWriterTests
{
    private static Candidate Make(
        string seq, int z, double mz, double rtStart, double rtStop,
        string group) =>
        new Candidate
        {
            PrecursorId = $"{seq}+{z}",
            StrippedSequence = seq,
            ModifiedSequence = seq,
            PrecursorCharge = z,
            PrecursorMz = mz,
            RtStart = rtStart,
            RtStop = rtStop,
            RtApex = (rtStart + rtStop) / 2.0,
            PrecursorQuantity = 1e6,
            QValue = 0.001,
            ProteinQValue = 0.001,
            Proteotypic = 1,
            ProteinGroup = group,
            PeptideType = "unique",
            Fragments = new[]
            {
                new FragmentIon(200.0, 100.0, 1),
                new FragmentIon(300.0, 90.0, 1),
            },
            Top4Fragments = new[] { 200.0, 300.0 },
            Run = "test",
        };

    [Fact]
    public void Build_Header_Matches_Thermo_Mass_List_Table_Schema()
    {
        // The Thermo Method Editor Mass List Table requires these exact
        // column names. Earlier versions used "t (min)" / "Window (min)"
        // / "Normalized CE", which Method Editor could not map: it
        // silently fell back to a 0-to-end-of-gradient window for every
        // entry, which defeats scheduling entirely. Pin the exact
        // header strings so a future rename trips this test rather than
        // breaking the import again.
        var cands = new[] { Make("PEPTIDE", 2, 500.0, 5.0, 6.0, "P1") };
        var schedule = Scheduler.Run(cands, new SchedulingParameters
        {
            Mode = AcquisitionMode.Prm,
            CycleBudget = 10,
        });
        var csv = ThermoCsvWriter.Build(cands, schedule, new SchedulingParameters
        {
            Mode = AcquisitionMode.Prm,
            PrmIsolationWidthTh = 0.7,
            NormalizedCollisionEnergy = 22.0,
        });

        var lines = csv.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "Compound,Formula,Adduct,m/z,z,"
            + "t start (min),t stop (min),"
            + "Isolation Window (m/z),HCD Collision Energy",
            lines[0]);
    }

    [Fact]
    public void Build_Prm_TStart_TStop_Are_Padded_Scheduling_Window()
    {
        // PRM row's t start / t stop columns should be the slot's
        // padded scheduling window (Slot.RtStart and Slot.RtStop),
        // matching what the scheduler actually costed against the
        // cycle budget.
        var cands = new[] { Make("PEPTIDE", 2, 500.0, 5.0, 6.0, "P1") };
        var schedule = Scheduler.Run(cands, new SchedulingParameters
        {
            Mode = AcquisitionMode.Prm,
            CycleBudget = 10,
            FiringPadSec = 15.0,
        });
        var csv = ThermoCsvWriter.Build(cands, schedule, new SchedulingParameters
        {
            Mode = AcquisitionMode.Prm,
            PrmIsolationWidthTh = 0.7,
            NormalizedCollisionEnergy = 22.0,
            FiringPadSec = 15.0,
        });

        var rows = csv.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length); // header + 1 row
        var fields = rows[1].Split(',');
        // Header positions: 0 Compound, 1 Formula, 2 Adduct, 3 m/z,
        // 4 z, 5 t start, 6 t stop, 7 isolation, 8 HCD.
        double tStart = double.Parse(fields[5],
            System.Globalization.CultureInfo.InvariantCulture);
        double tStop = double.Parse(fields[6],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(schedule.Slots[0].RtStart, tStart, precision: 4);
        Assert.Equal(schedule.Slots[0].RtStop, tStop, precision: 4);
        Assert.True(tStart < tStop);
    }

    [Fact]
    public void Build_Adduct_Column_Is_Blank()
    {
        // Adduct should be blank for peptides; the z column already
        // encodes the protonation state. Method Editor's example mass
        // list also shows blank adduct for peptide entries.
        var cands = new[] { Make("PEPTIDE", 3, 400.0, 5.0, 6.0, "P1") };
        var schedule = Scheduler.Run(cands, new SchedulingParameters
        {
            Mode = AcquisitionMode.Prm,
            CycleBudget = 10,
        });
        var csv = ThermoCsvWriter.Build(cands, schedule, new SchedulingParameters
        {
            Mode = AcquisitionMode.Prm,
            PrmIsolationWidthTh = 0.7,
        });

        var rows3 = csv.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.RemoveEmptyEntries);
        var fields = rows3[1].Split(',');
        Assert.Equal(string.Empty, fields[1]); // Formula
        Assert.Equal(string.Empty, fields[2]); // Adduct
    }
}
