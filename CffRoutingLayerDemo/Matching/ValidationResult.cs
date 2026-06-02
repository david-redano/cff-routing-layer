// Matching/ValidationResult.cs
namespace CffRoutingLayerDemo.Matching;

public enum ValidationStatus
{
    Pass,       // All clear — execute
    Warn,       // Can execute but some slots are defaulted or uncertain
    Fail        // Cannot execute — missing critical inputs
}

public sealed record ValidationResult
{
    public required ValidationStatus Status { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<string> MissingSlots { get; init; }
    public required IReadOnlyList<string> DefaultedSlots { get; init; }
}
