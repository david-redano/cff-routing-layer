// Ranking/FeatureAlignmentRanker.cs
namespace CffRoutingLayerDemo.Ranking;

using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Scores candidates by weighted feature alignment.
/// Deterministic, sub-millisecond, fully debuggable.
/// Used when LLM is unavailable or when candidates are clearly differentiated.
/// </summary>
public sealed class FeatureAlignmentRanker : IPlanRanker
{
    public Task<RankingResult> RankAsync(QueryIntent intent, IReadOnlyList<CandidateResult> candidates)
    {
        if (candidates.Count == 0)
        {
            return Task.FromResult(new RankingResult
            {
                Candidates    = [],
                Reasoning     = "No candidates to rank",
                TopConfidence = 0f,
                IsAmbiguous   = false
            });
        }

        if (candidates.Count == 1)
        {
            return Task.FromResult(new RankingResult
            {
                Candidates = [new RankedCandidate
                {
                    Plan        = candidates[0].Plan,
                    Score       = candidates[0].AlignmentScore,
                    Explanation = $"Only candidate. Matched: [{string.Join(", ", candidates[0].MatchedFeatures)}]"
                }],
                Reasoning     = "Single candidate — no ranking needed",
                TopConfidence = candidates[0].AlignmentScore,
                IsAmbiguous   = false
            });
        }

        var ranked = candidates
            .Select(c => new RankedCandidate
            {
                Plan        = c.Plan,
                Score       = ComputeRankingScore(c, intent),
                Explanation = BuildExplanation(c)
            })
            .OrderByDescending(r => r.Score)
            .ToList();

        var top1 = ranked[0].Score;
        var top2 = ranked.Count > 1 ? ranked[1].Score : 0f;
        var isAmbiguous = (top1 - top2) < 0.1f;

        return Task.FromResult(new RankingResult
        {
            Candidates    = ranked,
            Reasoning     = isAmbiguous
                ? $"Top 2 candidates within 0.10 of each other — ambiguous"
                : $"Clear winner: {ranked[0].Plan.PlanId} (gap: {top1 - top2:F2})",
            TopConfidence = top1,
            IsAmbiguous   = isAmbiguous
        });
    }

    private static float ComputeRankingScore(CandidateResult candidate, QueryIntent intent)
    {
        var baseScore = candidate.AlignmentScore;

        // Penalty for unmet entity requirements
        var unmetCount = candidate.MismatchedFeatures.Count(f => f.StartsWith("entities:"));
        baseScore -= unmetCount * 0.15f;

        // Bonus for output field relevance (up to +0.08)
        var outputOverlap = ComputeOutputRelevance(candidate, intent);

        // Bonus for description / sample-query text overlap (up to +0.20)
        // High description relevance signals the plan was written for this exact query pattern;
        // reward it enough to push clear matches above ExecuteThreshold without deflating the base.
        var descOverlap = ComputeDescriptionRelevance(candidate, intent);

        baseScore = baseScore + outputOverlap * 0.08f + descOverlap * 0.20f;

        return Math.Clamp(baseScore, 0f, 1f);
    }

    private static float ComputeOutputRelevance(CandidateResult candidate, QueryIntent intent)
    {
        if (candidate.Plan.Features is null) return 0.5f;
        // Expand camelCase field names into constituent words before matching.
        // e.g. "TopCustomerBalance" → {"top", "customer", "balance"}
        // This prevents "customer" matching inside "topcustomer" or "totalcustomers".
        var outputWords = candidate.Plan.Features.OutputFieldNames
            .SelectMany(SplitCamelCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queryTokens = intent.NormalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = queryTokens.Count(t => t.Length > 3 && outputWords.Contains(t));
        return Math.Min(1f, matches * 0.2f);
    }

    /// <summary>Splits a camelCase or PascalCase identifier into lowercase words.</summary>
    private static IEnumerable<string> SplitCamelCase(string input)
    {
        var words = System.Text.RegularExpressions.Regex.Split(
            input, @"(?<=[a-z])(?=[A-Z])");
        return words.Select(w => w.ToLowerInvariant()).Where(w => w.Length > 0);
    }

    /// <summary>
    /// Normalized Levenshtein similarity between the query and the plan's best sample query.
    /// This replaces the old token-overlap approach, which failed on semantically equivalent
    /// but lexically different queries (e.g. "percentage of invoices paid late" vs
    /// "Calculate what percentage of total sales invoices were paid after their due date").
    /// Edit distance captures surface similarity even with different word choices.
    /// </summary>
    private static float ComputeDescriptionRelevance(CandidateResult candidate, QueryIntent intent)
    {
        var plan = candidate.Plan;

        if (plan.SampleQueries.Count == 0)
        {
            // Fall back to corpus overlap when no sample queries are available.
            var corpus = ((plan.Understanding ?? "") + " " + (plan.Description ?? "")).ToLowerInvariant().Trim();
            if (string.IsNullOrWhiteSpace(corpus)) return 0.5f;
            var queryTokens = intent.NormalizedQuery
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 3)
                .ToList();
            if (queryTokens.Count == 0) return 0.5f;
            var matchCount = queryTokens.Count(t => corpus.Contains(t));
            return (float)matchCount / queryTokens.Count;
        }

        // Best sample-query similarity (normalized edit distance).
        var normalized = intent.NormalizedQuery;
        var editSim = plan.SampleQueries
            .Max(sq => NormalizedEditSimilarity(normalized, sq.Trim().ToLowerInvariant()));

        // Keyword coherence guard: if the query's content words are mostly absent from the
        // plan's understanding + description + sample text, this plan is about a different
        // subject even if the surface phrasing looks similar (e.g. both mention "customer").
        // Cap the edit-similarity contribution proportionally to keyword overlap.
        var planText = ((plan.Understanding ?? "") + " " + (plan.Description ?? "") + " " +
                        string.Join(" ", plan.SampleQueries)).ToLowerInvariant();
        var contentWords = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 4 && !IsStopWord(t))
            .ToList();
        if (contentWords.Count > 0)
        {
            float keywordCoverage = (float)contentWords.Count(w => planText.Contains(w)) / contentWords.Count;
            // Scale: full coverage → no change; zero coverage → reduce to 25 % of edit score.
            editSim *= 0.25f + 0.75f * keywordCoverage;
        }

        return editSim;
    }

    private static readonly HashSet<string> StopWords = new(
        new[]
        {
            "which", "where", "about", "would", "could", "should", "their", "there",
            "these", "those", "shall", "might", "shows", "lists", "gives", "tell",
            "show", "list", "give", "what", "that", "this", "from", "have",
            "been", "were", "with", "more", "most", "some", "than", "then", "when",
        },
        StringComparer.OrdinalIgnoreCase);

    private static bool IsStopWord(string word) => StopWords.Contains(word);

    /// <summary>
    /// Normalized edit similarity: 1 - (Levenshtein / max(|a|, |b|)).
    /// Returns 1.0 for identical strings, 0.0 for completely different.
    /// </summary>
    private static float NormalizedEditSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1f;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0f;
        int maxLen = Math.Max(a.Length, b.Length);
        int dist   = LevenshteinDistance(a, b);
        return 1f - ((float)dist / maxLen);
    }

    private static int LevenshteinDistance(string a, string b)
    {
        int m = a.Length;
        int n = b.Length;

        // Use two-row rolling array to keep memory O(n).
        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int j = 0; j <= n; j++) prev[j] = j;

        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return prev[n];
    }

    private static string BuildExplanation(CandidateResult c)
    {
        var parts = new List<string>();
        if (c.MatchedFeatures.Count > 0)
            parts.Add($"matched [{string.Join(", ", c.MatchedFeatures)}]");
        if (c.MismatchedFeatures.Count > 0)
            parts.Add($"missed [{string.Join(", ", c.MismatchedFeatures)}]");
        return string.Join("; ", parts);
    }
}
