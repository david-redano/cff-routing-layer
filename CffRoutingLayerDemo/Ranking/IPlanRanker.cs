// Ranking/IPlanRanker.cs
namespace CffRoutingLayerDemo.Ranking;

using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Queries;

public interface IPlanRanker
{
    /// <summary>
    /// Ranks candidate plans by how well they satisfy the query intent.
    /// Returns candidates in descending order of suitability.
    /// </summary>
    Task<RankingResult> RankAsync(QueryIntent intent, IReadOnlyList<CandidateResult> candidates);
}
