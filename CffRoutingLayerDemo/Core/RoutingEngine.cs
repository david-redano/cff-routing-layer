// Core/RoutingEngine.cs
namespace CffRoutingLayerDemo.Core;

using CffRoutingLayerDemo.Agents;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Normalization;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

/// <summary>
/// Orchestrates the rule-based routing path:
///   Stage 1 — Domain guardrails
///   Stage 2 — Semantic cache lookup (normalised key)
///   Stage 3 — Intent classification
///   Stage 4 — Agent registry lookup
///   Stage 5 — Execution plan generation
///   Stage 6 — Plan execution
///
/// Returns <c>(result, fromCache)</c> so callers know whether the result
/// came from cache.  Multi-intent queries are handled by classifying all
/// intents and joining their results.
/// </summary>
public sealed class RoutingEngine
{
    private readonly ISemanticCache    _cache;
    private readonly IIntentClassifier _classifier;
    private readonly AgentRegistry     _registry;
    private readonly PlanExecutor      _executor;
    private readonly RegexIntentRewriter _rewriter;

    private static readonly HashSet<string> DomainKeywords =
    [
        "cash", "flow", "invoice", "bill", "reconcile", "tax", "profit",
        "loss", "revenue", "expense", "income", "account", "payment",
        "transaction", "financial", "balance", "bank", "report", "budget",
        "forecast", "p&l", "bookkeeping", "vendor", "customer", "payroll",
        "anomaly", "audit", "runway",
    ];

    public RoutingEngine(
        ISemanticCache     cache,
        IIntentClassifier  classifier,
        AgentRegistry      registry,
        PlanExecutor       executor,
        RegexIntentRewriter rewriter)
    {
        _cache      = cache;
        _classifier = classifier;
        _registry   = registry;
        _executor   = executor;
        _rewriter   = rewriter;
    }

    /// <returns>
    /// A tuple of (<c>result</c>, <c>fromCache</c>) where <c>fromCache</c> is
    /// <see langword="true"/> when the answer was served from the semantic cache.
    /// </returns>
    public async Task<(string Result, bool FromCache)> HandleAsync(
        RoutingContext context,
        CancellationToken ct = default)
    {
        if (!IsInDomain(context.UserMessage))
            return ("I can only assist with accounting and financial tasks. " +
                    "Try asking about cash flow, invoices, reconciliation, taxes, or P&L.", false);

        var rewritten = await _rewriter.RewriteAsync(context.UserMessage);

        // Stage 2 — Semantic cache
        var cached = _cache.Lookup(rewritten.NormalizedText);
        if (cached is not null)
        {
            var cachedResult = await _executor.ExecuteAsync(cached.Plan, context, ct);
            return (cachedResult, true);
        }

        // Stage 3 — Multi-intent classification
        var allIntents = _classifier.ClassifyAll(rewritten.NormalizedText)
            .Where(r => r.Intent != "Unknown")
            .ToList();

        if (allIntents.Count == 0)
            return ("I could not determine what you need. Please rephrase your accounting question.", false);

        if (allIntents.Count == 1)
        {
            var result = await RouteIntentAsync(allIntents[0], context, ct);
            return (result, false);
        }

        // Multiple intents — route each and join results
        var parts = new List<string>(allIntents.Count);
        for (int i = 0; i < allIntents.Count; i++)
        {
            var part = await RouteIntentAsync(allIntents[i], context, ct);
            parts.Add($"══ [{i + 1}/{allIntents.Count}] {allIntents[i].Intent} ══\n{part}");
        }
        return (string.Join("\n\n", parts), false);
    }

    private async Task<string> RouteIntentAsync(
        IntentResult intent, RoutingContext context, CancellationToken ct)
    {
        IAgent agent;
        try
        {
            agent = _registry.Resolve(intent.AgentId);
        }
        catch (InvalidOperationException)
        {
            return $"No agent available for intent '{intent.Intent}'.";
        }

        var plan   = agent.BuildPlan(intent, context);
        var result = await _executor.ExecuteAsync(plan, context, ct);
        _cache.Store(context.UserMessage, intent, plan);
        return result;
    }

    private static bool IsInDomain(string message)
    {
        var lower = message.ToLowerInvariant();
        return DomainKeywords.Any(lower.Contains);
    }
}
