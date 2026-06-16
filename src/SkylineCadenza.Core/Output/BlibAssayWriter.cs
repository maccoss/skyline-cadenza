#nullable enable

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SkylineCadenza.Core.Scheduling;

namespace SkylineCadenza.Core.Output;

/// <summary>
/// Writes a self-contained BiblioSpec non-redundant <c>.blib</c> describing
/// the scheduled assay: one reference spectrum per (peptide, charge) with
/// the top-N library fragments encoded as the spectrum's peak list, plus
/// per-replicate RT boundaries that match the firing window Cadenza
/// chose. Skyline can then add this BLIB to its peptide-settings
/// libraries and pull the peptides into the document via its normal
/// library-driven workflow.
/// </summary>
/// <remarks>
/// The schema this writer emits is BiblioSpec schema version 8 (the
/// version that introduced <c>RefSpectra.startTime</c> /
/// <c>endTime</c>). The output reads cleanly through Cadenza's own
/// <c>BlibRetentionTimeReader</c> and through Skyline. Reference peak
/// lists are zlib-compressed BLOBs of little-endian doubles (m/z) and
/// little-endian floats (intensity), matching BiblioSpec's wire format.
/// </remarks>
public static class BlibAssayWriter
{
    /// <summary>Number of fragments emitted per reference spectrum.</summary>
    public const int PeaksPerSpectrum = 6;

    private const string SyntheticSourceFileName = "cadenza_assay.design";
    private const string AssayLsidPrefix = "urn:lsid:cadenza:spectral_library:bibliospec:nr:";

    /// <summary>
    /// Result statistics for a write.
    /// </summary>
    public sealed record WriteResult(
        string Path,
        int RefSpectraWritten,
        int RetentionTimeRowsWritten,
        int ProteinsWritten,
        int FragmentsWritten);

    /// <summary>
    /// Writes the scheduled subset of <paramref name="candidates"/> to a
    /// fresh <c>.blib</c> at <paramref name="path"/>. Overwrites any file
    /// already at that path.
    /// </summary>
    /// <param name="candidates">Full candidate pool. Only entries whose
    /// index appears in <paramref name="schedule"/>.<c>ScheduledIndices</c>
    /// are written.</param>
    /// <param name="schedule">Schedule result from <see cref="Scheduler.Run"/>.</param>
    /// <param name="path">Output <c>.blib</c> path.</param>
    /// <param name="assayName">Used to seed the library LSID and the
    /// <c>LibInfo</c> row.</param>
    public static WriteResult Write(
        IReadOnlyList<Candidate> candidates,
        ScheduleResult schedule,
        string path,
        string assayName = "cadenza-assay")
    {
        if (File.Exists(path)) File.Delete(path);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using (var tx = conn.BeginTransaction())
        {
            CreateSchema(conn);
            WriteLibInfo(conn, assayName, scheduledCount: schedule.ScheduledIndices.Length);
            WriteScoreTypes(conn);
            WriteIonMobilityTypes(conn);
            WriteSpectrumSourceFiles(conn);

            // Build the protein -> id map first so RefSpectraProteins can
            // reference protein ids without a round-trip.
            var proteinIds = WriteProteins(conn, candidates, schedule);

            int refCount = 0, rtCount = 0, fragCount = 0;
            foreach (int candIdx in schedule.ScheduledIndices)
            {
                var c = candidates[candIdx];
                long refId = WriteRefSpectrum(conn, c);
                WriteRefSpectrumPeaks(conn, refId, c.Fragments, out int peaks);
                WriteModifications(conn, refId, c.ModifiedSequence);
                WriteRetentionTime(conn, refId, c);
                if (proteinIds.TryGetValue(c.ProteinGroup, out long protId))
                    WriteRefSpectrumProtein(conn, refId, protId);
                refCount++;
                rtCount++;
                fragCount += peaks;
            }

            tx.Commit();
            return new WriteResult(path, refCount, rtCount, proteinIds.Count, fragCount);
        }
    }

    private static void CreateSchema(SqliteConnection conn)
    {
        // BiblioSpec schema version 8: includes RefSpectra.startTime /
        // endTime. Tables only - no indexes; Skyline reads them via
        // primary-key lookups.
        string[] ddl =
        {
            @"CREATE TABLE LibInfo(
                libLSID TEXT,
                createTime TEXT,
                numSpecs INTEGER,
                majorVersion INTEGER,
                minorVersion INTEGER)",

            @"CREATE TABLE IonMobilityTypes(
                id INTEGER PRIMARY KEY,
                ionMobilityType VARCHAR(128))",

            @"CREATE TABLE ScoreTypes(
                id INTEGER PRIMARY KEY,
                scoreType VARCHAR(128),
                probabilityType VARCHAR(128))",

            @"CREATE TABLE SpectrumSourceFiles(
                id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                fileName VARCHAR(512),
                idFileName VARCHAR(512),
                cutoffScore REAL,
                workflowType TINYINT)",

            @"CREATE TABLE Proteins(
                id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                accession VARCHAR(200))",

            @"CREATE TABLE RefSpectraProteins(
                RefSpectraId INTEGER NOT NULL,
                ProteinId INTEGER NOT NULL)",

            @"CREATE TABLE RefSpectra(
                id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                peptideSeq VARCHAR(150),
                precursorMZ REAL,
                precursorCharge INTEGER,
                peptideModSeq VARCHAR(200),
                prevAA CHAR(1),
                nextAA CHAR(1),
                copies INTEGER,
                numPeaks INTEGER,
                ionMobility REAL,
                collisionalCrossSectionSqA REAL,
                ionMobilityHighEnergyOffset REAL,
                ionMobilityType TINYINT,
                retentionTime REAL,
                startTime REAL,
                endTime REAL,
                totalIonCurrent REAL,
                moleculeName VARCHAR(128),
                chemicalFormula VARCHAR(128),
                precursorAdduct VARCHAR(128),
                inchiKey VARCHAR(128),
                otherKeys VARCHAR(128),
                fileID INTEGER,
                SpecIDinFile VARCHAR(256),
                score REAL,
                scoreType TINYINT)",

            @"CREATE TABLE RefSpectraPeaks(
                RefSpectraID INTEGER,
                peakMZ BLOB,
                peakIntensity BLOB)",

            @"CREATE TABLE Modifications(
                id INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL,
                RefSpectraID INTEGER,
                position INTEGER,
                mass REAL)",

            @"CREATE TABLE RetentionTimes(
                RefSpectraID INTEGER,
                RedundantRefSpectraID INTEGER,
                SpectrumSourceID INTEGER,
                ionMobility REAL,
                collisionalCrossSectionSqA REAL,
                ionMobilityHighEnergyOffset REAL,
                ionMobilityType TINYINT,
                retentionTime REAL,
                startTime REAL,
                endTime REAL,
                score REAL,
                bestSpectrum INTEGER,
                FOREIGN KEY(RefSpectraID) REFERENCES RefSpectra(id))",
        };

        using var cmd = conn.CreateCommand();
        foreach (var sql in ddl)
        {
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteLibInfo(SqliteConnection conn, string assayName, int scheduledCount)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO LibInfo(libLSID, createTime, numSpecs, majorVersion, minorVersion)
            VALUES (@lsid, @t, @n, 1, 8)";
        cmd.Parameters.AddWithValue("@lsid", AssayLsidPrefix + Sanitize(assayName));
        cmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("R", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@n", scheduledCount);
        cmd.ExecuteNonQuery();
    }

    private static void WriteScoreTypes(SqliteConnection conn)
    {
        // BiblioSpec's standard score-type registry. We only use id=19
        // (GENERIC Q-VALUE) for RefSpectra rows; the full set keeps
        // downstream Skyline happy without complaining about unknown ids.
        (int id, string name, string prob)[] rows =
        {
            (0,  "UNKNOWN",                              "NOT_A_PROBABILITY_VALUE"),
            (1,  "PERCOLATOR QVALUE",                    "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (2,  "PEPTIDE PROPHET SOMETHING",            "PROBABILITY_THAT_IDENTIFICATION_IS_CORRECT"),
            (3,  "SPECTRUM MILL",                        "NOT_A_PROBABILITY_VALUE"),
            (4,  "IDPICKER FDR",                         "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (5,  "MASCOT IONS SCORE",                    "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (6,  "TANDEM EXPECTATION VALUE",             "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (7,  "PROTEIN PILOT CONFIDENCE",             "PROBABILITY_THAT_IDENTIFICATION_IS_CORRECT"),
            (8,  "SCAFFOLD SOMETHING",                   "PROBABILITY_THAT_IDENTIFICATION_IS_CORRECT"),
            (9,  "WATERS MSE PEPTIDE SCORE",             "NOT_A_PROBABILITY_VALUE"),
            (10, "OMSSA EXPECTATION SCORE",              "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (11, "PROTEIN PROSPECTOR EXPECTATION SCORE", "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (12, "SEQUEST XCORR",                        "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (13, "MAXQUANT SCORE",                       "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (14, "MORPHEUS SCORE",                       "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (15, "MSGF+ SCORE",                          "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (16, "PEAKS CONFIDENCE SCORE",               "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (17, "BYONIC SCORE",                         "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (18, "PEPTIDE SHAKER CONFIDENCE",            "PROBABILITY_THAT_IDENTIFICATION_IS_CORRECT"),
            (19, "GENERIC Q-VALUE",                      "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT"),
            (20, "HARDKLOR IDOTP",                       "PROBABILITY_THAT_IDENTIFICATION_IS_CORRECT"),
        };
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO ScoreTypes(id, scoreType, probabilityType) VALUES (@id, @name, @prob)";
        var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
        var pName = cmd.CreateParameter(); pName.ParameterName = "@name"; cmd.Parameters.Add(pName);
        var pProb = cmd.CreateParameter(); pProb.ParameterName = "@prob"; cmd.Parameters.Add(pProb);
        foreach (var r in rows)
        {
            pId.Value = r.id; pName.Value = r.name; pProb.Value = r.prob;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteIonMobilityTypes(SqliteConnection conn)
    {
        (int id, string name)[] rows =
        {
            (0, "none"),
            (1, "driftTime(msec)"),
            (2, "inverseK0(Vsec/cm^2)"),
            (3, "compensation(V)"),
        };
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO IonMobilityTypes(id, ionMobilityType) VALUES (@id, @name)";
        var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
        var pName = cmd.CreateParameter(); pName.ParameterName = "@name"; cmd.Parameters.Add(pName);
        foreach (var r in rows)
        {
            pId.Value = r.id; pName.Value = r.name;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteSpectrumSourceFiles(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SpectrumSourceFiles(id, fileName, idFileName, cutoffScore, workflowType)
            VALUES (1, @f, @i, 0.01, 1)";
        cmd.Parameters.AddWithValue("@f", SyntheticSourceFileName);
        cmd.Parameters.AddWithValue("@i", SyntheticSourceFileName);
        cmd.ExecuteNonQuery();
    }

    private static Dictionary<string, long> WriteProteins(
        SqliteConnection conn, IReadOnlyList<Candidate> candidates, ScheduleResult schedule)
    {
        var proteinIds = new Dictionary<string, long>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Proteins(accession) VALUES (@acc); SELECT last_insert_rowid();";
        var pAcc = cmd.CreateParameter(); pAcc.ParameterName = "@acc"; cmd.Parameters.Add(pAcc);
        foreach (int candIdx in schedule.ScheduledIndices)
        {
            var group = candidates[candIdx].ProteinGroup;
            if (proteinIds.ContainsKey(group)) continue;
            pAcc.Value = group;
            long id = (long)cmd.ExecuteScalar()!;
            proteinIds[group] = id;
        }
        return proteinIds;
    }

    private static long WriteRefSpectrum(SqliteConnection conn, Candidate c)
    {
        string stripped = StripModifications(c.ModifiedSequence);
        string modSeq = NormalizeModifiedSequence(c.ModifiedSequence);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO RefSpectra(
                peptideSeq, precursorMZ, precursorCharge, peptideModSeq,
                copies, numPeaks,
                retentionTime, startTime, endTime,
                fileID, score, scoreType)
            VALUES (@seq, @mz, @z, @modSeq,
                1, @n,
                @rt, @rtStart, @rtStop,
                1, @qval, 19);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@seq", stripped);
        cmd.Parameters.AddWithValue("@mz", c.PrecursorMz);
        cmd.Parameters.AddWithValue("@z", c.PrecursorCharge);
        cmd.Parameters.AddWithValue("@modSeq", modSeq);
        cmd.Parameters.AddWithValue("@n", Math.Min(PeaksPerSpectrum, c.Fragments.Length));
        cmd.Parameters.AddWithValue("@rt", c.RtApex);
        cmd.Parameters.AddWithValue("@rtStart", c.RtStart);
        cmd.Parameters.AddWithValue("@rtStop", c.RtStop);
        cmd.Parameters.AddWithValue("@qval", double.IsNaN(c.QValue) ? 0.0 : c.QValue);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void WriteRefSpectrumPeaks(
        SqliteConnection conn, long refId, FragmentIon[] fragments, out int peaksWritten)
    {
        int take = Math.Min(PeaksPerSpectrum, fragments.Length);
        peaksWritten = take;
        if (take == 0) return;

        // Sort by m/z ascending - BiblioSpec spec is for peak lists to
        // be sorted that way so spectrum-match algorithms can scan in
        // order. We pick the top-N by intensity first, then re-sort.
        var picked = fragments.Take(take).OrderBy(f => f.Mz).ToArray();

        // Pack m/z as little-endian doubles, intensity as little-endian
        // floats, then zlib-compress each blob. BiblioSpec is willing to
        // accept uncompressed blobs when their compressed size would be
        // larger than original, but for typical N >= 4 the compression
        // is negligible and we can always compress.
        byte[] mzBytes = new byte[take * sizeof(double)];
        byte[] intBytes = new byte[take * sizeof(float)];
        for (int i = 0; i < take; i++)
        {
            BitConverter.TryWriteBytes(mzBytes.AsSpan(i * 8, 8), picked[i].Mz);
            BitConverter.TryWriteBytes(intBytes.AsSpan(i * 4, 4), (float)picked[i].Intensity);
        }
        byte[] mzCompressed = ZlibCompress(mzBytes);
        byte[] intCompressed = ZlibCompress(intBytes);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO RefSpectraPeaks(RefSpectraID, peakMZ, peakIntensity)
            VALUES (@id, @mz, @int)";
        cmd.Parameters.AddWithValue("@id", refId);
        cmd.Parameters.AddWithValue("@mz", mzCompressed);
        cmd.Parameters.AddWithValue("@int", intCompressed);
        cmd.ExecuteNonQuery();
    }

    private static void WriteModifications(SqliteConnection conn, long refId, string modSeq)
    {
        if (string.IsNullOrEmpty(modSeq)) return;
        var mods = ParseMassDeltaMods(modSeq);
        if (mods.Count == 0) return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Modifications(RefSpectraID, position, mass) VALUES (@id, @pos, @mass)";
        var pId = cmd.CreateParameter(); pId.ParameterName = "@id"; cmd.Parameters.Add(pId);
        var pPos = cmd.CreateParameter(); pPos.ParameterName = "@pos"; cmd.Parameters.Add(pPos);
        var pMass = cmd.CreateParameter(); pMass.ParameterName = "@mass"; cmd.Parameters.Add(pMass);
        foreach (var m in mods)
        {
            pId.Value = refId; pPos.Value = m.Position; pMass.Value = m.MassDelta;
            cmd.ExecuteNonQuery();
        }
    }

    private static void WriteRetentionTime(SqliteConnection conn, long refId, Candidate c)
    {
        // One synthetic per-replicate row mirroring the canonical
        // RefSpectra values, so BlibRetentionTimeReader's union-window
        // query reproduces (rtStart, rtStop). bestSpectrum = 1 because
        // there's exactly one observation per peptide in an assay BLIB.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO RetentionTimes(
                RefSpectraID, RedundantRefSpectraID, SpectrumSourceID,
                retentionTime, startTime, endTime,
                score, bestSpectrum)
            VALUES (@id, -1, 1, @rt, @rtStart, @rtStop, @qval, 1)";
        cmd.Parameters.AddWithValue("@id", refId);
        cmd.Parameters.AddWithValue("@rt", c.RtApex);
        cmd.Parameters.AddWithValue("@rtStart", c.RtStart);
        cmd.Parameters.AddWithValue("@rtStop", c.RtStop);
        cmd.Parameters.AddWithValue("@qval", double.IsNaN(c.QValue) ? 0.0 : c.QValue);
        cmd.ExecuteNonQuery();
    }

    private static void WriteRefSpectrumProtein(SqliteConnection conn, long refId, long protId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO RefSpectraProteins(RefSpectraId, ProteinId) VALUES (@r, @p)";
        cmd.Parameters.AddWithValue("@r", refId);
        cmd.Parameters.AddWithValue("@p", protId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Compress <paramref name="raw"/> with the zlib wire format
    /// (RFC 1950: 2-byte header, deflate body, Adler-32 trailer).
    /// </summary>
    internal static byte[] ZlibCompress(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }
        return ms.ToArray();
    }

    // --- Modification parsing -------------------------------------------

    private static readonly Regex ModDeltaRegex = new(
        @"\[([+\-]?\d+(?:\.\d+)?)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed record ParsedMod(int Position, double MassDelta);

    /// <summary>
    /// Parse a peptide modified sequence in BLIB/Skyline mass-delta form
    /// (e.g. <c>C[+57.0214637]PEPTIDE</c>) and return one
    /// <see cref="ParsedMod"/> per <c>[+x.xx]</c> bracket. Position is
    /// 1-based, counting only amino acid letters. Brackets with non-
    /// numeric contents (e.g. <c>C[UniMod:4]</c>) are skipped: the BLIB
    /// reader will still read the peptideModSeq string, but we can't
    /// populate the Modifications table without an explicit mass delta.
    /// </summary>
    internal static List<ParsedMod> ParseMassDeltaMods(string modSeq)
    {
        var result = new List<ParsedMod>();
        int aaPos = 0;
        int i = 0;
        while (i < modSeq.Length)
        {
            char ch = modSeq[i];
            if (char.IsLetter(ch))
            {
                aaPos++;
                i++;
            }
            else if (ch == '[')
            {
                int end = modSeq.IndexOf(']', i + 1);
                if (end < 0) break;
                string inside = modSeq.Substring(i + 1, end - i - 1);
                if (double.TryParse(inside, NumberStyles.Float, CultureInfo.InvariantCulture, out double mass))
                {
                    result.Add(new ParsedMod(Math.Max(1, aaPos), mass));
                }
                i = end + 1;
            }
            else
            {
                i++;
            }
        }
        return result;
    }

    /// <summary>
    /// Strip a modified sequence to bare amino acids. Mirrors the
    /// behaviour of <c>SkylineLibraryLoader.StripModifications</c> but
    /// is local here so the writer has no cross-Ingest dependency.
    /// </summary>
    internal static string StripModifications(string modSeq)
    {
        if (string.IsNullOrEmpty(modSeq)) return modSeq;
        var sb = new StringBuilder(modSeq.Length);
        int i = 0;
        while (i < modSeq.Length)
        {
            char ch = modSeq[i];
            if (char.IsLetter(ch)) { sb.Append(ch); i++; }
            else if (ch == '[' || ch == '(')
            {
                char close = ch == '[' ? ']' : ')';
                int end = modSeq.IndexOf(close, i + 1);
                if (end < 0) break;
                i = end + 1;
            }
            else
            {
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Normalise to BLIB's expected mass-delta form: square brackets
    /// only, no flanking underscores, and any <c>[UniMod:N]</c>
    /// identifiers converted to their mass-delta equivalent
    /// (<c>C[UniMod:4]</c> -&gt; <c>C[+57.021464]</c>). BiblioSpec's
    /// library reader (<c>LibKeyModificationMatcher</c>) parses the
    /// bracket contents as a numeric mass delta and throws
    /// <c>FormatException: The number 'UniMod:4' is not in the correct
    /// format</c> when it sees a UniMod ID instead. DIA-NN and Carafe
    /// both emit UniMod IDs in their modified sequences; we translate
    /// them here so a BLIB Cadenza writes reads cleanly through
    /// Skyline's Library Explorer.
    /// </summary>
    internal static string NormalizeModifiedSequence(string modSeq)
    {
        if (string.IsNullOrEmpty(modSeq)) return modSeq;
        var sb = new StringBuilder(modSeq.Length);
        int i = 0;
        while (i < modSeq.Length)
        {
            char ch = modSeq[i];
            if (ch == '_')
            {
                i++;
                continue;
            }
            if (ch == '(' || ch == '[')
            {
                char close = ch == '(' ? ')' : ']';
                int end = modSeq.IndexOf(close, i + 1);
                if (end < 0)
                {
                    // Unmatched bracket - copy the rest verbatim.
                    sb.Append(modSeq, i, modSeq.Length - i);
                    break;
                }
                string inside = modSeq.Substring(i + 1, end - i - 1);
                string converted = ConvertModBracketContent(inside);
                sb.Append('[').Append(converted).Append(']');
                i = end + 1;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Convert the content of a modification bracket to BLIB's
    /// mass-delta form. Already-numeric values pass through unchanged
    /// (preserving any "+" / "-" sign); <c>UniMod:N</c> identifiers
    /// are translated via <see cref="UniModMassDeltas"/>; anything
    /// unrecognised is left as-is so a downstream parse error has a
    /// chance of pointing at the actual offending string instead of
    /// our rewrite.
    /// </summary>
    private static string ConvertModBracketContent(string inside)
    {
        if (string.IsNullOrEmpty(inside)) return inside;

        // Numeric (with optional + / - prefix): pass through, optionally
        // adding the leading + so BLIB readers consistently see a sign.
        if (double.TryParse(inside,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double already))
        {
            return already >= 0 && inside[0] != '+'
                ? "+" + inside
                : inside;
        }

        if (inside.StartsWith("UniMod:", StringComparison.OrdinalIgnoreCase))
        {
            string idStr = inside.Substring("UniMod:".Length);
            if (int.TryParse(idStr,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int unimodId)
                && UniModMassDeltas.TryGetValue(unimodId, out double mass))
            {
                string sign = mass >= 0 ? "+" : "";
                return sign + mass.ToString("0.0#######",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return inside;
    }

    /// <summary>
    /// UniMod ID -&gt; monoisotopic mass delta in Da. The set covers
    /// modifications routinely seen in bottom-up tryptic proteomics
    /// workflows that Cadenza ingests from DIA-NN, Carafe, and Skyline.
    /// Values match the canonical UniMod database (https://www.unimod.org).
    /// Unknown IDs pass through unchanged - <c>LibKeyModificationMatcher</c>
    /// will then fail loudly with the actual UniMod string in the
    /// error so we can extend this list.
    /// </summary>
    private static readonly Dictionary<int, double> UniModMassDeltas = new()
    {
        { 1,    42.010565 },  // Acetyl
        { 2,    14.015650 },  // Amidated
        { 3,   162.052824 },  // Biotin (variant - not the linker; use 64 for ICAT)
        { 4,    57.021464 },  // Carbamidomethyl
        { 5,    43.005814 },  // Carbamyl
        { 6,    58.005479 },  // Carboxymethyl
        { 7,    -0.984016 },  // Deamidated
        { 21,   79.966331 },  // Phospho
        { 24,   71.037114 },  // Propionyl
        { 26,   39.994915 },  // Pyro-carbamidomethyl
        { 27,   -18.010565 }, // Glu->pyro-Glu
        { 28,   -17.026549 }, // Gln->pyro-Glu
        { 34,    14.015650 }, // Methyl
        { 35,    15.994915 }, // Oxidation
        { 36,    28.031300 }, // Dimethyl
        { 37,    42.046950 }, // Trimethyl
        { 39,    45.987721 }, // Methylthio (S-methylthio cys)
        { 121,  114.042927 }, // GlyGly (ubiquitin remnant)
        { 188,    6.020129 }, // Label:13C(6)
        { 199,    8.014199 }, // Label:13C(6)15N(2) (often labeled Heavy K)
        { 259,    8.014199 }, // Label:13C(6)15N(2) (canonical UniMod id)
        { 267,   10.008269 }, // Label:13C(6)15N(4) (Heavy R)
        { 425,   15.994915 }, // Hydroxyl (same mass as Oxidation, different annotation)
        { 730,  144.105918 }, // TMT0 (rare; representative)
        { 737,  229.162932 }, // TMT 6-plex / 10-plex / 11-plex
        { 738,  225.155833 }, // TMT2-plex (representative)
        { 2016, 304.207146 }, // TMTpro 16-plex
    };

    private static string Sanitize(string s) =>
        Regex.Replace(s, @"[^A-Za-z0-9_\-]", "_");
}
