// Validation/SlotCoverageValidator.cs
namespace CffRoutingLayerDemo.Validation;

using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Checks that every required input slot in the plan has a bound value or a plan default.
/// </summary>
public sealed class SlotCoverageValidator : IPlanValidator
{
    public ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings)
    {
        var boundSlotNames = bindings.Select(b => b.SlotName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultedKeys  = plan.DefaultEntities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var errors    = new List<string>();
        var warnings  = new List<string>();
        var missing   = new List<string>();
        var defaulted = new List<string>();

        foreach (var slot in plan.RequiredInputSlots)
        {
            if (boundSlotNames.Contains(slot)) continue;

            if (defaultedKeys.Contains(slot))
            {
                defaulted.Add(slot);
                warnings.Add($"Slot '{slot}' not in query — using plan default '{plan.DefaultEntities[slot]}'");
            }
            else
            {
                missing.Add(slot);
                errors.Add($"Required slot '{slot}' has no value and no default");
            }
        }

        var status = errors.Count > 0 ? ValidationStatus.Fail
                   : warnings.Count > 0 ? ValidationStatus.Warn
                   : ValidationStatus.Pass;

        return new ValidationResult
        {
            Status         = status,
            Errors         = errors,
            Warnings       = warnings,
            MissingSlots   = missing,
            DefaultedSlots = defaulted
        };
    }
}
