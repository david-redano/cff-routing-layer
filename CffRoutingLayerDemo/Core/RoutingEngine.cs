// Core/RoutingEngine.cs
namespace CffRoutingLayerDemo.Core;

using System.Diagnostics;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Conversation;
using CffRoutingLayerDemo.Demo;
using CffRoutingLayerDemo.Normalization;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

public sealed class RoutingEngine
{
    private readonly ISemanticCache    _cache;
    private readonly IIntentClassifier _classifier;
    private readonly AgentRegistry     _registry;
    private readonly PlanExecutor      _executor;
    private readonly IIntentRewriter   _rewriter;
    private readonly SessionStats?     _stats;
    private readonly IDisambiguator?   _disambiguator;

    /// <summary>Exposes the underlying cache (used by Console renderer to list entries).</summary>
    public ISemanticCache Cache => _cache;

    private static readonly HashSet<string> DomainKeywords =
    [
        "cash", "flow", "invoice", "bill", "billing", "reconcile", "recon",
        "tax", "profit", "loss", "revenue", "expense", "income", "account",
        "payment", "transaction", "financial", "balance", "bank", "report",
        "budget", "forecast", "p&l", "pnl", "bookkeeping", "vendor",
        "customer", "payroll", "runway", "deduction", "write-off", "anomaly"
    ];

    public RoutingEngine(
        ISemanticCache cache,
        IIntentClassifier classifier,
        AgentRegistry registry,
        PlanExecutor executor,
        IIntentRewriter? rewriter = null,
        SessionStats? stats = null,
        IDisambiguator? disambiguator = null)
    {
        _cache          = cache;
        _classifier     = classifier;
        _registry       = registry;
        _executor       = executor;
        _rewriter       = rewriter ?? new RegexIntentRewriter();
        _stats          = stats;
        _disambiguator  = disambiguator;
    }

    /// <summary>
    /// Run the 7-stage routing pipeline for <paramref name="context"/>.
    /// When <paramref name="history"/> is provided the LLM rewriter uses it
    /// for coreference resolution, and the completed turn is appended to it.
    /// </summary>
    public async Task<(string Result, bool FromCache)> HandleAsync(
        RoutingContext context,
        ConversationHistory? history = null,
        CancellationToken ct = default)
    {
        // Stage 1 — Guardrails
        // When conversation history exists, skip the keyword check: coreferences
        // ("do the same", "now for Q2") won't contain domain keywords — the LLM
        // rewriter resolves them. The classifier's "Unknown" result is the fallback.
        bool hasHistory = history is { TotalTurns: > 0 };
        if (!hasHistory && !IsInDomain(context.UserMessage))
        {
            _stats?.RecordGuardrailRejection();
            return ("I can only assist with accounting and financial tasks. " +
                   "Try asking about cash flow, invoices, reconciliation, taxes, or P&L.", false);
        }

        // Stage 2 — Intent Rewriter (PII extraction + normalisation + coreference)
        var rwSw = Stopwatch.StartNew();
        var rewritten = await _rewriter.RewriteAsync(context.UserMessage, history, ct);
        rwSw.Stop();
        if (_rewriter is not CffRoutingLayerDemo.Normalization.RegexIntentRewriter)
            _stats?.RecordLlmRewriterCall(rwSw.ElapsedMilliseconds);
        else if (_stats is not null && rwSw.ElapsedMilliseconds > 0)
            _stats.RecordLlmRewriterCall(0); // regex: count 0 ms to keep avg meaningful
        if (rewritten.ExtractedEntities.Count > 0 || rewritten.NormalizedText != context.UserMessage)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(
                $"  ► Stage 2  Intent rewriter...              ✓ \"{rewritten.NormalizedText}\"");
            if (rewritten.ExtractedEntities.Count > 0)
            {
                var slots = string.Join(", ", rewritten.ExtractedEntities.Select(kv => $"{kv.Key}={kv.Value}"));
                Console.WriteLine($"             Slots: {slots}");
            }
            Console.ResetColor();
        }

        // Stage 3 — Semantic Cache (lookup on normalised text — not raw input)
        var cached = _cache.Lookup(rewritten.NormalizedText);
        if (cached is not null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(
                $"  ► Stage 3  Semantic cache lookup...        ✓ HIT  (similarity: {cached.Score:P0})");
            Console.WriteLine(
                $"             Intent reused: {cached.Intent.Intent}");
            Console.WriteLine(
                $"  ► Stage 7  Executing plan (from cache)...");
            Console.ResetColor();

            // Rebuild the plan with fresh entities — structure comes from the registry
            // (same YAML-driven steps as the original), but slots are re-populated from
            // the current request so stale entity values never leak through.
            var cacheAgents = _registry.Resolve(cached.Intent.AgentId);
            var cacheAgent  = cacheAgents.Count == 1
                ? cacheAgents[0]
                : cacheAgents.FirstOrDefault(a => a.Intent == cached.Intent.Intent)
                  ?? cacheAgents[0];
            var freshEntities = new Dictionary<string, string>(rewritten.ExtractedEntities);
            var freshIntent   = cached.Intent with { Entities = freshEntities };
            var freshPlan     = cacheAgent.BuildPlan(freshIntent, context);

            var enriched = context with { Entities = freshEntities };
            var exSw = Stopwatch.StartNew();
            var cachedResult = await _executor.ExecuteAsync(freshPlan, enriched, ct);
            exSw.Stop();
            _stats?.RecordExecutorMs(exSw.ElapsedMilliseconds);

            history?.AddTurn(new ConversationTurn(
                Timestamp:          DateTime.UtcNow,
                UserMessage:        context.UserMessage,
                NormalizedMessage:  rewritten.NormalizedText,
                ExtractedEntities:  rewritten.ExtractedEntities,
                Intent:             cached.Intent.Intent,
                AgentId:            cached.Intent.AgentId,
                AssistantResponse:  cachedResult,
                WasStreamed:        false));

            return (cachedResult, true);
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ► Stage 3  Semantic cache lookup...        ✗ MISS");
        Console.ResetColor();

        // Stage 4 — Intent Classification (on normalised text)
        var clSw = Stopwatch.StartNew();
        var allIntents = _classifier.ClassifyAll(rewritten.NormalizedText);
        clSw.Stop();
        if (_classifier is not CffRoutingLayerDemo.Classification.RuleBasedClassifier)
            _stats?.RecordLlmClassifierCall(clSw.ElapsedMilliseconds);
        else if (_stats is not null)
            _stats.RecordLlmClassifierCall(0);

        var knownIntents = allIntents.Where(i => i.Intent != "Unknown").ToList();
        bool fromFallback = false;

        if (knownIntents.Count == 0)
        {
            // Capability-based fallback: use extracted capabilities if available
            var capable = _registry.FindByCapability(rewritten.NormalizedText, rewritten.Capabilities);

            // Understanding-based fallback: score agents by their natural-language Understanding field
            capable ??= _registry.FindByUnderstanding(rewritten.NormalizedText);

            if (capable is not null && !string.IsNullOrEmpty(capable.Intent))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                string matchedBy = capable.Understanding.Length > 0 &&
                                   _registry.FindByCapability(rewritten.NormalizedText, rewritten.Capabilities) is null
                    ? $"understanding match [{capable.Understanding[..Math.Min(50, capable.Understanding.Length)]}…]"
                    : $"capability match [{string.Join(", ", capable.Capabilities)}]";
                Console.WriteLine(
                    $"  ► Stage 4b Fallback match...                ✓ {capable.AgentId} — {matchedBy}");
                Console.ResetColor();
                knownIntents = [new IntentResult(capable.Intent, 0.5,
                    new Dictionary<string, string>(rewritten.ExtractedEntities),
                    capable.AgentId, RequiresConfirmation: false)];
                fromFallback = true;
            }
            else
            {
                _stats?.RecordGuardrailRejection();
                return ("I could not determine what you need. Please rephrase your accounting question.", false);
            }
        }

        // Stage 4c — Disambiguation (only when Stage 4b produced low-confidence candidates)
        // Asks the LLM to verify each candidate against its plan Understanding description.
        if (fromFallback && _disambiguator is not null && knownIntents.Count > 0)
        {
            var candidates = knownIntents
                .Select(intent =>
                {
                    try
                    {
                        var agents = _registry.Resolve(intent.AgentId);
                        var agent  = agents.FirstOrDefault(a => a.Intent == intent.Intent)
                                     ?? agents[0];
                        var desc   = !string.IsNullOrEmpty(agent.Understanding)
                                     ? agent.Understanding
                                     : agent.Description;
                        return (intent.Intent, desc);
                    }
                    catch { return (intent.Intent, intent.Intent); }
                })
                .ToList();

            var disambSw = Stopwatch.StartNew();
            var selected = await _disambiguator.SelectAsync(
                rewritten.NormalizedText, candidates, ct);
            disambSw.Stop();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(selected is not null
                ? $"  ► Stage 4c Disambiguation...               ✓ {selected} ({disambSw.ElapsedMilliseconds} ms)"
                : $"  ► Stage 4c Disambiguation...               ✗ REJECTED — not an accounting intent ({disambSw.ElapsedMilliseconds} ms)");
            Console.ResetColor();

            if (selected is null)
            {
                _stats?.RecordGuardrailRejection();
                return ("I can only assist with accounting and financial tasks. " +
                        "Try asking about cash flow, invoices, reconciliation, taxes, or P&L.", false);
            }

            knownIntents = [knownIntents.First(i => i.Intent == selected)];
        }

        bool isMulti = knownIntents.Count > 1;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        if (isMulti)
            Console.WriteLine(
                $"  ► Stage 4  Intent classification...        ✓ {knownIntents.Count} intents: " +
                string.Join(", ", knownIntents.Select(i => i.Intent)));
        else
            Console.WriteLine(
                $"  ► Stage 4  Intent classification...        ✓ {knownIntents[0].Intent} ({knownIntents[0].Confidence:P0})");

        // Stages 5–7 — execute each intent; collect partial results
        var partialResults  = new List<string>(knownIntents.Count);
        ExecutionPlan?  primaryPlan   = null;
        IntentResult    primaryIntent = knownIntents[0];

        var execSw = Stopwatch.StartNew();

        for (int idx = 0; idx < knownIntents.Count; idx++)
        {
            var intent = knownIntents[idx];

            // Merge rewriter entities into classifier entities (rewriter is more precise for PII)
            var mergedEntities = rewritten.MergeEntities(intent.Entities);
            intent = intent with { Entities = mergedEntities };

            // Stage 5 — Agent Registry Lookup

            var agentManifests = _registry.Resolve(intent.AgentId);
            var agent = agentManifests.Count == 1
                ? agentManifests[0]
                : agentManifests.FirstOrDefault(a => a.Intent == intent.Intent)
                  ?? agentManifests[0];
            Console.WriteLine(
                $"  ► Stage 5  Agent registry lookup...        ✓ {agent.AgentId} ({agent.Intent})");

            // Stage 6 — Execution Plan Generation
            var plan = agent.BuildPlan(intent, context);
            Console.WriteLine(
                $"  ► Stage 6  Execution plan generated...     ✓ {plan.Steps.Count} steps");

            // Stage 7 — Execute
            Console.WriteLine(isMulti
                ? $"  ► Stage 7  Executing plan [{idx + 1}/{knownIntents.Count}]..."
                : "  ► Stage 7  Executing plan...");
            Console.ResetColor();

            var partialResult = await _executor.ExecuteAsync(
                plan, context with { Entities = mergedEntities }, ct);

            partialResults.Add(isMulti
                ? $"══ [{idx + 1}/{knownIntents.Count}] {intent.Intent} ══\n{partialResult}"
                : partialResult);

            if (idx == 0)
            {
                primaryPlan   = plan;
                primaryIntent = intent;
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
        }

        execSw.Stop();
        _stats?.RecordExecutorMs(execSw.ElapsedMilliseconds);

        var result = string.Join("\n\n", partialResults);

        // Cache only single-intent queries — compound queries are too specific to reuse reliably
        if (!isMulti && primaryPlan is not null)
        {
            _cache.Store(rewritten.NormalizedText, primaryIntent, primaryPlan);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("             [Stored in semantic cache]");
            Console.ResetColor();
        }

        history?.AddTurn(new ConversationTurn(
            Timestamp:          DateTime.UtcNow,
            UserMessage:        context.UserMessage,
            NormalizedMessage:  rewritten.NormalizedText,
            ExtractedEntities:  primaryIntent.Entities,
            Intent:             string.Join("+", knownIntents.Select(i => i.Intent)),
            AgentId:            primaryIntent.AgentId,
            AssistantResponse:  result,
            WasStreamed:        false));

        return (result, false);
    }

    private static bool IsInDomain(string message)
    {
        var lower = message.ToLowerInvariant();
        return DomainKeywords.Any(lower.Contains);
    }
}
