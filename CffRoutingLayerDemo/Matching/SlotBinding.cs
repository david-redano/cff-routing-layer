// Matching/SlotBinding.cs
namespace CffRoutingLayerDemo.Matching;

public enum BindingSource
{
    QueryExtraction,        // Directly from user query
    TemporalResolution,     // Resolved from relative date expression
    ConstraintExtraction,   // Derived from a filter/condition
    SessionContext,         // Carried over from conversation history
    PlanDefault             // Default value defined in the plan
}

public sealed record SlotBinding
{
    public required string SlotName { get; init; }
    public required string Value { get; init; }
    public required BindingSource Source { get; init; }
    public required float Confidence { get; init; }
}
