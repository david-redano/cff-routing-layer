// Index/Filters/DomainFilter.cs
namespace CffRoutingLayerDemo.Index.Filters;

using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Eliminates plans from the wrong domain.
/// Typically removes 70–85% of candidates in a single pass.
/// </summary>
public sealed class DomainFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        // Don't filter if domain confidence is low — we might be wrong
        if (intent.DomainConfidence < 0.4f)
            return candidates;

        return candidates.Where(p =>
            p.Features is null ||
            p.Features.Domain == intent.Domain ||
            p.Features.Domain == "general");
    }
}
