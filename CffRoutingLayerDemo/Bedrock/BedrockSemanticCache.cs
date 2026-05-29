// Bedrock/BedrockSemanticCache.cs
namespace CffRoutingLayerDemo.Bedrock;

using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Config;
using CffRoutingLayerDemo.Core;

/// <summary>
/// Semantic cache backed by Amazon Titan Embeddings V2.
/// Lookup and store both call Bedrock for real embeddings (1536 dims),
/// then apply cosine similarity with a configurable threshold.
/// </summary>
public sealed class BedrockSemanticCache : ISemanticCache, IDisposable
{
    private readonly BedrockEmbeddingProvider _embedder;
    private readonly double _threshold;
    private readonly List<CacheEntry> _entries = [];

    // Titan returns float[]; CacheEntry uses double[] for cosine maths
    private static double[] ToDouble(float[] v) => Array.ConvertAll(v, x => (double)x);

    public BedrockSemanticCache(AppConfig config)
    {
        _embedder  = new BedrockEmbeddingProvider(config);
        _threshold = config.CacheSimilarityThreshold;
    }

    public IReadOnlyList<CacheEntry> Entries => _entries.AsReadOnly();

    public CacheHit? Lookup(string normalizedText)
        => LookupAsync(normalizedText).GetAwaiter().GetResult();

    public async Task<CacheHit?> LookupAsync(
        string normalizedText,
        CancellationToken ct = default)
    {
        if (_entries.Count == 0) return null;

        var queryVec = ToDouble(await _embedder.EmbedAsync(normalizedText, ct));

        CacheEntry? best      = null;
        double      bestScore = 0;

        foreach (var entry in _entries)
        {
            var score = CosineSimilarity(queryVec, entry.Vector);
            if (score >= _threshold && score > bestScore)
            {
                bestScore = score;
                best      = entry;
            }
        }

        return best is null ? null : new CacheHit(best.Intent, best.Plan, bestScore);
    }

    public void Store(string normalizedText, IntentResult intent, ExecutionPlan plan)
        => StoreAsync(normalizedText, intent, plan).GetAwaiter().GetResult();

    public async Task StoreAsync(
        string normalizedText,
        IntentResult intent,
        ExecutionPlan plan,
        CancellationToken ct = default)
    {
        var vec = ToDouble(await _embedder.EmbedAsync(normalizedText, ct));
        _entries.Add(new CacheEntry(normalizedText, vec, intent, plan, DateTime.UtcNow));
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot   += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    public void Dispose() => _embedder.Dispose();
}
