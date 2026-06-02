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

                // ── Step 2: skip if already cached FOR THE SAME INTENT ───────
                // Only deduplicate within the same intent. If the nearest
                // cached entry belongs to a different intent (e.g. a
                // ListInvoices entry absorbing a ListSalesOrders sample),
                // the sample must still be stored so this intent has its
                // own anchor that query-time lookups can resolve correctly.
                var existing = _cache.Lookup(rewritten.NormalizedText);
                if (existing is not null && existing.Intent.Intent == plan.Intent)
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
            // Store one entry keyed on a COMPACT form of the plan's `understanding`
            // text — stripped of leading action verbs and trimmed to ≤60 chars —
            // so it is structurally close to what LlmIntentRewriter produces at
            // query time. Titan V2 cosine between two short, similar-form strings
            // reliably scores ≥ 0.95, safely above the 0.88 threshold.
            //
            // The full `understanding` text is intentionally NOT used here: it is
            // a long natural-language sentence optimised for Stage 4b token-overlap
            // matching, not for embedding similarity.
            var anchorText = CompactForCache(plan.Understanding);
            if (!string.IsNullOrWhiteSpace(anchorText))
            {
                var anchorLookup = _cache.Lookup(anchorText);
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
                        UserMessage: anchorText,
                        CompanyId:   plan.DefaultEntities.GetValueOrDefault("companyId", "DEMO-001"),
                        Timestamp:   DateTime.UtcNow,
                        Entities:    new Dictionary<string, string>(plan.DefaultEntities));

                    var anchorPlan = agent.BuildPlan(anchorIntent, anchorContext);
                    _cache.Store(anchorText, anchorIntent, anchorPlan);

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

    private static readonly System.Text.RegularExpressions.Regex _rxLeadingVerb = new(
        @"^(Calculate|Identify|Compare|Retrieve|List|Fetch|Find|Show|Get|Determine|Count|Analyse|Analyze|Generate|Check|Rank|Provide)\s+(the\s+)?(which\s+)?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex _rxFiller = new(
        @"\b(whether\s+(any\s+|there\s+are\s+)?|a\s+percentage\s+of\s+|the\s+total\s+|the\s+number\s+of\s+|the\s+single\s+)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Strips the leading action verb and common filler phrases from a plan's
    /// understanding text, lowercases the result, and trims to ≤60 chars so it
    /// is structurally close to what <c>LlmIntentRewriter</c> produces at query time.
    /// </summary>
    private static string CompactForCache(string understanding)
    {
        if (string.IsNullOrWhiteSpace(understanding)) return "";

        var text = _rxLeadingVerb.Replace(understanding, "").TrimStart();
        text = _rxFiller.Replace(text, "").TrimStart();
        text = text.ToLowerInvariant();

        if (text.Length > 60)
        {
            var cut = text.LastIndexOf(' ', 60);
            text = cut > 0 ? text[..cut] : text[..60];
        }

        // Remove trailing punctuation and dangling conjunctions / em-dashes
        text = System.Text.RegularExpressions.Regex
            .Replace(text.TrimEnd('.', ',', ';', '?', '!'),
                     @"\s+[\-–—]?\s*(and|or|but|—|–|-)?$", "")
            .Trim();

        return text;
    }
}
