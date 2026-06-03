// Index/IPlanIndex.cs
namespace CffRoutingLayerDemo.Index;

using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

public sealed record IndexStats
{
    public required int PlanCount { get; init; }
    public required int ClusterCount { get; init; }
}

public interface IPlanIndex
{
    /// <summary>
    /// Retrieves candidate plans compatible with the query intent, ordered by score.
    /// Implementations may be synchronous (structural index) or asynchronous (hybrid embedding).
    /// </summary>
    Task<IReadOnlyList<CandidateResult>> RetrieveAsync(
        QueryIntent intent,
        int maxCandidates = 10,
        CancellationToken ct = default);

    IndexStats GetStats();
}
