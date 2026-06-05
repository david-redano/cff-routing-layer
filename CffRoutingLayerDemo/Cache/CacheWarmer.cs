// Cache/CacheWarmer.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Normalization;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

/// <summary>
/// Warms the <see cref="InMemorySemanticCache"/> at startup by normalising each
/// plan's sample queries and storing an <see cref="IntentResult"/> keyed to
/// the normalised text.  This gives the cache an initial hot state without
/// requiring real user traffic.
/// </summary>
public sealed class CacheWarmer
{
    private readonly InMemorySemanticCache _cache;
    private readonly RegexIntentRewriter   _rewriter;
    private readonly IIntentClassifier     _classifier;
    private readonly AgentRegistry         _registry;
    private readonly PlanExecutor          _executor;

    public CacheWarmer(
        InMemorySemanticCache cache,
        RegexIntentRewriter   rewriter,
        IIntentClassifier     classifier,
        AgentRegistry         registry,
        PlanExecutor          executor)
    {
        _cache      = cache;
        _rewriter   = rewriter;
        _classifier = classifier;
        _registry   = registry;
        _executor   = executor;
    }

    /// <summary>
    /// Loads all YAML plan files from <paramref name="plansDirectory"/> and
    /// stores a normalised cache entry for each sample query.
    /// </summary>
    public async Task WarmAsync(string plansDirectory)
    {
        var plans = PlanLoader.LoadFromDirectory(plansDirectory);

        foreach (var plan in plans)
        {
            if (plan.SampleQueries.Count == 0) continue;

            // Build a minimal ExecutionPlan from the definition for caching
            var execPlan = new ExecutionPlan(
                PlanId: plan.PlanId,
                Intent: plan.Intent,
                Steps: plan.Steps
                    .Select(s => new PlanStep(s.Id, s.Action, [], []))
                    .ToList());

            // Build the IntentResult directly from the plan definition so we
            // don't depend on the classifier matching every sample query correctly.
            var intentResult = new IntentResult(
                Intent:              plan.Intent,
                Confidence:          0.95,
                Entities:            plan.DefaultEntities,
                AgentId:             plan.AgentId,
                RequiresConfirmation: false);

            foreach (var sampleQuery in plan.SampleQueries)
            {
                var rewritten = await _rewriter.RewriteAsync(sampleQuery);
                _cache.Store(rewritten.NormalizedText, intentResult, execPlan);
            }
        }
    }
}
