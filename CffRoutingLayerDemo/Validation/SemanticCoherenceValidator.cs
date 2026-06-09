// Validation/SemanticCoherenceValidator.cs
namespace CffRoutingLayerDemo.Validation;

using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Rejects plans whose semantic subject is entirely absent from the user's query.
///
/// Problem it solves: <see cref="SlotCoverageValidator"/> only checks that required slot
/// keys are present. A plan with only <c>companyId</c> defaulted always passes regardless
/// of whether the user actually asked about that plan's subject (e.g. a "balance" plan
/// passing for a "payments" query because both default to companyId = DEMO-001).
///
/// Approach: extract content words (len > 4, non-stop-word) from the plan's
/// <c>Understanding</c> + <c>Description</c> text. If NONE of those words appear in the
/// query, emit a Warn. If fewer than a minimum fraction appear, also emit a Warn.
/// This is intentionally a Warn (not Fail) so the plan is still tried when there is no
/// better-covered alternative — the ranking score is the primary selector; this validator
/// adds a warning signal that appears in the diagnostic trace.
/// </summary>
public sealed class SemanticCoherenceValidator : IPlanValidator
{
    /// <summary>
    /// Fraction of plan content words that must appear in the query to pass silently.
    /// Below this threshold a Warn is added.
    /// </summary>
    private const float MinCoverageForSilentPass = 0.20f;

    private static readonly HashSet<string> StopWords = new(
        [
            "which", "where", "about", "would", "could", "should", "their", "there",
            "these", "those", "shall", "might", "shows", "lists", "gives", "based",
            "given", "using", "using", "across", "within", "between", "through",
            "against", "total", "value", "values", "amount", "number", "count",
            "level", "point", "items", "company", "account", "report", "query",
        ],
        StringComparer.OrdinalIgnoreCase);

    public ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings)
    {
        var planCorpus = ((plan.Understanding ?? "") + " " + (plan.Description ?? ""))
            .ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(planCorpus))
            return new ValidationResult { Status = ValidationStatus.Pass, Errors = [], Warnings = [], MissingSlots = [], DefaultedSlots = [] };

        // Extract meaningful content words from the plan.
        var planWords = planCorpus
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 4 && !StopWords.Contains(w))
            .Select(w => System.Text.RegularExpressions.Regex.Replace(w, @"[^a-z]", ""))
            .Where(w => w.Length > 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (planWords.Count == 0)
            return new ValidationResult { Status = ValidationStatus.Pass, Errors = [], Warnings = [], MissingSlots = [], DefaultedSlots = [] };

        var queryLower = intent.NormalizedQuery.ToLowerInvariant();
        var matchCount = planWords.Count(w => queryLower.Contains(w));
        float coverage = (float)matchCount / planWords.Count;

        if (coverage < MinCoverageForSilentPass)
        {
            var warning = coverage == 0
                ? $"Plan '{plan.PlanId}' subject words not found in query (semantic mismatch likely)"
                : $"Plan '{plan.PlanId}' low semantic coverage ({coverage:P0} of subject words found in query)";

            return new ValidationResult
            {
                Status         = ValidationStatus.Warn,
                Errors         = [],
                Warnings       = [warning],
                MissingSlots   = [],
                DefaultedSlots = []
            };
        }

        return new ValidationResult { Status = ValidationStatus.Pass, Errors = [], Warnings = [], MissingSlots = [], DefaultedSlots = [] };
    }
}
