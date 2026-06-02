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

        // Bonus for output field relevance
        var outputOverlap = ComputeOutputRelevance(candidate, intent);
        baseScore = baseScore * 0.85f + outputOverlap * 0.15f;

        return Math.Clamp(baseScore, 0f, 1f);
    }

    private static float ComputeOutputRelevance(CandidateResult candidate, QueryIntent intent)
    {
        if (candidate.Plan.Features is null) return 0.5f;
        // Check if query keywords appear in expected output fields
        var outputText = string.Join(" ", candidate.Plan.Features.OutputFieldNames).ToLowerInvariant();
        var queryTokens = intent.NormalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = queryTokens.Count(t => t.Length > 3 && outputText.Contains(t));
        return Math.Min(1f, matches * 0.2f);
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
