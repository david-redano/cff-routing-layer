// Retrieval/HybridPlanRetriever.cs
namespace CffRoutingLayerDemo.Retrieval;

using CffRoutingLayerDemo.Index;
using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Phase 1 — Hybrid retrieval combining:
///   Signal A: Embedding similarity   (semantic; weight 0.55)
///   Signal B: Metadata match score   (structural; weight 0.45)
///
/// Replaces the serial DomainFilter→ActionFilter→TemporalFilter→EntityFilter chain.
/// Every plan receives a combined score; nothing is hard-filtered out.
/// Returns top-10 candidates (more breathing room for the ranker).
/// </summary>
public sealed class HybridPlanRetriever : IPlanIndex
{
    private const float EmbeddingWeight = 0.55f;
    private const float MetadataWeight  = 0.45f;

    private readonly IReadOnlyList<PlanDefinition> _allPlans;
    private readonly IEmbeddingService _embedder;
    private readonly PlanEmbeddingIndex _embeddingIndex;
    private readonly IndexStats _stats;

    public HybridPlanRetriever(
        IReadOnlyList<PlanDefinition> plans,
        IEmbeddingService embedder,
        PlanEmbeddingIndex embeddingIndex)
    {
        _allPlans       = plans;
        _embedder       = embedder;
        _embeddingIndex = embeddingIndex;
        _stats          = new IndexStats { PlanCount = plans.Count, ClusterCount = 0 };
    }

    public async Task<IReadOnlyList<CandidateResult>> RetrieveAsync(
        QueryIntent intent,
        int maxCandidates = 10,
        CancellationToken ct = default)
    {
        // ── Signal A: semantic embedding similarity ──────────────────────────
        var queryEmbedding = await _embedder.EmbedAsync(intent.RawQuery, ct);
        var embeddingScores = _embeddingIndex.Search(queryEmbedding, _allPlans.Count)
            .ToDictionary(x => x.PlanId, x => x.Score);

        // ── Signal B: metadata match ─────────────────────────────────────────
        var results = _allPlans.Select(plan =>
        {
            var embScore  = embeddingScores.TryGetValue(plan.PlanId, out var es) ? es : 0f;
            var metaScore = ComputeMetadataScore(intent, plan);
            var combined  = embScore * EmbeddingWeight + metaScore * MetadataWeight;

            return new CandidateResult
            {
                Plan              = plan,
                AlignmentScore    = combined,
                MatchedFeatures   = BuildMatchedFeatures(intent, plan, embScore, metaScore),
                MismatchedFeatures = [],
                UnknownFeatures   = []
            };
        })
        .OrderByDescending(c => c.AlignmentScore)
        .Take(maxCandidates)
        .ToList();

        return results;
    }

    public IndexStats GetStats() => _stats;

    // ── Metadata scoring ──────────────────────────────────────────────────────

    private static float ComputeMetadataScore(QueryIntent intent, PlanDefinition plan)
    {
        var features = plan.Features;
        if (features is null) return 0.2f; // No feature vector — neutral score

        float score = 0f;

        // Domain match (35% of metadata score)
        if (string.Equals(features.Domain, intent.Domain, StringComparison.OrdinalIgnoreCase))
            score += 0.35f;
        else if (string.Equals(features.Domain, "general", StringComparison.OrdinalIgnoreCase))
            score += 0.17f; // Domain-agnostic plans get partial credit
        else if (string.Equals(intent.Domain, "unknown", StringComparison.OrdinalIgnoreCase) ||
                 intent.DomainConfidence < 0.4f)
            score += 0.15f; // Uncertain domain — don't penalise

        // SubDomain match (25% of metadata score)
        if (string.Equals(features.SubDomain, intent.SubDomain, StringComparison.OrdinalIgnoreCase))
            score += 0.25f;
        else if (string.Equals(intent.SubDomain, "unknown", StringComparison.OrdinalIgnoreCase))
            score += 0.10f;

        // Action match (25% of metadata score)
        if (features.PrimaryAction == intent.PrimaryAction)
            score += 0.25f;
        else if (intent.ImpliedActions.Contains(features.PrimaryAction))
            score += 0.12f;
        else if (intent.ActionConfidence <= 0.35f)
            score += 0.10f; // Action not detected — neutral

        // Temporal compatibility (15% of metadata score)
        if (IsTemporallyCompatible(features, intent.TemporalScope))
            score += 0.15f;

        return score; // max 1.0
    }

    private static bool IsTemporallyCompatible(
        PlanFeatureVector features,
        TemporalScope? intentTemporal)
    {
        if (intentTemporal is null || intentTemporal.Type == TemporalScopeType.None)
            return true; // No temporal requirement — compatible with everything

        return features.SupportsDateRange || features.SupportsPointInTime;
    }

    private static IReadOnlyList<string> BuildMatchedFeatures(
        QueryIntent intent,
        PlanDefinition plan,
        float embScore,
        float metaScore)
    {
        var parts = new List<string>
        {
            $"embedding:{embScore:F2}",
            $"metadata:{metaScore:F2}"
        };
        if (plan.Features is not null &&
            string.Equals(plan.Features.Domain, intent.Domain, StringComparison.OrdinalIgnoreCase))
            parts.Add($"domain:{plan.Features.Domain}");
        return parts;
    }
}
