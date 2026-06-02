// Matching/RankingResult.cs
namespace CffRoutingLayerDemo.Matching;

using CffRoutingLayerDemo.Plans;

public sealed record RankedCandidate
{
    public required PlanDefinition Plan { get; init; }
    public required float Score { get; init; }
    public required string Explanation { get; init; }
}

public sealed record RankingResult
{
    public required IReadOnlyList<RankedCandidate> Candidates { get; init; }
    public required string Reasoning { get; init; }
    public required float TopConfidence { get; init; }
    public required bool IsAmbiguous { get; init; }
}
