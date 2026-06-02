// Program.cs — CFF Routing Layer Demo
using DotNetEnv;
using CffRoutingLayerDemo.Bedrock;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.CompanyData;
using CffRoutingLayerDemo.Config;
using CffRoutingLayerDemo.Conversation;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Demo;
using CffRoutingLayerDemo.Normalization;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

// ── Load .env file if present ────────────────────────────────────────────────
if (File.Exists(".env"))
    Env.Load();

var config = AppConfig.FromEnvironment();

// ── Bootstrap CanonicalPhrases from plan YAML files ───────────────────────────
// Must run before constructing BedrockLlmClassifier or LlmIntentRewriter so
// their system prompts contain the full, up-to-date intent list.
var plansDir      = Path.Combine(AppContext.BaseDirectory, "plans");
var benchmarkFile         = Path.Combine(AppContext.BaseDirectory, "benchmarks", "routing_benchmarks.yaml");
var rewriterBenchmarkFile = Path.Combine(AppContext.BaseDirectory, "benchmarks", "rewriter_benchmarks.yaml");
if (Directory.Exists(plansDir))
    CanonicalPhrases.LoadFromPlans(plansDir);

// Load demo scenarios from both benchmark YAMLs so the `demo` command
// always reflects the latest suites without requiring a code change.
DemoScenarios.LoadFromBenchmarks(benchmarkFile);
DemoScenarios.LoadFromBenchmarks(rewriterBenchmarkFile);

// ── Build infrastructure ──────────────────────────────────────────────────────

ISemanticCache cache = config.UseBedrockCache
    ? new BedrockSemanticCache(config)
    : new InMemorySemanticCache();

IIntentClassifier classifier = config.UseBedrockClassifier
    ? new BedrockLlmClassifier(config)
    : new RuleBasedClassifier();

IIntentRewriter rewriter = config.UseLlmRewriter
    ? new LlmIntentRewriter(config)
    : new RegexIntentRewriter();

var registry      = AgentRegistry.LoadFromPlans(plansDir);
var dataStore     = new CompanyDataStore();
RagSummarizer? ragSummarizer = config.UseBedrockRag ? new RagSummarizer(config) : null;
var executor      = new PlanExecutor(dataStore, ragSummarizer);
var stats         = new SessionStats();
IDisambiguator? disambiguator = config.UseBedrockDisambiguator
    ? new BedrockDisambiguator(config)
    : null;
var engine        = new RoutingEngine(cache, classifier, registry, executor, rewriter, stats, disambiguator);

// Streaming fallback (used for Unknown intents)
var streaming = new BedrockStreamingConversation(config);

// Single conversation history for the entire session
var history = new ConversationHistory();

// ── Cache pre-warming ─────────────────────────────────────────────────────────
// Each sample query is normalised via the rewriter before being stored so the
// cache key matches what the router produces at query time (post-rewrite text).
if (Directory.Exists(plansDir))
{
    var warmer = new CacheWarmer(cache, rewriter, classifier, registry, executor);
    await warmer.WarmAsync(plansDir);
}

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

        case "cache":
            ConsoleRenderer.PrintCacheEntries(cache.Entries);
            continue;

        case "config":
            ConsoleRenderer.PrintConfig(
                config.UseLlmRewriter,
                config.UseBedrockCache,
                config.UseBedrockClassifier,
                config.AwsRegion,
                config.BedrockRewriterModelId,
                config.BedrockLlmModelId,
                config.CacheSimilarityThreshold);
            continue;

        case "stats":
            ConsoleRenderer.PrintStats(stats);
            continue;

        case "compact":
            if (!config.UseBedrockCache && !config.UseLlmRewriter)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine("  Compaction requires Bedrock (USE_LLM_REWRITER=true or USE_BEDROCK_CACHE=true).");
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
            await RunDemoScenariosAsync(engine, streaming, history, config, cts.Token);
            continue;
    }

    // ── Route the user message ────────────────────────────────────────────────
    ConsoleRenderer.PrintStageHeader(trimmed);

    var started = DateTime.UtcNow;
    try
    {
        var context = new RoutingContext(
            RequestId:   Guid.NewGuid().ToString(),
            UserMessage: trimmed,
            CompanyId:   "DEMO-001",
            Timestamp:   DateTime.UtcNow);

        var (result, fromCache) = await engine.HandleAsync(context, history, cts.Token);

        var elapsed = DateTime.UtcNow - started;
        var intentForStats = history.Turns.LastOrDefault()?.Intent ?? "";
        stats.RecordQuery(fromCache, (long)elapsed.TotalMilliseconds, intentForStats);
        ConsoleRenderer.PrintMetrics(elapsed, cached: fromCache);

        // Check if result signals unknown intent → streaming fallback
        if (result.StartsWith("I could not determine"))
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  [Unknown intent] Routing to streaming fallback...");
            Console.ResetColor();

            var streamResult = await streaming.ChatAsync(trimmed, history, cts.Token);
            stats.RecordStreamingFallback();
            stats.RecordLlmStreamingCall();

            // Record streaming turn in shared history
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
        else if (!result.StartsWith("I can only assist"))
        {
            ConsoleRenderer.PrintResult(result, fromCache: fromCache);
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
    if (history.NeedsCompaction && config.UseLlmRewriter)
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

// ── Demo scenarios runner ─────────────────────────────────────────────────────
static async Task RunDemoScenariosAsync(
    RoutingEngine engine,
    BedrockStreamingConversation streaming,
    ConversationHistory history,
    AppConfig config,
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

        // Each scenario runs with a fresh conversation history so that
        // guardrails are not suppressed by turns accumulated in prior scenarios.
        var scenarioHistory = new ConversationHistory();

        foreach (var turn in scenario.Turns)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"\n  Turn: {turn.Label}");
            Console.ResetColor();

            ConsoleRenderer.PrintStageHeader(turn.UserMessage);

            var ctx = new RoutingContext(
                RequestId:   Guid.NewGuid().ToString(),
                UserMessage: turn.UserMessage,
                CompanyId:   "DEMO-001",
                Timestamp:   DateTime.UtcNow);

            var start = DateTime.UtcNow;
            try
            {
                var (result, fromCache) = await engine.HandleAsync(ctx, scenarioHistory, ct);
                var elapsed = DateTime.UtcNow - start;

                if (result.StartsWith("I could not determine"))
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

                ConsoleRenderer.PrintMetrics(elapsed, cached: fromCache);
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

    ConsoleRenderer.PrintCacheEntries(engine.Cache.Entries);
}

