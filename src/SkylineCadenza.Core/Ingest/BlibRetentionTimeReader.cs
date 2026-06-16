using Microsoft.Data.Sqlite;

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Reads per-peptide chromatographic peak boundaries from a BiblioSpec
/// <c>.blib</c> file. The boundaries Cadenza uses for the firing window
/// are the UNION of every replicate's measured peak edges, taken from
/// the BLIB's <c>RetentionTimes</c> table (per-peptide, per-source-file
/// observations).
/// </summary>
/// <remarks>
/// BiblioSpec stores per-replicate observations in <c>RetentionTimes</c>
/// (one row per peptide / source-file). For Cadenza's union firing
/// window we want <c>MIN(startTime)</c> and <c>MAX(endTime)</c> across
/// every replicate, NOT a single canonical observation. The
/// <c>bestSpectrum</c> flag identifies BiblioSpec's chosen-as-canonical
/// row per peptide and is deliberately not used as a filter here.
///
/// The <c>score</c> column on each <c>RetentionTimes</c> row carries
/// the identification confidence for that replicate. The
/// <c>ScoreTypes</c> table documents its semantics:
/// <list type="bullet">
/// <item><c>PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT</c> (q-values,
/// FDRs): smaller is better, filter to <c>score &lt;= cutoff</c>.</item>
/// <item><c>PROBABILITY_THAT_IDENTIFICATION_IS_CORRECT</c>: larger is
/// better, filter to <c>score &gt;= 1 - cutoff</c>.</item>
/// <item><c>NOT_A_PROBABILITY_VALUE</c>: no filter applied.</item>
/// </list>
///
/// BLIB peak boundaries already include per-replicate peak-shape
/// variance; <see cref="Scheduling.SchedulingParameters.FiringPadSec"/>
/// adds extra robustness on top, not the primary buffer.
/// </remarks>
public static class BlibRetentionTimeReader
{
    /// <summary>
    /// Per-peptide aggregated firing window.
    /// </summary>
    public sealed record PeakBoundary(
        double RtStart,
        double RtStop,
        int ReplicateCount);

    /// <summary>
    /// What a single BLIB contributed.
    /// </summary>
    public sealed class Library
    {
        /// <summary>Absolute path to the BLIB file on disk.</summary>
        public required string Path { get; init; }

        /// <summary>Per-(peptideModSeq, charge) firing window.</summary>
        public required IReadOnlyDictionary<(string ModSeq, int Charge), PeakBoundary> Boundaries { get; init; }

        /// <summary>
        /// Fraction of peptides whose RetentionTimes rows showed
        /// observable variance (max(endTime) - min(startTime) wider
        /// than 0.01 min). Measured libraries (DIA-NN, PRM) have a
        /// high score (close to 1); single-prediction libraries
        /// (Prosit) have a near-zero score.
        /// </summary>
        public required double VarianceScore { get; init; }
    }

    /// <summary>
    /// Open the BLIB at <paramref name="blibPath"/> read-only and return
    /// the per-peptide union firing windows after filtering out
    /// observations whose identification confidence falls below
    /// <paramref name="qValueCutoff"/>.
    /// </summary>
    /// <param name="blibPath">Absolute path to a <c>.blib</c> file.</param>
    /// <param name="qValueCutoff">Identification confidence threshold,
    /// applied per replicate. Matches
    /// <see cref="Scheduling.SchedulingParameters.QValueCutoff"/> so
    /// the same threshold drives ingest filtering and the per-replicate
    /// boundary filter.</param>
    public static Library Read(string blibPath, double qValueCutoff)
    {
        if (!File.Exists(blibPath))
            throw new FileNotFoundException("BLIB not found.", blibPath);

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = blibPath,
            Mode = SqliteOpenMode.ReadOnly,
            // Don't pool: see BlibAssayWriter for the rationale. The
            // reader is a one-shot read of a BLIB; callers may delete
            // the file (e.g. tests) immediately afterwards.
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        bool hasBoundaryColumns = HasStartEndColumns(conn);
        if (!hasBoundaryColumns)
        {
            return new Library
            {
                Path = blibPath,
                Boundaries = new Dictionary<(string, int), PeakBoundary>(),
                VarianceScore = 0.0,
            };
        }

        // Map each scoreType id to its filter predicate. We don't try
        // to interpret "NOT_A_PROBABILITY_VALUE" types like XCORR or
        // raw search scores - the threshold has no meaning there and
        // we just accept every row.
        var scoreTypeDirection = ReadScoreDirections(conn);

        // The aggregation is per-peptide (RefSpectraID); we then project
        // out to (modSeq, charge) using RefSpectra. The predicate on
        // score is parameterised per scoreType id so a single BLIB can
        // (in theory) mix scoreTypes across rows.
        var boundaries = new Dictionary<(string, int), PeakBoundary>();

        // Group RefSpectra by scoreType so we can apply the right
        // predicate to each group. In practice every row in a BLIB has
        // the same scoreType, but the schema does not enforce that.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT rs.id, rs.peptideModSeq, rs.precursorCharge, rs.scoreType
              FROM RefSpectra rs
             WHERE rs.peptideModSeq IS NOT NULL
               AND rs.precursorCharge IS NOT NULL";

        var refRows = new List<(long Id, string ModSeq, int Charge, int ScoreType)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.IsDBNull(1) || reader.IsDBNull(2)) continue;
                refRows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3)));
            }
        }

        // Pre-compute the per-scoreType score predicate as a SQL fragment.
        // Cached on first use to avoid string concat per row.
        int variancePeptides = 0;
        foreach (var (id, modSeq, charge, scoreType) in refRows)
        {
            var direction = scoreTypeDirection.GetValueOrDefault(scoreType, ScoreDirection.NoFilter);
            string scorePredicate = direction switch
            {
                ScoreDirection.LowerIsBetter => " AND (rt.score IS NULL OR rt.score <= @cutoff)",
                ScoreDirection.HigherIsBetter => " AND (rt.score IS NULL OR rt.score >= 1.0 - @cutoff)",
                _ => string.Empty,
            };

            using var aggCmd = conn.CreateCommand();
            aggCmd.CommandText = @"
                SELECT MIN(rt.startTime), MAX(rt.endTime),
                       COUNT(DISTINCT rt.SpectrumSourceID)
                  FROM RetentionTimes rt
                 WHERE rt.RefSpectraID = @id
                   AND rt.startTime IS NOT NULL
                   AND rt.endTime IS NOT NULL"
                + scorePredicate;
            aggCmd.Parameters.AddWithValue("@id", id);
            if (scorePredicate.Length > 0)
                aggCmd.Parameters.AddWithValue("@cutoff", qValueCutoff);

            using var ar = aggCmd.ExecuteReader();
            if (!ar.Read()) continue;
            if (ar.IsDBNull(0) || ar.IsDBNull(1)) continue;

            double start = ar.GetDouble(0);
            double stop = ar.GetDouble(1);
            int n = ar.IsDBNull(2) ? 0 : ar.GetInt32(2);
            if (n == 0) continue;

            boundaries[(modSeq, charge)] = new PeakBoundary(start, stop, n);
            if (stop - start > 0.01) variancePeptides++;
        }

        double variance = boundaries.Count == 0
            ? 0.0
            : (double)variancePeptides / boundaries.Count;

        return new Library
        {
            Path = blibPath,
            Boundaries = boundaries,
            VarianceScore = variance,
        };
    }

    private enum ScoreDirection
    {
        NoFilter,
        LowerIsBetter,
        HigherIsBetter,
    }

    private static Dictionary<int, ScoreDirection> ReadScoreDirections(SqliteConnection conn)
    {
        var map = new Dictionary<int, ScoreDirection>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, probabilityType FROM ScoreTypes";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int id = reader.GetInt32(0);
            string pt = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            map[id] = pt switch
            {
                "PROBABILITY_THAT_IDENTIFICATION_IS_INCORRECT" => ScoreDirection.LowerIsBetter,
                "PROBABILITY_THAT_IDENTIFICATION_IS_CORRECT" => ScoreDirection.HigherIsBetter,
                _ => ScoreDirection.NoFilter,
            };
        }
        return map;
    }

    private static bool HasStartEndColumns(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(RetentionTimes)";
        using var reader = cmd.ExecuteReader();
        bool hasStart = false, hasEnd = false;
        while (reader.Read())
        {
            string name = reader.GetString(1);
            if (name == "startTime") hasStart = true;
            if (name == "endTime") hasEnd = true;
        }
        return hasStart && hasEnd;
    }
}
