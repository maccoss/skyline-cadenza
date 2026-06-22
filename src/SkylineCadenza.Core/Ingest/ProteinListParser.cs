#nullable enable

namespace SkylineCadenza.Core.Ingest;

/// <summary>
/// Parses a user-supplied target-protein list. Accepts:
///   - a FASTA file: each <c>&gt;</c> header line yields one accession;
///   - free-form text or a plain text file: one token per line, where each
///     token is an accession (e.g. <c>P55011</c>), a UniProt-style id
///     (e.g. <c>sp|P55011|S12A2_HUMAN</c>), or a gene symbol (e.g. <c>SLC12A2</c>).
///
/// Tokens that look like gene symbols can be resolved to accessions via the
/// gene-&gt;accession map built from the DIA-NN report's <c>Genes</c> and
/// <c>Protein.Ids</c> columns - see <see cref="ResolveGenes"/>.
/// </summary>
public static class ProteinListParser
{
    /// <summary>
    /// Parse an arbitrary input string. Returns the parsed tokens in the
    /// order they appeared (de-duplicated). FASTA is detected by the
    /// leading <c>&gt;</c> on the first non-blank line.
    /// </summary>
    public static List<string> ParseText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();

        // Detect FASTA: first non-blank line starts with '>'
        bool isFasta = false;
        foreach (var line in text.Split('\n'))
        {
            string t = line.TrimStart();
            if (t.Length == 0) continue;
            isFasta = t[0] == '>';
            break;
        }

        return isFasta ? ParseFastaText(text) : ParsePlainText(text);
    }

    public static List<string> ParseFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Target protein list not found", path);
        return ParseText(File.ReadAllText(path));
    }

    private static List<string> ParseFastaText(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r').TrimStart();
            if (line.Length == 0 || line[0] != '>') continue;
            // Pull the first whitespace-delimited token after '>'.
            string header = line.Substring(1).TrimStart();
            int wsAt = IndexOfAny(header, WsChars);
            string id = wsAt < 0 ? header : header.Substring(0, wsAt);
            string accession = ExtractAccession(id);
            if (seen.Add(accession)) result.Add(accession);
        }
        return result;
    }

    private static List<string> ParsePlainText(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in text.Split(LineSplits, StringSplitOptions.None))
        {
            string token = raw.Trim().TrimEnd(',', ';');
            if (token.Length == 0) continue;
            string accession = ExtractAccession(token);
            if (seen.Add(accession)) result.Add(accession);
        }
        return result;
    }

    /// <summary>
    /// Resolve any gene-symbol entries in <paramref name="tokens"/> to
    /// accession ids using <paramref name="geneToAccession"/> (built from
    /// the DIA-NN report). Tokens already present as accessions in
    /// <paramref name="knownAccessions"/> pass through untouched.
    /// Unresolved tokens drop out.
    /// </summary>
    public static List<string> ResolveGenes(
        IEnumerable<string> tokens,
        IReadOnlyDictionary<string, IReadOnlyList<string>> geneToAccession,
        ISet<string> knownAccessions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in tokens)
        {
            if (knownAccessions.Contains(raw))
            {
                if (seen.Add(raw)) result.Add(raw);
                continue;
            }
            if (geneToAccession.TryGetValue(raw, out var accs))
            {
                foreach (var acc in accs)
                    if (seen.Add(acc)) result.Add(acc);
                continue;
            }
            // Otherwise treat the token itself as an accession - caller can
            // re-check membership.
            if (seen.Add(raw)) result.Add(raw);
        }
        return result;
    }

    /// <summary>
    /// Build a <c>gene -&gt; accessions</c> map from DIA-NN rows. Both the
    /// <c>Genes</c> and <c>Protein.Ids</c> columns are semicolon-separated.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<string>> BuildGeneToAccession(
        IEnumerable<DiannRow> rows)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var genes = row.Genes.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var accs = row.ProteinIds.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var g in genes)
            {
                var gene = g.Trim();
                if (gene.Length == 0) continue;
                if (!map.TryGetValue(gene, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    map[gene] = set;
                }
                foreach (var a in accs)
                {
                    var acc = a.Trim();
                    if (acc.Length > 0) set.Add(acc);
                }
            }
        }
        return map.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value.ToList());
    }

    /// <summary>
    /// Extract the bare accession from a UniProt-style identifier such as
    /// <c>sp|P55011|S12A2_HUMAN</c> or <c>tr|A0A075B5J9|...</c>. Returns the
    /// input unchanged if it doesn't look like that format. Exposed so the
    /// scheduler's target-list filter can normalise both sides of the
    /// comparison when a candidate's protein group is a full Skyline
    /// protein name and the target list is bare accessions (or vice
    /// versa).
    /// </summary>
    public static string ExtractAccession(string id)
    {
        if (id.Length > 3 && (id.StartsWith("sp|", StringComparison.Ordinal) ||
                              id.StartsWith("tr|", StringComparison.Ordinal)))
        {
            int firstPipe = id.IndexOf('|');
            int secondPipe = id.IndexOf('|', firstPipe + 1);
            if (secondPipe > firstPipe + 1)
                return id.Substring(firstPipe + 1, secondPipe - firstPipe - 1);
        }
        return id;
    }

    private static readonly char[] WsChars = { ' ', '\t' };
    private static readonly string[] LineSplits = { "\n", "\r\n", "\r", ",", ";", "|" };

    private static int IndexOfAny(string s, char[] any)
    {
        for (int i = 0; i < s.Length; i++)
            foreach (var c in any)
                if (s[i] == c) return i;
        return -1;
    }
}
