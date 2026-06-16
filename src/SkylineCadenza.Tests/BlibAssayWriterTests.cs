using SkylineCadenza.Core.Ingest;
using SkylineCadenza.Core.Output;
using SkylineCadenza.Core.Scheduling;
using Xunit;
using Microsoft.Data.Sqlite;

namespace SkylineCadenza.Tests;

public class BlibAssayWriterTests
{
    private static Candidate MakeCandidate(
        string strippedSeq, string modSeq, int charge, double mz,
        double rtStart, double rtStop, string proteinGroup,
        (double mz, double intensity, int charge)[] fragments)
    {
        var ions = fragments
            .Select(f => new FragmentIon(f.mz, f.intensity, f.charge))
            .ToArray();
        return new Candidate
        {
            PrecursorId = $"{strippedSeq}+{charge}",
            StrippedSequence = strippedSeq,
            ModifiedSequence = modSeq,
            PrecursorCharge = charge,
            PrecursorMz = mz,
            RtStart = rtStart,
            RtStop = rtStop,
            RtApex = (rtStart + rtStop) / 2,
            PrecursorQuantity = 1e6,
            QValue = 0.001,
            ProteinQValue = 0.001,
            Proteotypic = 1,
            ProteinGroup = proteinGroup,
            PeptideType = "unique",
            Fragments = ions,
            Top4Fragments = Candidate.DeriveTopMz(ions, 4),
            Run = "test",
        };
    }

    private static ScheduleResult MakeSchedule(IReadOnlyList<Candidate> candidates)
    {
        // Schedule every candidate into its own slot for the round-trip
        // test - we're verifying the writer, not the scheduler.
        var slots = new Slot[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            slots[i] = new Slot
            {
                Id = i,
                MzMin = candidates[i].PrecursorMz,
                MzMax = candidates[i].PrecursorMz,
                RtStart = candidates[i].RtStart,
                RtStop = candidates[i].RtStop,
                CoStart = candidates[i].RtStart,
                CoStop = candidates[i].RtStop,
                Fragments = Array.Empty<double>(),
            };
            slots[i].MemberIndices.Add(i);
        }
        return new ScheduleResult
        {
            ScheduledIndices = Enumerable.Range(0, candidates.Count).ToArray(),
            ScheduledSlotIds = Enumerable.Range(0, candidates.Count).ToArray(),
            Slots = slots,
            RtGrid = Array.Empty<double>(),
            SlotCountCurve = Array.Empty<int>(),
            ProteinGroupsCovered = candidates.Select(c => c.ProteinGroup).Distinct().Count(),
        };
    }

    private static string TempBlib() =>
        Path.Combine(Path.GetTempPath(), $"cadenza-test-{Guid.NewGuid():N}.blib");

    [Fact]
    public void Write_ProducesReadableBlib_RoundTripViaReader()
    {
        var candidates = new[]
        {
            MakeCandidate("PEPTIDE", "PEPTIDE", 2, 400.20, 5.10, 5.60, "P1",
                new[] { (200.1, 100.0, 1), (300.2, 80.0, 1), (400.3, 60.0, 1) }),
            MakeCandidate("ANOTHERONE", "ANOTHERONE", 3, 500.30, 7.30, 7.90, "P2",
                new[] { (250.1, 90.0, 1), (350.2, 70.0, 2) }),
        };
        var schedule = MakeSchedule(candidates);

        string path = TempBlib();
        try
        {
            var result = BlibAssayWriter.Write(candidates, schedule, path, "round-trip-test");
            Assert.Equal(2, result.RefSpectraWritten);
            Assert.Equal(2, result.RetentionTimeRowsWritten);
            Assert.Equal(2, result.ProteinsWritten);
            Assert.Equal(5, result.FragmentsWritten);

            // Read back via the existing BlibRetentionTimeReader.
            var lib = BlibRetentionTimeReader.Read(path, qValueCutoff: 0.5);
            Assert.Equal(2, lib.Boundaries.Count);

            Assert.True(lib.Boundaries.TryGetValue(("PEPTIDE", 2), out var b1));
            Assert.Equal(5.10, b1!.RtStart, precision: 3);
            Assert.Equal(5.60, b1.RtStop, precision: 3);
            Assert.Equal(1, b1.ReplicateCount);

            Assert.True(lib.Boundaries.TryGetValue(("ANOTHERONE", 3), out var b2));
            Assert.Equal(7.30, b2!.RtStart, precision: 3);
            Assert.Equal(7.90, b2.RtStop, precision: 3);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Write_StoresUpToSixFragments_PerSpectrum()
    {
        var manyFrags = Enumerable.Range(1, 10)
            .Select(i => (mz: 100.0 + i * 50, intensity: (double)(20 - i), charge: 1))
            .ToArray();
        var cands = new[]
        {
            MakeCandidate("MANYFRAG", "MANYFRAG", 2, 400.0, 5.0, 5.5, "P1", manyFrags),
        };
        var schedule = MakeSchedule(cands);

        string path = TempBlib();
        try
        {
            var result = BlibAssayWriter.Write(cands, schedule, path);
            Assert.Equal(BlibAssayWriter.PeaksPerSpectrum, result.FragmentsWritten);

            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT numPeaks FROM RefSpectra";
            var numPeaks = (long)cmd.ExecuteScalar()!;
            Assert.Equal(BlibAssayWriter.PeaksPerSpectrum, (int)numPeaks);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Write_PopulatesProteinsAndMapping()
    {
        var cands = new[]
        {
            MakeCandidate("AAA", "AAA", 2, 300.0, 5.0, 5.4, "ProtA",
                new[] { (150.0, 10.0, 1) }),
            MakeCandidate("BBB", "BBB", 2, 350.0, 6.0, 6.4, "ProtA",
                new[] { (170.0, 8.0, 1) }),
            MakeCandidate("CCC", "CCC", 2, 400.0, 7.0, 7.4, "ProtB",
                new[] { (190.0, 6.0, 1) }),
        };
        var schedule = MakeSchedule(cands);

        string path = TempBlib();
        try
        {
            var result = BlibAssayWriter.Write(cands, schedule, path);
            Assert.Equal(2, result.ProteinsWritten);

            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = "SELECT COUNT(DISTINCT accession) FROM Proteins";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);

            cmd.CommandText = "SELECT COUNT(*) FROM RefSpectraProteins";
            Assert.Equal(3L, (long)cmd.ExecuteScalar()!);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Write_ParsesMassDeltaModifications()
    {
        // C[+57.0214637]PEPTIDE has one mod at position 1 with mass 57.02...
        var cands = new[]
        {
            MakeCandidate("CPEPTIDE", "C[+57.0214637]PEPTIDE", 2, 432.20, 5.0, 5.4, "P1",
                new[] { (200.0, 10.0, 1) }),
        };
        var schedule = MakeSchedule(cands);

        string path = TempBlib();
        try
        {
            BlibAssayWriter.Write(cands, schedule, path);
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT position, mass FROM Modifications";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.Equal(1, r.GetInt32(0));
            Assert.Equal(57.0214637, r.GetDouble(1), precision: 6);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ParseMassDeltaMods_HandlesMultipleModsAndPositions()
    {
        var mods = BlibAssayWriter.ParseMassDeltaMods("AM[+15.99]C[+57.02]PEPTIDE");
        Assert.Equal(2, mods.Count);
        Assert.Equal(2, mods[0].Position);
        Assert.Equal(15.99, mods[0].MassDelta, precision: 2);
        Assert.Equal(3, mods[1].Position);
        Assert.Equal(57.02, mods[1].MassDelta, precision: 2);
    }

    [Fact]
    public void StripModifications_RemovesAllBracketsAndParens()
    {
        Assert.Equal("CPEPTIDE", BlibAssayWriter.StripModifications("C[+57.0]PEPTIDE"));
        Assert.Equal("CPEPTIDE", BlibAssayWriter.StripModifications("C(UniMod:4)PEPTIDE"));
        Assert.Equal("CPEPTIDE", BlibAssayWriter.StripModifications("C[UniMod:4]PEPTIDE"));
    }

    [Fact]
    public void NormalizeModifiedSequence_ConvertsUniModIdsToMassDeltas()
    {
        // Skyline's LibKeyModificationMatcher tries to parse the
        // bracket contents as a number, so UniMod IDs in BLIB
        // peptideModSeq throw FormatException. Cadenza translates
        // them before writing. Mass values must match the UniMod
        // database (https://www.unimod.org).
        Assert.Equal("C[+57.021464]PEPTIDE",
            BlibAssayWriter.NormalizeModifiedSequence("C(UniMod:4)PEPTIDE"));
        Assert.Equal("C[+57.021464]PEPTIDE",
            BlibAssayWriter.NormalizeModifiedSequence("_C[UniMod:4]PEPTIDE_"));
        Assert.Equal("PEPM[+15.994915]TIDE",
            BlibAssayWriter.NormalizeModifiedSequence("PEPM[UniMod:35]TIDE"));
        Assert.Equal("PEPS[+79.966331]TIDE",
            BlibAssayWriter.NormalizeModifiedSequence("PEPS(UniMod:21)TIDE"));
    }

    [Fact]
    public void NormalizeModifiedSequence_AlreadyMassDelta_PassesThrough()
    {
        // If the input is already in mass-delta form, the only
        // transformation is the leading + sign for positive values
        // when the source omitted it.
        Assert.Equal("C[+57.021464]PEPTIDE",
            BlibAssayWriter.NormalizeModifiedSequence("C[+57.021464]PEPTIDE"));
        Assert.Equal("C[+57.0]PEPTIDE",
            BlibAssayWriter.NormalizeModifiedSequence("C[57.0]PEPTIDE"));
        Assert.Equal("C[-0.984016]PEPTIDE",
            BlibAssayWriter.NormalizeModifiedSequence("C[-0.984016]PEPTIDE"));
    }

    [Fact]
    public void NormalizeModifiedSequence_UnknownUniModId_LeavesItForSkylineToReport()
    {
        // For UniMod IDs we don't recognise, pass them through
        // verbatim so Skyline's error message points at the actual
        // offending ID instead of a translated mass that hides the
        // root cause.
        Assert.Equal("C[UniMod:99999]PEPTIDE",
            BlibAssayWriter.NormalizeModifiedSequence("C[UniMod:99999]PEPTIDE"));
    }

    [Fact]
    public void Write_SchemaMatchesReaderExpectations()
    {
        var cands = new[]
        {
            MakeCandidate("AAA", "AAA", 2, 300.0, 5.0, 5.4, "P1",
                new[] { (150.0, 10.0, 1) }),
        };
        var schedule = MakeSchedule(cands);

        string path = TempBlib();
        try
        {
            BlibAssayWriter.Write(cands, schedule, path);
            using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            var tables = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));

            // All tables BlibRetentionTimeReader queries must exist.
            Assert.Contains("LibInfo", tables);
            Assert.Contains("RefSpectra", tables);
            Assert.Contains("RetentionTimes", tables);
            Assert.Contains("ScoreTypes", tables);
            Assert.Contains("SpectrumSourceFiles", tables);
            Assert.Contains("Proteins", tables);
            Assert.Contains("RefSpectraProteins", tables);
            Assert.Contains("RefSpectraPeaks", tables);
            Assert.Contains("Modifications", tables);
            Assert.Contains("IonMobilityTypes", tables);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
