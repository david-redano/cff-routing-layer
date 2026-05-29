// Core/RoutingEngine.cs
namespace CffRoutingLayerDemo.Core;

using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Conversation;
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
        IIntentRewriter? rewriter = null)
    {
        _cache      = cache;
        _classifier = classifier;
        _registry   = registry;
        _executor   = executor;
        _rewriter   = rewriter ?? new RegexIntentRewriter();
    }

    /// <summary>
    /// Run the 7-stage routing pipeline for <paramref name="context"/>.
    /// When <paramref name="history"/> is provided the LLM rewriter uses it
    /// for coreference resolution, and the completed turn is appended to it.
    /// </summary>
    public async Task<string> HandleAsync(
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
            return "I can only assist with accounting and financial tasks. " +
                   "Try asking about cash flow, invoices, reconciliation, taxes, or P&L.";

        // Stage 2 — Intent Rewriter (PII extraction + normalisation + coreference)
        var rewritten = await _rewriter.RewriteAsync(context.UserMessage, history, ct);
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
            var cachedResult = await _executor.ExecuteAsync(cached.Plan, enriched, ct);

            history?.AddTurn(new ConversationTurn(
                Timestamp:          DateTime.UtcNow,
                UserMessage:        context.UserMessage,
                NormalizedMessage:  rewritten.NormalizedText,
                ExtractedEntities:  rewritten.ExtractedEntities,
                Intent:             cached.Intent.Intent,
                AgentId:            cached.Intent.AgentId,
                AssistantResponse:  cachedResult,
                WasStreamed:        false));

            return cachedResult;
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ► Stage 3  Semantic cache lookup...        ✗ MISS");
        Console.ResetColor();

        // Stage 4 — Intent Classification (on normalised text)
        var intent = _classifier.Classify(rewritten.NormalizedText);

        if (intent.Intent == "Unknown")
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
                intent = new IntentResult(capable.Intent, 0.5, new Dictionary<string, string>(rewritten.ExtractedEntities),
                    capable.AgentId, RequiresConfirmation: false);
            }
            else
            {
                return "I could not determine what you need. Please rephrase your accounting question.";
            }
        }

        // Merge rewriter entities into classifier entities (rewriter is more precise for PII)
        var mergedEntities = rewritten.MergeEntities(intent.Entities);
        intent = intent with { Entities = mergedEntities };

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(
            $"  ► Stage 4  Intent classification...        ✓ {intent.Intent} ({intent.Confidence:P0})");

        // Stage 5 — Agent Registry Lookup
        var agent = _registry.Resolve(intent.AgentId);
        Console.WriteLine(
            $"  ► Stage 5  Agent registry lookup...        ✓ {agent.AgentId}");

        // Stage 6 — Execution Plan Generation
        var plan = agent.BuildPlan(intent, context);
        Console.WriteLine(
            $"  ► Stage 6  Execution plan generated...     ✓ {plan.Steps.Count} steps");

        // Stage 7 — Execute & Cache (store on normalised text key)
        Console.WriteLine("  ► Stage 7  Executing plan...");
        Console.ResetColor();

        var result = await _executor.ExecuteAsync(plan, context with { Entities = mergedEntities }, ct);

        _cache.Store(rewritten.NormalizedText, intent, plan);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("             [Stored in semantic cache]");
        Console.ResetColor();

        // Record turn in conversation history
        history?.AddTurn(new ConversationTurn(
            Timestamp:          DateTime.UtcNow,
            UserMessage:        context.UserMessage,
            NormalizedMessage:  rewritten.NormalizedText,
            ExtractedEntities:  mergedEntities,
            Intent:             intent.Intent,
            AgentId:            intent.AgentId,
            AssistantResponse:  result,
            WasStreamed:        false));

        return result;
    }

    private static bool IsInDomain(string message)
    {
        var lower = message.ToLowerInvariant();
        return DomainKeywords.Any(lower.Contains);
    }
}
