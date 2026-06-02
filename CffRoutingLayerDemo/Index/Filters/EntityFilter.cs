// Index/Filters/EntityFilter.cs
namespace CffRoutingLayerDemo.Index.Filters;

using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Eliminates plans that require entity types the query doesn't provide
/// AND for which the plan has no defaults.
/// </summary>
public sealed class EntityFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        var providedTypes = intent.ExtractedSlots
            .Where(s => s.Confidence >= 0.5f)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return candidates.Where(p =>
        {
            if (p.Features is null) return true;

            var unmet = p.Features.RequiredEntityTypes
                .Except(providedTypes, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unmet.Count == 0) return true;

            // Pass if plan has defaults for all unmet slots
            var defaultedKeys = p.DefaultEntities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return unmet.All(u => defaultedKeys.Contains(u));
        });
    }
}
