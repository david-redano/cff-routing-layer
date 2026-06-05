// Cache/CacheEntry.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;

public sealed record CacheEntry(
    string NormalizedText,
    double[] Vector,
    IntentResult Intent,
    ExecutionPlan Plan,
    DateTime StoredAt
);
