using SkylineCadenza.Core.Parsimony;
using Xunit;

namespace SkylineCadenza.Tests;

public class ParsimonyEngineTests
{
    [Fact]
    public void Subsumed_protein_drops_out_and_its_peptides_become_unique_to_the_subsumer()
    {
        // P1 has {A, B}; P2 has {A, B, C}. P1's peptide set is a strict subset
        // of P2's, so P1 is subsumed and removed. A, B, C all become unique to P2.
        var input = new Dictionary<string, IReadOnlyList<string>>
        {
            ["A"] = new[] { "P1", "P2" },
            ["B"] = new[] { "P1", "P2" },
            ["C"] = new[] { "P2" },
        };

        var result = ParsimonyEngine.Assign(input);

        Assert.Equal(("P2", "unique"), result["A"]);
        Assert.Equal(("P2", "unique"), result["B"]);
        Assert.Equal(("P2", "unique"), result["C"]);
    }

    [Fact]
    public void Indistinguishable_proteins_collapse_to_the_lexicographically_smallest_canonical_id()
    {
        // P1 and P2 have identical peptide sets {A, B}. They collapse into one
        // group; the canonical id is min("P1","P2") = "P1".
        var input = new Dictionary<string, IReadOnlyList<string>>
        {
            ["A"] = new[] { "P1", "P2" },
            ["B"] = new[] { "P1", "P2" },
        };

        var result = ParsimonyEngine.Assign(input);

        Assert.Equal(("P1", "unique"), result["A"]);
        Assert.Equal(("P1", "unique"), result["B"]);
    }

    [Fact]
    public void Razor_peptide_goes_to_the_group_with_more_unique_peptides_ties_broken_by_lower_id()
    {
        // P1 peptides {A, B, C}; P2 peptides {A, D}. After parsimony both remain
        // active (neither subsumes the other). B and C are unique to P1; D is
        // unique to P2; A is shared (razor). P1 has 2 unique peptides vs P2's 1,
        // so the razor A goes to P1.
        var input = new Dictionary<string, IReadOnlyList<string>>
        {
            ["A"] = new[] { "P1", "P2" },
            ["B"] = new[] { "P1" },
            ["C"] = new[] { "P1" },
            ["D"] = new[] { "P2" },
        };

        var result = ParsimonyEngine.Assign(input);

        Assert.Equal(("P1", "razor"), result["A"]);
        Assert.Equal(("P1", "unique"), result["B"]);
        Assert.Equal(("P1", "unique"), result["C"]);
        Assert.Equal(("P2", "unique"), result["D"]);
    }

    [Fact]
    public void Razor_ties_on_unique_count_break_to_the_lower_canonical_id()
    {
        // Both groups have 1 unique peptide each; razor A goes to P1 (lexicographic).
        var input = new Dictionary<string, IReadOnlyList<string>>
        {
            ["A"] = new[] { "P1", "P2" },
            ["B"] = new[] { "P1" },
            ["C"] = new[] { "P2" },
        };

        var result = ParsimonyEngine.Assign(input);

        Assert.Equal(("P1", "razor"), result["A"]);
    }

    [Fact]
    public void FindSubsumable_matches_inverted_index_against_a_pairwise_reference()
    {
        // Cross-check the fast inverted-index subsumption finder against a
        // direct O(N^2) reference on a small randomised instance. They must
        // agree on every protein.
        var random = new Random(42);
        var prots = new Dictionary<string, HashSet<string>>();
        for (int p = 0; p < 30; p++)
        {
            var peps = new HashSet<string>();
            int n = 3 + random.Next(8);
            for (int j = 0; j < n; j++)
                peps.Add($"pep{random.Next(40)}");
            prots[$"P{p:D2}"] = peps;
        }

        var fast = ParsimonyEngine.FindSubsumable(prots);
        var slow = ReferenceSubsumable(prots);

        Assert.Equal(slow, fast);
    }

    private static Dictionary<string, string> ReferenceSubsumable(
        IReadOnlyDictionary<string, HashSet<string>> prots)
    {
        // Reference: for each A, sort all proteins, find the smallest B
        // whose peptide set is a strict superset of A's.
        var sorted = prots.Keys.OrderBy(p => p, StringComparer.Ordinal).ToList();
        var result = new Dictionary<string, string>();
        foreach (var protA in sorted)
        {
            var pepsA = prots[protA];
            if (pepsA.Count == 0) continue;
            foreach (var protB in sorted)
            {
                if (protB == protA) continue;
                var pepsB = prots[protB];
                if (pepsB.Count > pepsA.Count && pepsA.IsSubsetOf(pepsB))
                {
                    result[protA] = protB;
                    break;
                }
            }
        }
        return result;
    }
}
