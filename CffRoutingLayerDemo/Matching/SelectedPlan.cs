// Matching/SelectedPlan.cs
namespace CffRoutingLayerDemo.Matching;

using CffRoutingLayerDemo.Plans;

public sealed record SelectedPlan
{
    public required PlanDefinition Plan { get; init; }
    public required float Confidence { get; init; }
    public required IReadOnlyList<SlotBinding> SlotBindings { get; init; }
    public required ValidationResult ValidationResult { get; init; }
    public required string Explanation { get; init; }
}
