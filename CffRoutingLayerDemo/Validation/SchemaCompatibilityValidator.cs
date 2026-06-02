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
            if (step.Parameters.Count == 0)
            {
                // Track outputs even for parameter-free steps
                foreach (var action in new[] { step.Action, step.ToolName }.Where(a => !string.IsNullOrEmpty(a)))
                    available.Add($"output_of_{step.Id}");
                continue;
            }

            // Check which required params are unmet
            var unmet = step.Parameters.Keys
                .Where(k => !available.Contains(k))
                .ToList();

            // Check if predecessor steps produce them
            var predOutputs = plan.Steps
                .Where(s => step.DependsOn.Contains(s.Id))
                .SelectMany(s => s.Parameters.Keys)     // outputs = params of dependent steps
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var stillUnmet = unmet.Where(u => !predOutputs.Contains(u)).ToList();

            if (stillUnmet.Count > 0)
            {
                // Downgrade to warning rather than error for YAML plans — parameter names
                // may be output field names from dynamic execution steps
                warnings.Add($"Step {step.Id} ({(string.IsNullOrEmpty(step.ToolName) ? step.Action : step.ToolName)}) " +
                             $"references parameters not confirmed in preceding steps: [{string.Join(", ", stillUnmet)}]");
            }

            // Mark step outputs as available
            foreach (var key in step.Parameters.Keys) available.Add(key);
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
