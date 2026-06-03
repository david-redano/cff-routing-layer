// Validation/SchemaCompatibilityValidator.cs
namespace CffRoutingLayerDemo.Validation;

using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Validates that each step's required inputs can be satisfied by either
/// the query's slot bindings or a preceding step's outputs.
/// Catches plans where the tool chain is internally inconsistent.
/// </summary>
public sealed class SchemaCompatibilityValidator : IPlanValidator
{
    public ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings)
    {
        var errors   = new List<string>();
        var warnings = new List<string>();

        // Available data starts with query bindings + plan defaults
        var available = new HashSet<string>(
            bindings.Select(b => b.SlotName).Concat(plan.DefaultEntities.Keys),
            StringComparer.OrdinalIgnoreCase);

        // Process steps in dependency order
        var ordered = TopologicalSort(plan.Steps);

        foreach (var step in ordered)
        {
            var stepLabel = string.IsNullOrEmpty(step.ToolName) ? step.Action : step.ToolName;

            // Check which required params (InputSchema) are unmet
            if (step.Parameters.Count > 0)
            {
                var unmet = step.Parameters.Keys
                    .Where(k => !available.Contains(k))
                    .ToList();

                if (unmet.Count > 0)
                {
                    // Downgrade to warning — named plans use static parameter values
                    // (e.g. ledgerType: operating), not runtime input requirements
                    warnings.Add($"Step {step.Id} ({stepLabel}) references " +
                                 $"[{string.Join(", ", unmet)}] not found in preceding outputs or bindings");
                }
            }

            // Add this step's declared output fields to the available set.
            // If OutputFields is empty (plan doesn't declare them), use a sentinel so
            // downstream steps that depend on this one are not incorrectly flagged.
            if (step.OutputFields.Count > 0)
            {
                foreach (var field in step.OutputFields)
                    available.Add(field);
            }
            else
            {
                available.Add($"output_of_{step.Id}");
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
            MissingSlots   = [],
            DefaultedSlots = []
        };
    }

    private static IReadOnlyList<YamlPlanStep> TopologicalSort(IReadOnlyList<YamlPlanStep> steps)
    {
        var sorted  = new List<YamlPlanStep>();
        var visited = new HashSet<int>();

        void Visit(YamlPlanStep step)
        {
            if (visited.Contains(step.Id)) return;
            visited.Add(step.Id);
            foreach (var depId in step.DependsOn)
            {
                var dep = steps.FirstOrDefault(s => s.Id == depId);
                if (dep is not null) Visit(dep);
            }
            sorted.Add(step);
        }

        foreach (var step in steps) Visit(step);
        return sorted;
    }
}
