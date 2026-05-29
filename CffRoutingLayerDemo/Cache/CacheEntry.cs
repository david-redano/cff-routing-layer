// Cache/CacheEntry.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;

public record CacheEntry(
    string NormalizedText,
    double[] Vector,
    IntentResult Intent,
    ExecutionPlan Plan,
    DateTime StoredAt
);

public record CacheHit(
    IntentResult Intent,
    ExecutionPlan Plan,
    double Score
);
