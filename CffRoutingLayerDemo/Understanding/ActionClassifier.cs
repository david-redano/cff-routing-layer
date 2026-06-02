// Understanding/ActionClassifier.cs
namespace CffRoutingLayerDemo.Understanding;

using CffRoutingLayerDemo.Queries;

public sealed record ActionClassification(
    DomainAction Action,
    IReadOnlySet<DomainAction> ImpliedActions,
    float Confidence);

/// <summary>
/// Deterministic verb-to-action mapping. No LLM involved.
/// </summary>
public sealed class ActionClassifier
{
    private static readonly Dictionary<DomainAction, string[]> ActionVerbs = new()
    {
        [DomainAction.List]       = ["show", "list", "display", "get", "fetch", "find", "retrieve", "what are", "which"],
        [DomainAction.Create]     = ["create", "make", "generate", "send", "issue", "produce", "build"],
        [DomainAction.Compute]    = ["calculate", "compute", "estimate", "determine", "how much", "what is the total", "sum"],
        [DomainAction.Compare]    = ["compare", "reconcile", "match", "cross-reference", "versus", "vs", "difference between"],
        [DomainAction.Forecast]   = ["forecast", "predict", "project", "estimate future", "runway", "how long"],
        [DomainAction.Audit]      = ["audit", "check", "verify", "validate", "anomaly", "flag", "detect"],
        [DomainAction.Categorize] = ["categorize", "classify", "group", "segment", "break down"],
        [DomainAction.Optimize]   = ["optimize", "improve", "reduce", "maximize", "minimize", "best", "recommend"]
    };

    public ActionClassification Classify(string normalizedQuery)
    {
        var scores = new Dictionary<DomainAction, int>();

        foreach (var (action, verbs) in ActionVerbs)
        {
            var matchCount = verbs.Count(v => normalizedQuery.Contains(v, StringComparison.OrdinalIgnoreCase));
            if (matchCount > 0)
                scores[action] = matchCount;
        }

        if (scores.Count == 0)
            return new ActionClassification(DomainAction.List, new HashSet<DomainAction>(), 0.3f);

        var primary = scores.MaxBy(kv => kv.Value).Key;
        var secondary = scores
            .Where(kv => kv.Key != primary)
            .Select(kv => kv.Key)
            .ToHashSet();

        return new ActionClassification(primary, secondary, Math.Min(1f, scores[primary] * 0.4f));
    }
}
