// Cache/InMemorySemanticCache.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;

public sealed class InMemorySemanticCache : ISemanticCache
{
    private readonly List<CacheEntry> _entries = [];
    private readonly EmbeddingSimulator _embedder;
    private const double SimilarityThreshold = 0.75;

    public IReadOnlyList<CacheEntry> Entries => _entries.AsReadOnly();

    public InMemorySemanticCache() : this(new EmbeddingSimulator()) { }

    public InMemorySemanticCache(EmbeddingSimulator embedder)
    {
        _embedder = embedder;
    }

    public CacheHit? Lookup(string userMessage)
    {
        if (_entries.Count == 0) return null;

        var queryVector = _embedder.Embed(userMessage);

        return _entries
            .Select(e => new { Entry = e, Score = CosineSimilarity(queryVector, e.Vector) })
            .Where(x => x.Score >= SimilarityThreshold)
            .OrderByDescending(x => x.Score)
            .Select(x => new CacheHit(x.Entry.Intent, x.Entry.Plan, x.Score))
            .FirstOrDefault();
    }

    public void Store(string userMessage, IntentResult intent, ExecutionPlan plan)
    {
        _entries.Add(new CacheEntry(
            NormalizedText: userMessage,
            Vector:         _embedder.Embed(userMessage),
            Intent:         intent,
            Plan:           plan,
            StoredAt:       DateTime.UtcNow
        ));
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot  = a.Zip(b, (x, y) => x * y).Sum();
        double magA = Math.Sqrt(a.Sum(x => x * x));
        double magB = Math.Sqrt(b.Sum(x => x * x));
        return magA == 0 || magB == 0 ? 0 : dot / (magA * magB);
    }
}
