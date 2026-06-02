// Program.cs — CFF Routing Layer Demo
using DotNetEnv;
using CffRoutingLayerDemo.Bedrock;
using CffRoutingLayerDemo.CompanyData;
using CffRoutingLayerDemo.Config;
using CffRoutingLayerDemo.Conversation;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Demo;
using CffRoutingLayerDemo.Index;
using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Ranking;
using CffRoutingLayerDemo.Understanding;
using CffRoutingLayerDemo.Validation;

// ── Load .env file if present ────────────────────────────────────────────────
if (File.Exists(".env"))
    Env.Load();

var config = AppConfig.FromEnvironment();

// ── Load plans and build structural index ────────────────────────────────────
var plansDir      = Path.Combine(AppContext.BaseDirectory, "plans");
var benchmarkFile         = Path.Combine(AppContext.BaseDirectory, "benchmarks", "routing_benchmarks.yaml");
var rewriterBenchmarkFile = Path.Combine(AppContext.BaseDirectory, "benchmarks", "rewriter_benchmarks.yaml");

var plans = Directory.Exists(plansDir)
    ? PlanLoader.LoadFromDirectory(plansDir)
    : Array.Empty<PlanDefinition>();

var planIndex = new PlanIndex(plans);

// Load demo scenarios from benchmark YAMLs
DemoScenarios.LoadFromBenchmarks(benchmarkFile);
DemoScenarios.LoadFromBenchmarks(rewriterBenchmarkFile);

// ── Build infrastructure ──────────────────────────────────────────────────────

BedrockLlmHelper? bedrockHelper = config.UseLlmQueryParser || config.UseLlmRanker || config.UseBedrockRag
    ? new BedrockLlmHelper(config)
    : null;

IQueryParser queryParser = (config.UseLlmQueryParser && bedrockHelper is not null)
    ? new LlmQueryParser(bedrockHelper)
    : new RuleBasedQueryParser();

IPlanRanker ranker = (config.UseLlmRanker && bedrockHelper is not null)
    ? new LlmPlanJudge(bedrockHelper)
    : new FeatureAlignmentRanker();

IPlanValidator validator = new CompositeValidator();

var dataStore     = new CompanyDataStore();
RagSummarizer? ragSummarizer = (config.UseBedrockRag && bedrockHelper is not null)
    ? new RagSummarizer(config)
    : null;
var executor      = new PlanExecutor(dataStore, ragSummarizer);
var stats         = new SessionStats();

var pipeline = new PlanRoutingPipeline(queryParser, planIndex, ranker, validator, executor);

// Streaming fallback (used for unknown intents)
var streaming = new BedrockStreamingConversation(config);

// Single conversation history for the entire session
var history = new ConversationHistory();

// ── Banner ────────────────────────────────────────────────────────────────────
ConsoleRenderer.PrintBanner();

// ── REPL ──────────────────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write("\ncff> ");
    Console.ResetColor();

    var input = Console.ReadLine();
    if (input is null || cts.IsCancellationRequested) break;

    var trimmed = input.Trim();
    if (string.IsNullOrEmpty(trimmed)) continue;

    // ── Built-in commands ─────────────────────────────────────────────────────
    switch (trimmed.ToLowerInvariant())
    {
        case "exit":
        case "quit":
        case "q":
            goto Done;

        case "help":
            ConsoleRenderer.PrintHelp();
            continue;

        case "history":
            history.PrintToConsole();
            continue;

        case "clear":
            history.Clear();
            streaming.Reset();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Conversation history cleared.");
            Console.ResetColor();
            continue;

        case "config":
            ConsoleRenderer.PrintConfig(
                config.UseLlmQueryParser,
                config.UseLlmRanker,
                config.UseBedrockRag,
                config.AwsRegion,
                config.BedrockLlmModelId);
            continue;

        case "stats":
            ConsoleRenderer.PrintStats(stats);
            continue;

        case "compact":
            if (!config.UseLlmQueryParser)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("  Compaction requires Bedrock (USE_LLM_QUERY_PARSER=true).");
                Console.ResetColor();
            }
            else if (history.Turns.Count < 2)
            {
                Console.WriteLine("  Not enough turns to compact.");
            }
            else
            {
                var bedrockClient = new Amazon.BedrockRuntime.AmazonBedrockRuntimeClient(
                    Amazon.RegionEndpoint.GetBySystemName(config.AwsRegion));
                await history.CompactAsync(bedrockClient, config.BedrockRewriterModelId, cts.Token);
                bedrockClient.Dispose();
            }
            continue;

        case "demo":
            await RunDemoScenariosAsync(pipeline, streaming, history, config, stats, cts.Token);
            continue;
    }

    // ── Route the user message ────────────────────────────────────────────────
    ConsoleRenderer.PrintStageHeader(trimmed);

    var started = DateTime.UtcNow;
    try
    {
        var convCtx = BuildConversationContext(history);
        var (result, decision) = await pipeline.HandleAsync(trimmed, "DEMO-001", convCtx, cts.Token);
        var elapsed = DateTime.UtcNow - started;

        var intent  = decision.SelectedPlan?.Plan.Intent ?? decision.Status.ToString();
        var agentId = decision.SelectedPlan?.Plan.AgentId ?? "";
        stats.RecordQuery(fromCache: false, (long)elapsed.TotalMilliseconds, intent);
        ConsoleRenderer.PrintMetrics(elapsed, cached: false);

        if (decision.Status == RoutingStatus.Rejected || decision.Status == RoutingStatus.NoPlanFound)
        {
            // Streaming fallback for unmatched queries
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  [Fallback] Routing to streaming conversation...");
            Console.ResetColor();

            var streamResult = await streaming.ChatAsync(trimmed, history, cts.Token);
            stats.RecordStreamingFallback();
            stats.RecordLlmStreamingCall();

            history.AddTurn(new ConversationTurn(
                Timestamp:         DateTime.UtcNow,
                UserMessage:       trimmed,
                NormalizedMessage: trimmed,
                ExtractedEntities: new Dictionary<string, string>(),
                Intent:            "Streamed",
                AgentId:           "",
                AssistantResponse: streamResult,
                WasStreamed:       true));
        }
        else if (decision.Status == RoutingStatus.Success)
        {
            ConsoleRenderer.PrintResult(result, fromCache: false);
            history.AddTurn(new ConversationTurn(
                Timestamp:         DateTime.UtcNow,
                UserMessage:       trimmed,
                NormalizedMessage: trimmed,
                ExtractedEntities: decision.SelectedPlan?.SlotBindings
                    .ToDictionary(b => b.SlotName, b => b.Value)
                    ?? new Dictionary<string, string>(),
                Intent:            intent,
                AgentId:           agentId,
                AssistantResponse: result,
                WasStreamed:       false));
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  {result}");
            Console.ResetColor();
        }
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        ConsoleRenderer.PrintError(ex.Message);
    }

    // ── Auto-compact if threshold exceeded ────────────────────────────────────
    if (history.NeedsCompaction && config.UseLlmQueryParser)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  [History] Threshold reached ({history.Turns.Count} turns). Auto-compacting...");
        Console.ResetColor();

        var bedrockClient = new Amazon.BedrockRuntime.AmazonBedrockRuntimeClient(
            Amazon.RegionEndpoint.GetBySystemName(config.AwsRegion));
        await history.CompactAsync(bedrockClient, config.BedrockRewriterModelId, cts.Token);
        bedrockClient.Dispose();
    }
}

Done:
Console.WriteLine("\nGoodbye.");
bedrockHelper?.Dispose();

// ── Helpers ───────────────────────────────────────────────────────────────────

static ConversationContext? BuildConversationContext(ConversationHistory history)
{
    if (history.Turns.Count == 0) return null;
    var recent = history.Turns.TakeLast(4).ToList();
    var lastDomain = recent.LastOrDefault(t => !string.IsNullOrEmpty(t.Intent))?.Intent;
    var sessionSlots = recent
        .SelectMany(t => t.ExtractedEntities)
        .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);

    return new ConversationContext
    {
        RecentTurns = recent.Select(t => new PreviousTurn
        {
            Query          = t.UserMessage,
            ResolvedAction = t.Intent,
            Domain         = t.AgentId
        }).ToList(),
        ActiveDomain = lastDomain,
        SessionSlots = sessionSlots
    };
}

// ── Demo scenarios runner ─────────────────────────────────────────────────────
static async Task RunDemoScenariosAsync(
    PlanRoutingPipeline pipeline,
    BedrockStreamingConversation streaming,
    ConversationHistory history,
    AppConfig config,
    SessionStats stats,
    CancellationToken ct)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
    Console.WriteLine("  DEMO — Running all scenarios");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.ResetColor();

    var totalStart = DateTime.UtcNow;
    int passed = 0, failed = 0;

    foreach (var scenario in DemoScenarios.All)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  SCENARIO: {scenario.Name}");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {scenario.Description}");
        Console.ResetColor();

        var scenarioHistory = new ConversationHistory();

        foreach (var turn in scenario.Turns)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  Turn: {turn.Label}");
            Console.ResetColor();

            ConsoleRenderer.PrintStageHeader(turn.UserMessage);

            var start = DateTime.UtcNow;
            try
            {
                var convCtx = BuildConversationContext(scenarioHistory);
                var (result, decision) = await pipeline.HandleAsync(turn.UserMessage, "DEMO-001", convCtx, ct);
                var elapsed = DateTime.UtcNow - start;

                if (decision.Status == RoutingStatus.Rejected || decision.Status == RoutingStatus.NoPlanFound)
                {
                    var streamResult = await streaming.ChatAsync(turn.UserMessage, scenarioHistory, ct);
                    scenarioHistory.AddTurn(new ConversationTurn(
                        Timestamp:         DateTime.UtcNow,
                        UserMessage:       turn.UserMessage,
                        NormalizedMessage: turn.UserMessage,
                        ExtractedEntities: new Dictionary<string, string>(),
                        Intent:            "Streamed",
                        AgentId:           "",
                        AssistantResponse: streamResult,
                        WasStreamed:       true));
                }
                else if (decision.Status == RoutingStatus.Success)
                {
                    ConsoleRenderer.PrintResult(result, fromCache: false);
                }

                ConsoleRenderer.PrintMetrics(elapsed, cached: false);
                passed++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ✗ {ex.Message}");
                Console.ResetColor();
                failed++;
            }
        }
    }

    var totalElapsed = DateTime.UtcNow - totalStart;
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("\n═══════════════════════════════════════════════════════════════");
    Console.WriteLine($"  DEMO COMPLETE — {passed} passed, {failed} failed");
    Console.WriteLine($"  Total time: {totalElapsed.TotalSeconds:F1}s");
    Console.WriteLine($"  History: {history.TotalTurns} turns in main session");
    Console.WriteLine("═══════════════════════════════════════════════════════════════");
    Console.ResetColor();
}
