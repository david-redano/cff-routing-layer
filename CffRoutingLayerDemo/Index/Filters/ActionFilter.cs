// Index/Filters/ActionFilter.cs
namespace CffRoutingLayerDemo.Index.Filters;

using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Eliminates plans whose primary action doesn't match the query's action.
/// </summary>
public sealed class ActionFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        return candidates.Where(p =>
            p.Features is null ||
            p.Features.PrimaryAction == intent.PrimaryAction ||
            p.Features.SecondaryActions.Contains(intent.PrimaryAction) ||
            intent.ImpliedActions.Contains(p.Features.PrimaryAction));
    }
}
