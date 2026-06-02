// Validation/CompositeValidator.cs
namespace CffRoutingLayerDemo.Validation;

using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Runs all validators and aggregates results.
/// </summary>
public sealed class CompositeValidator : IPlanValidator
{
    private readonly IReadOnlyList<IPlanValidator> _validators;

    public CompositeValidator()
        : this(new SlotCoverageValidator(), new SchemaCompatibilityValidator()) { }

    public CompositeValidator(params IPlanValidator[] validators)
    {
        _validators = validators;
    }

    public ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings)
    {
        var allErrors    = new List<string>();
        var allWarnings  = new List<string>();
        var allMissing   = new List<string>();
        var allDefaulted = new List<string>();

        foreach (var validator in _validators)
        {
            var result = validator.Validate(plan, intent, bindings);
            allErrors.AddRange(result.Errors);
            allWarnings.AddRange(result.Warnings);
            allMissing.AddRange(result.MissingSlots);
            allDefaulted.AddRange(result.DefaultedSlots);
        }

        var status = allErrors.Count > 0 ? ValidationStatus.Fail
                   : allWarnings.Count > 0 ? ValidationStatus.Warn
                   : ValidationStatus.Pass;

        return new ValidationResult
        {
            Status         = status,
            Errors         = allErrors,
            Warnings       = allWarnings,
            MissingSlots   = allMissing,
            DefaultedSlots = allDefaulted
        };
    }
}
