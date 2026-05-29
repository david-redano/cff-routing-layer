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
        SessionStats? stats = null)
    {
        _cache      = cache;
        _classifier = classifier;
        _registry   = registry;
        _executor   = executor;
        _rewriter   = rewriter ?? new RegexIntentRewriter();
        _stats      = stats;
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

            var enriched = context with { Entities = rewritten.ExtractedEntities };
            var exSw = Stopwatch.StartNew();
            var cachedResult = await _executor.ExecuteAsync(cached.Plan, enriched, ct);
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

        if (knownIntents.Count == 0)
        {
            // Capability-based fallback: scan registered agents for a capability
            // keyword that appears in the normalised message.
            var capable = _registry.FindByCapability(rewritten.NormalizedText);
            if (capable is not null && !string.IsNullOrEmpty(capable.Intent))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(
                    $"  ► Stage 4b Capability match...             ✓ {capable.AgentId} [{string.Join(", ", capable.Capabilities)}]");
                Console.ResetColor();
                knownIntents = [new IntentResult(capable.Intent, 0.5,
                    new Dictionary<string, string>(rewritten.ExtractedEntities),
                    capable.AgentId, RequiresConfirmation: false)];
            }
            else
            {
                _stats?.RecordGuardrailRejection();
                return ("I could not determine what you need. Please rephrase your accounting question.", false);
            }
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
            var agent = _registry.Resolve(intent.AgentId);
            Console.WriteLine(
                $"  ► Stage 5  Agent registry lookup...        ✓ {agent.AgentId}");

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
