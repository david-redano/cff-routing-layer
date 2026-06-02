// Validation/IPlanValidator.cs
namespace CffRoutingLayerDemo.Validation;

using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

public interface IPlanValidator
{
    /// <summary>
    /// Validates that a selected plan can execute given the available context.
    /// Returns Pass, Warn, or Fail.
    /// </summary>
    ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings);
}
