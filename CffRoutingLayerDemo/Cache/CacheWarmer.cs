// Cache/CacheWarmer.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Normalization;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

/// <summary>
/// Pre-warms the semantic cache from YAML plan definitions.
///
/// Each <see cref="PlanDefinition.SampleQueries"/> entry is first run through
/// <see cref="IIntentRewriter.RewriteAsync"/> so the stored cache key is the
/// normalised, PII-free text — exactly what the router looks up at query time.
///
/// Without this step a raw sample like "Create an invoice for TechVentures LLC
/// for $4,500" would never match a live query normalised to
/// "create an invoice for ${customer} ${amount}", producing a guaranteed miss.
/// </summary>
public sealed class CacheWarmer
{
    private readonly ISemanticCache    _cache;
    private readonly IIntentRewriter   _rewriter;
    private readonly IIntentClassifier _classifier;
    private readonly AgentRegistry     _registry;
    private readonly PlanExecutor      _executor;

    public CacheWarmer(
        ISemanticCache    cache,
        IIntentRewriter   rewriter,
        IIntentClassifier classifier,
        AgentRegistry     registry,
        PlanExecutor      executor)
    {
        _cache      = cache;
        _rewriter   = rewriter;
        _classifier = classifier;
        _registry   = registry;
        _executor   = executor;
    }

    /// <summary>
    /// Load all plan YAML files from <paramref name="plansDirectory"/> and
    /// warm the cache with each sample query.
    /// </summary>
    /// <param name="plansDirectory">Path to the directory containing *.yaml plan files.</param>
    /// <param name="ct">Cancellation token (pass through to Bedrock calls when live).</param>
    /// <returns>Number of cache entries created.</returns>
    public async Task<int> WarmAsync(
        string plansDirectory,
        CancellationToken ct = default)
    {
        var plans   = PlanLoader.LoadFromDirectory(plansDirectory);
        int warmed  = 0;
        int skipped = 0;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [Cache warmer] Loading {plans.Count} plan definition(s) from '{plansDirectory}'...");
        Console.ResetColor();

        foreach (var plan in plans)
        {
            // Resolve the agent so we can build a real ExecutionPlan

            AgentManifest agent;
            try
            {
                var agentManifests = _registry.Resolve(plan.AgentId);
                agent = agentManifests.Count == 1
                    ? agentManifests[0]
                    : agentManifests.FirstOrDefault(a => a.Intent == plan.Intent)
                      ?? agentManifests[0];
            }
            catch (KeyNotFoundException)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  [Cache warmer] ⚠ Unknown agentId '{plan.AgentId}' in plan '{plan.PlanId}' — skipped.");
                Console.ResetColor();
                continue;
            }

            foreach (var rawQuery in plan.SampleQueries)
            {
                ct.ThrowIfCancellationRequested();

                // ── Step 1: normalize (rewrite) the raw sample query ──────────
                // This is the critical step. The cache key MUST be the
                // normalized text, not the raw sample string.
                RewrittenIntent rewritten;
                try
                {
                    // No history context during pre-warming — these are
                    // isolated, context-free sample queries.
                    rewritten = await _rewriter.RewriteAsync(rawQuery, history: null, ct);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"  [Cache warmer] ⚠ Rewriter failed for '{rawQuery[..Math.Min(40, rawQuery.Length)]}…': {ex.Message}");
                    Console.ResetColor();
                    skipped++;
                    continue;
                }

                // ── Step 2: skip if already cached (avoid duplicate keys) ─────
                var existing = _cache.Lookup(rewritten.NormalizedText);
                if (existing is not null)
                {
                    skipped++;
                    continue;
                }

                // ── Step 3: build intent + execution plan ─────────────────────
                var mergedEntities = new Dictionary<string, string>(plan.DefaultEntities);
                foreach (var kv in rewritten.ExtractedEntities)
                    mergedEntities.TryAdd(kv.Key, kv.Value);

                var intent = new CffRoutingLayerDemo.Core.IntentResult(
                    Intent:                plan.Intent,
                    Confidence:            1.0,
                    Entities:              mergedEntities,
                    AgentId:               plan.AgentId,
                    RequiresConfirmation:  false);

                var context = new CffRoutingLayerDemo.Core.RoutingContext(
                    RequestId:   $"warm-{Guid.NewGuid().ToString("N")[..8]}",
                    UserMessage: rawQuery,
                    CompanyId:   mergedEntities.GetValueOrDefault("companyId", "DEMO-001"),
                    Timestamp:   DateTime.UtcNow,
                    Entities:    mergedEntities);

                var execPlan = agent.BuildPlan(intent, context);

                // ── Step 4: store under the normalised key ────────────────────
                _cache.Store(rewritten.NormalizedText, intent, execPlan);

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(
                    $"  [Cache warmer] ✓ [{plan.Intent,-28}] \"{rewritten.NormalizedText[..Math.Min(55, rewritten.NormalizedText.Length)]}\"");
                Console.ResetColor();

                warmed++;
            }

            // ── Understanding anchor ──────────────────────────────────────────
            // Store one entry keyed on the plan's `understanding` text directly
            // (no rewriter call). The understanding is entity-free, so its
            // EmbeddingSimulator vector is deterministic. This guarantees at
            // least one cache entry that reliably matches any live query for
            // this intent — insulating the cache from LLM non-determinism where
            // the rewriter extracts different slot names on different calls
            // (e.g. keeping "TechVentures LLC" literal on warm vs. emitting
            // ${customer} at query time, dropping the cosine below 0.75).
            if (!string.IsNullOrWhiteSpace(plan.Understanding))
            {
                var anchorLookup = _cache.Lookup(plan.Understanding);
                if (anchorLookup is null)
                {
                    var anchorIntent = new CffRoutingLayerDemo.Core.IntentResult(
                        Intent:               plan.Intent,
                        Confidence:           1.0,
                        Entities:             new Dictionary<string, string>(plan.DefaultEntities),
                        AgentId:              plan.AgentId,
                        RequiresConfirmation: false);

                    var anchorContext = new CffRoutingLayerDemo.Core.RoutingContext(
                        RequestId:   $"warm-anchor-{plan.Intent}",
                        UserMessage: plan.Understanding,
                        CompanyId:   plan.DefaultEntities.GetValueOrDefault("companyId", "DEMO-001"),
                        Timestamp:   DateTime.UtcNow,
                        Entities:    new Dictionary<string, string>(plan.DefaultEntities));

                    var anchorPlan = agent.BuildPlan(anchorIntent, anchorContext);
                    _cache.Store(plan.Understanding, anchorIntent, anchorPlan);

                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine(
                        $"  [Cache warmer] ✓ [{plan.Intent,-28}] (understanding anchor)");
                    Console.ResetColor();

                    warmed++;
                }
            }
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [Cache warmer] Done — {warmed} entries added, {skipped} skipped.");
        Console.ResetColor();

        return warmed;
    }
}
