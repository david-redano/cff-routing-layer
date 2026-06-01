// Registry/AgentManifest.cs
namespace CffRoutingLayerDemo.Registry;

using CffRoutingLayerDemo.Core;

/// <summary>
/// Metadata + plan-building capability for a single registered agent.
/// An agent produces an <see cref="ExecutionPlan"/> given an intent and context.
/// </summary>
public sealed class AgentManifest
{
    public string AgentId        { get; init; } = "";
    public string DisplayName    { get; init; } = "";
    public string Description    { get; init; } = "";
    public string Intent         { get; init; } = "";
    public string[] Capabilities { get; init; } = [];

    // ── Reasoning fields (from <Reasoning> block) ─────────────────────────

    /// <summary>Natural-language description of what the plan does.
    /// Used as a semantic signal when keyword and capability matching both fail.</summary>
    public string   Understanding        { get; init; } = "";

    /// <summary>Top-level summary field names from ExpectedData (e.g. TotalExpenses, Currency).
    /// Matching against these boosts the Understanding score for queries that name expected outputs.</summary>
    public string[] ExpectedSummaryFields { get; init; } = [];

    /// <summary>
    /// Build a domain-specific execution plan for the given intent + context.
    /// Override by providing a custom <see cref="PlanFactory"/>; otherwise
    /// a default 3-step plan (auth → execute → report) is produced.
    /// </summary>
    public Func<IntentResult, RoutingContext, ExecutionPlan>? PlanFactory { get; init; }

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context)
        => PlanFactory is not null
            ? PlanFactory(intent, context)
            : DefaultPlan(intent, context);

    private ExecutionPlan DefaultPlan(IntentResult intent, RoutingContext context)
        => new(
            PlanId: $"{AgentId}-{context.RequestId[..8]}",
            Intent: intent.Intent,
            Steps:
            [
                new PlanStep(1, "authenticate",
                    new Dictionary<string, string> { ["companyId"] = context.CompanyId },
                    []),
                new PlanStep(2, "execute",
                    new Dictionary<string, string>(intent.Entities) { ["intent"] = intent.Intent },
                    [1]),
                new PlanStep(3, "generate-report",
                    new Dictionary<string, string> { ["intent"] = intent.Intent },
                    [2])
            ]);
}
