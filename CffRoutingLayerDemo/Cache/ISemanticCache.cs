// Cache/ISemanticCache.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;

public interface ISemanticCache
{
    CacheHit? Lookup(string userMessage);
    void Store(string userMessage, IntentResult intent, ExecutionPlan plan);
    IReadOnlyList<CacheEntry> Entries { get; }
}
