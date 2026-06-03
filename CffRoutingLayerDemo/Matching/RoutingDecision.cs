// Matching/RoutingDecision.cs
namespace CffRoutingLayerDemo.Matching;

public enum RoutingStatus
{
    Success,
    ConfirmAndExecute,  // Score 0.60–0.80 — ask user "Did you mean X?" before executing
    Ambiguous,          // Top-2 gap < 0.08 — ask user to choose between candidates
    Clarify,            // Score 0.35–0.60 — ask user to rephrase
    Rejected,           // Score < 0.35 or query not understood
    NoPlanFound,        // Understood but no matching plan exists
    LowConfidence,      // Candidates found but none scored high enough (legacy)
    ValidationFailed    // Best plan can't execute with available inputs
}

public sealed record RoutingTraceLayerResult
{
    public required string LayerName { get; init; }
    public required double ElapsedMs { get; init; }
    public required object? Result { get; init; }
}

public sealed record RoutingTraceResult
{
    public required string RawQuery { get; init; }
    public required double TotalElapsedMs { get; init; }
    public required IReadOnlyList<RoutingTraceLayerResult> Layers { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

public sealed record RoutingDecision
{
    public required RoutingStatus Status { get; init; }
    public required string Message { get; init; }
    public SelectedPlan? SelectedPlan { get; init; }
    public IReadOnlyList<SelectedPlan>? MultiPlans { get; init; }
    public IReadOnlyList<string>? ConsideredPlanIds { get; init; }
    public required RoutingTraceResult Trace { get; init; }

    public static RoutingDecision Success(SelectedPlan plan, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.Success,
        Message = $"Matched plan {plan.Plan.PlanId} with {plan.Confidence:P0} confidence",
        SelectedPlan = plan,
        Trace = trace
    };

    public static RoutingDecision MultiSuccess(IReadOnlyList<SelectedPlan> plans, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.Success,
        Message = $"Matched {plans.Count} plans for multi-intent query",
        MultiPlans = plans,
        Trace = trace
    };

    public static RoutingDecision Rejected(string reason, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.Rejected,
        Message = reason,
        Trace = trace
    };

    public static RoutingDecision NoPlanFound(string reason, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.NoPlanFound,
        Message = reason,
        Trace = trace
    };

    public static RoutingDecision LowConfidence(string reason, IReadOnlyList<string> considered, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.LowConfidence,
        Message = reason,
        ConsideredPlanIds = considered,
        Trace = trace
    };

    public static RoutingDecision ValidationFailed(string reason, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.ValidationFailed,
        Message = reason,
        Trace = trace
    };

    public static RoutingDecision ConfirmAndExecute(SelectedPlan plan, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.ConfirmAndExecute,
        Message = $"Please confirm: did you mean \"{plan.Plan.DisplayName}\"?",
        SelectedPlan = plan,
        Trace = trace
    };

    public static RoutingDecision Ambiguous(IReadOnlyList<SelectedPlan> candidates, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.Ambiguous,
        Message = $"Multiple plans match with similar confidence — please choose: {string.Join(", ", candidates.Select(c => c.Plan.DisplayName))}",
        MultiPlans = candidates,
        Trace = trace
    };

    public static RoutingDecision Clarify(string reason, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.Clarify,
        Message = reason,
        Trace = trace
    };
}
