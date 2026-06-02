// Matching/CandidateResult.cs
namespace CffRoutingLayerDemo.Matching;

using CffRoutingLayerDemo.Plans;

public sealed record CandidateResult
{
    public required PlanDefinition Plan { get; init; }
    public required float AlignmentScore { get; init; }
    public required IReadOnlyList<string> MatchedFeatures { get; init; }
    public required IReadOnlyList<string> MismatchedFeatures { get; init; }
    public required IReadOnlyList<string> UnknownFeatures { get; init; }
}
