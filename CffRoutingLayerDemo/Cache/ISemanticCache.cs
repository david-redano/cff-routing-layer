// Cache/ISemanticCache.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;

public interface ISemanticCache
{
    CacheHit? Lookup(string normalizedText);
    void Store(string normalizedText, IntentResult intent, ExecutionPlan plan);
    IReadOnlyList<CacheEntry> Entries { get; }
}
