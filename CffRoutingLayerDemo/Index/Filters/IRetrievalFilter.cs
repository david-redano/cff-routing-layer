// Index/Filters/IRetrievalFilter.cs
namespace CffRoutingLayerDemo.Index.Filters;

using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

public interface IRetrievalFilter
{
    IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent);
}
