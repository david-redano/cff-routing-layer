// Index/Filters/ActionFilter.cs
namespace CffRoutingLayerDemo.Index.Filters;

using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Eliminates plans whose primary action doesn't match the query's action.
/// Skips filtering when action confidence is low — mirrors the same guard in DomainFilter.
/// </summary>
public sealed class ActionFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        // If no action verb was clearly detected, the classified action is a default guess.
        // Filtering on a guess would eliminate correct plans (e.g. "What's our net income?"
        // defaults to List but the right plan is a Compute plan).
        if (intent.ActionConfidence <= 0.35f)
            return candidates;

        return candidates.Where(p =>
            p.Features is null ||
            p.Features.PrimaryAction == intent.PrimaryAction ||
            p.Features.SecondaryActions.Contains(intent.PrimaryAction) ||
            intent.ImpliedActions.Contains(p.Features.PrimaryAction));
    }
}
