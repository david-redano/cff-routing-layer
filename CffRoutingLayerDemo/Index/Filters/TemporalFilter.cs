// Index/Filters/TemporalFilter.cs
namespace CffRoutingLayerDemo.Index.Filters;

using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Eliminates plans that cannot handle the query's temporal scope.
/// </summary>
public sealed class TemporalFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        if (intent.TemporalScope is null)
            return candidates;

        return candidates.Where(p =>
            p.Features is null ||
            intent.TemporalScope.Type switch
            {
                TemporalScopeType.BoundedPeriod  => p.Features.SupportsDateRange,
                TemporalScopeType.RelativeWindow  => p.Features.SupportsDateRange,
                TemporalScopeType.PointInTime     => p.Features.SupportsPointInTime || p.Features.SupportsDateRange,
                _                                 => true
            });
    }
}
