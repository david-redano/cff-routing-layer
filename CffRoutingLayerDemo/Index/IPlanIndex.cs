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
    /// Retrieves candidate plans that are structurally compatible with the query intent.
    /// Returns plans ordered by feature alignment score (deterministic, no LLM).
    /// </summary>
    IReadOnlyList<CandidateResult> Retrieve(QueryIntent intent, int maxCandidates = 5);

    IndexStats GetStats();
}
