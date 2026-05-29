// Benchmarks/MultiIntentBenchmarks.cs
namespace CffRoutingLayerDemo.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Normalization;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Compares single-intent vs multi-intent routing along two axes:
///
///   1. <see cref="ClassifyAll_vs_Classify"/> — pure classification overhead:
///      how much extra work does <c>ClassifyAll</c> add over <c>Classify</c>
///      when the message triggers N intents?
///
///   2. <see cref="RouteMultiIntent"/> / <see cref="RouteSingleIntent"/> —
///      full end-to-end pipeline latency so you can see the per-extra-plan cost
///      at routing time (plan lookup + executor × N plans).
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class MultiIntentBenchmarks
{
    private RuleBasedClassifier _classifier = null!;
    private RoutingEngine       _engineSingle = null!;
    private RoutingEngine       _engineMulti  = null!;

    // ── Parameter sets ────────────────────────────────────────────────────────

    public IEnumerable<string> SingleIntentQueries => LoadQueriesFromScenario(0);
    public IEnumerable<string> MultiIntentQueries  => LoadQueriesFromScenario(2); // index 2 = multi-intent scenario

    [ParamsSource(nameof(MultiIntentQueries))]
    public string MultiQuery { get; set; } = "Show me cash flow and also what's my tax liability for this year";

    [ParamsSource(nameof(SingleIntentQueries))]
    public string SingleQuery { get; set; } = "Generate a cash flow report for last month";

    // ── Setup ─────────────────────────────────────────────────────────────────

    [GlobalSetup]
    public async Task Setup()
    {
        _classifier = new RuleBasedClassifier();

        var cache    = new InMemorySemanticCache();
        var rewriter = new RegexIntentRewriter();
        var registry = AgentRegistry.BuildDefault();
        var executor = new PlanExecutor();

        // Both engines share the same components; split only so [Benchmark]
        // methods can reference the engine without depending on the param.
        _engineSingle = new RoutingEngine(cache, _classifier, registry, executor, rewriter);
        _engineMulti  = new RoutingEngine(cache, _classifier, registry, executor, rewriter);

        // Warm cache for single-intent queries only — multi-intent results are
        // deliberately not cached (see RoutingEngine) so we get the raw cost.
        var plansDir = FindDir("plans");
        if (plansDir is not null)
        {
            var warmer = new CacheWarmer(cache, rewriter, _classifier, registry, executor);
            await warmer.WarmAsync(plansDir);
        }
    }

    // ── Classification benchmarks ─────────────────────────────────────────────

    /// <summary>
    /// Measures ClassifyAll overhead vs Classify for the same multi-intent message.
    /// Both operate on the same RuleBasedClassifier instance; the only difference
    /// is ClassifyAll collects *all* scoring rules instead of stopping at the first.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Classify (single-best, baseline)")]
    public IntentResult Classify_SingleBest()
        => _classifier.Classify(MultiQuery);

    [Benchmark(Description = "ClassifyAll (all matching intents)")]
    public IReadOnlyList<IntentResult> ClassifyAll_AllMatching()
        => _classifier.ClassifyAll(MultiQuery);

    /// <summary>
    /// Counts detected intents per query — useful for verifying the classifier
    /// fan-out without measuring routing overhead.
    /// </summary>
    [Benchmark(Description = "ClassifyAll — count detected intents")]
    public int ClassifyAll_IntentCount()
        => _classifier.ClassifyAll(MultiQuery).Count(i => i.Intent != "Unknown");

    // ── End-to-end routing benchmarks ─────────────────────────────────────────

    /// <summary>
    /// Full pipeline for a known single-intent query (cache MISS path).
    /// Baseline for comparing against multi-intent overhead.
    /// </summary>
    [Benchmark(Description = "Route — single intent (cache miss)")]
    public async Task<string> RouteSingleIntent()
    {
        var ctx = new RoutingContext(
            Guid.NewGuid().ToString(),
            SingleQuery,
            "BENCH-MI-001",
            DateTime.UtcNow);
        var (result, _) = await _engineSingle.HandleAsync(ctx);
        return result;
    }

    /// <summary>
    /// Full pipeline for a compound query. Measures the cost of running N plans
    /// sequentially: classify-all → N × (agent lookup + plan build + executor).
    /// </summary>
    [Benchmark(Description = "Route — multi-intent (N plans, no cache)")]
    public async Task<string> RouteMultiIntent()
    {
        var ctx = new RoutingContext(
            Guid.NewGuid().ToString(),
            MultiQuery,
            "BENCH-MI-001",
            DateTime.UtcNow);
        var (result, _) = await _engineMulti.HandleAsync(ctx);
        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IEnumerable<string> LoadQueriesFromScenario(int scenarioIndex)
    {
        var file = FindFile("benchmarks/routing_benchmarks.yaml");
        if (file is null)
            return scenarioIndex == 0
                ? ["Generate a cash flow report for last month"]
                : ["Show me cash flow and also what's my tax liability for this year"];

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var doc = deserializer.Deserialize<BenchmarkFile>(File.ReadAllText(file));
        var scenario = doc.Benchmarks.ElementAtOrDefault(scenarioIndex);
        return scenario?.Queries.Select(q => q.Query)
            ?? (scenarioIndex == 0
                ? ["Generate a cash flow report for last month"]
                : ["Show me cash flow and also what's my tax liability for this year"]);
    }

    private static string? FindDir(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var c = Path.Combine(dir, "CffRoutingLayerDemo", name);
            if (Directory.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        return null;
    }

    private static string? FindFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var c = Path.Combine(dir, "CffRoutingLayerDemo", relative);
            if (File.Exists(c)) return c;
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        return null;
    }
}
