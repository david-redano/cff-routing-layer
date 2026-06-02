// Ranking/ScoringWeights.cs
namespace CffRoutingLayerDemo.Ranking;

/// <summary>
/// Weights for each feature dimension in plan scoring.
/// The only tuning knobs in the retrieval system — no magic numbers elsewhere.
/// All weights must sum to 1.0.
/// </summary>
public sealed record ScoringWeights
{
    public float Domain         { get; init; } = 0.25f;
    public float SubDomain      { get; init; } = 0.15f;
    public float Action         { get; init; } = 0.20f;
    public float Temporal       { get; init; } = 0.10f;
    public float EntityCoverage { get; init; } = 0.15f;
    public float OutputFormat   { get; init; } = 0.05f;
    public float Discriminator  { get; init; } = 0.10f;

    public static ScoringWeights Default => new();

    public bool IsValid()
    {
        var sum = Domain + SubDomain + Action + Temporal + EntityCoverage + OutputFormat + Discriminator;
        return Math.Abs(sum - 1.0f) < 0.01f;
    }
}
