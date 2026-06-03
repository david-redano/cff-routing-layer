// Retrieval/PlanEmbeddingIndex.cs
namespace CffRoutingLayerDemo.Retrieval;

using System.Text;
using CffRoutingLayerDemo.Plans;

/// <summary>
/// Builds and stores pre-computed embedding vectors for every loaded plan.
/// Built once at application startup; queried at sub-millisecond speed thereafter
/// via brute-force cosine similarity (114 plans × 1024 dims ≈ 460 KB — no ANN needed).
/// </summary>
public sealed class PlanEmbeddingIndex
{
    private readonly Dictionary<string, float[]> _embeddings = new();

    public IReadOnlyDictionary<string, float[]> Embeddings => _embeddings;

    /// <summary>
    /// Pre-compute and store embeddings for all plans.
    /// Should be called once at startup; takes ~10-15 seconds with Bedrock Titan.
    /// </summary>
    public async Task BuildAsync(
        IEnumerable<PlanDefinition> plans,
        IEmbeddingService embedder,
        CancellationToken ct = default)
    {
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            var text      = BuildEmbeddingText(plan);
            var embedding = await embedder.EmbedAsync(text, ct);
            _embeddings[plan.PlanId] = embedding;
        }
    }

    /// <summary>
    /// Return top-K plans by cosine similarity to <paramref name="queryEmbedding"/>.
    /// </summary>
    public List<(string PlanId, float Score)> Search(float[] queryEmbedding, int topK)
    {
        return _embeddings
            .Select(kvp => (kvp.Key, Score: CosineSimilarity(queryEmbedding, kvp.Value)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildEmbeddingText(PlanDefinition plan)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(plan.Understanding))
            sb.AppendLine(plan.Understanding);
        foreach (var sq in plan.SampleQueries)
            sb.AppendLine(sq);
        if (!string.IsNullOrWhiteSpace(plan.Domain))
            sb.AppendLine($"Domain: {plan.Domain}");
        if (!string.IsNullOrWhiteSpace(plan.Intent))
            sb.AppendLine($"Action: {plan.Intent}");
        if (plan.ExpectedData.Summary.Count > 0)
            sb.AppendLine($"Outputs: {string.Join(", ", plan.ExpectedData.Summary)}");
        return sb.ToString();
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0f;

        float dot  = 0f;
        float normA = 0f;
        float normB = 0f;

        for (int i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-8f ? 0f : dot / denom;
    }
}
