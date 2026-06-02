// Plans/PlanFeatureVector.cs
namespace CffRoutingLayerDemo.Plans;

using CffRoutingLayerDemo.Queries;

/// <summary>
/// Structural feature vector for a plan. Used for deterministic retrieval.
/// Every dimension is discrete and enumerable — no embeddings, no similarity thresholds.
/// Computed once at plan ingestion time via <see cref="Index.PlanFeatureExtractor"/>.
/// </summary>
public sealed record PlanFeatureVector
{
    // ─── Domain classification ───
    public required string Domain { get; init; }
    public required string SubDomain { get; init; }

    // ─── Action type ───
    public required DomainAction PrimaryAction { get; init; }
    public required IReadOnlySet<DomainAction> SecondaryActions { get; init; }

    // ─── Tool requirements ───
    public required IReadOnlySet<string> ToolNames { get; init; }   // Action names used across steps
    public required int StepCount { get; init; }
    public required bool RequiresCrossReference { get; init; }

    // ─── Entity requirements ───
    public required IReadOnlySet<string> RequiredEntityTypes { get; init; }
    public required IReadOnlySet<string> OptionalEntityTypes { get; init; }

    // ─── Temporal characteristics ───
    public required TemporalScopeType TemporalScope { get; init; }
    public required bool SupportsDateRange { get; init; }
    public required bool SupportsPointInTime { get; init; }

    // ─── Output characteristics ───
    public required OutputFormat OutputFormat { get; init; }
    public required IReadOnlySet<string> OutputFieldNames { get; init; }

    // ─── Discriminators (what distinguishes this plan from others in the same cluster) ───
    public required IReadOnlyDictionary<string, string> Discriminators { get; init; }
}
