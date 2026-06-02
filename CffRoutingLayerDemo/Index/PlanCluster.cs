// Index/PlanCluster.cs
namespace CffRoutingLayerDemo.Index;

using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

public sealed record ClusterDiscriminator
{
    public required string FeatureName { get; init; }
    public required IReadOnlyList<string> PossibleValues { get; init; }
    public required string Description { get; init; }
}

public sealed record PlanCluster
{
    public required string ClusterId { get; init; }
    public required string Domain { get; init; }
    public required string SubDomain { get; init; }
    public required DomainAction PrimaryAction { get; init; }
    public required IReadOnlyList<PlanDefinition> Plans { get; init; }
    public required IReadOnlyList<ClusterDiscriminator> Discriminators { get; init; }
}
