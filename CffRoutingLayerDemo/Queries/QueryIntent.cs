// Queries/QueryIntent.cs
namespace CffRoutingLayerDemo.Queries;

/// <summary>
/// Structured representation of what the user is asking for.
/// Output of Layer 1 (Query Understanding). Never compared by text similarity —
/// only by structural alignment against plan feature vectors.
/// </summary>
public sealed record QueryIntent
{
    public required string RawQuery { get; init; }
    public required string NormalizedQuery { get; init; }

    // ─── What action? ───
    public required DomainAction PrimaryAction { get; init; }
    public required IReadOnlySet<DomainAction> ImpliedActions { get; init; }

    // ─── What domain? ───
    public required string Domain { get; init; }
    public required string SubDomain { get; init; }
    public required float DomainConfidence { get; init; }

    // ─── What entities are provided? ───
    public required IReadOnlyList<QuerySlot> ExtractedSlots { get; init; }

    // ─── What time frame? ───
    public required TemporalScope? TemporalScope { get; init; }

    // ─── What constraints/filters? ───
    public required IReadOnlyList<Constraint> Constraints { get; init; }

    // ─── What output is expected? ───
    public required OutputFormat ExpectedOutput { get; init; }

    // ─── Multi-intent? ───
    public required IReadOnlyList<QueryIntent> SubIntents { get; init; }

    // ─── Confidence and diagnostics ───
    public required float OverallConfidence { get; init; }

    /// <summary>
    /// Confidence that <see cref="PrimaryAction"/> was explicitly detected (vs defaulted).
    /// ≤ 0.35 means no action verb matched — action should be treated as unknown during scoring.
    /// </summary>
    public required float ActionConfidence { get; init; }

    /// <summary>
    /// One-sentence explanation of the classification decision (from LLM).
    /// Populated by LlmQueryParser; empty string when rule-based fallback is used.
    /// Enables audit trails and debugging.
    /// </summary>
    public required string Reasoning { get; init; }

    public required IReadOnlyList<string> ParsingNotes { get; init; }
}
