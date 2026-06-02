// Index/ClusterBuilder.cs
namespace CffRoutingLayerDemo.Index;

using CffRoutingLayerDemo.Plans;

/// <summary>
/// Groups plans into clusters based on shared structural features.
/// A cluster = plans that serve the same general purpose but differ in specifics.
/// Identifies discriminators: features that distinguish plans within a cluster.
/// </summary>
public sealed class ClusterBuilder
{
    public IReadOnlyDictionary<string, PlanCluster> Build(IReadOnlyList<PlanDefinition> plans)
    {
        var groups = plans
            .Where(p => p.Features is not null)
            .GroupBy(p => $"{p.Features!.Domain}:{p.Features.SubDomain}:{p.Features.PrimaryAction}");

        var clusters = new Dictionary<string, PlanCluster>();

        foreach (var group in groups)
        {
            var plansInCluster = group.ToList();
            var discriminators = IdentifyDiscriminators(plansInCluster);

            clusters[group.Key] = new PlanCluster
            {
                ClusterId      = group.Key,
                Domain         = plansInCluster[0].Features!.Domain,
                SubDomain      = plansInCluster[0].Features!.SubDomain,
                PrimaryAction  = plansInCluster[0].Features!.PrimaryAction,
                Plans          = plansInCluster,
                Discriminators = discriminators
            };
        }

        return clusters;
    }

    private static IReadOnlyList<ClusterDiscriminator> IdentifyDiscriminators(List<PlanDefinition> plans)
    {
        if (plans.Count <= 1) return [];

        var discriminators = new List<ClusterDiscriminator>();

        // Tool chain variation
        var toolSets = plans.Select(p => string.Join("+", p.Features!.ToolNames.OrderBy(t => t))).ToList();
        if (toolSets.Distinct().Count() > 1)
        {
            discriminators.Add(new ClusterDiscriminator
            {
                FeatureName    = "tool_chain",
                PossibleValues = toolSets.Distinct().ToList(),
                Description    = "Different step/action sequences"
            });
        }

        // Step count variation (complexity)
        var stepCounts = plans.Select(p => p.Features!.StepCount.ToString()).Distinct().ToList();
        if (stepCounts.Count > 1)
        {
            discriminators.Add(new ClusterDiscriminator
            {
                FeatureName    = "complexity",
                PossibleValues = stepCounts,
                Description    = "Plans vary in number of steps"
            });
        }

        return discriminators;
    }
}
