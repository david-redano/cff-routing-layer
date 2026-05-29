// Benchmarks/CacheWarmerBenchmarks.cs
namespace CffRoutingLayerDemo.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Normalization;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

/// <summary>
/// Measures time to pre-warm the semantic cache from all YAML plan files.
/// Key insight: pre-warming normalises each sample query before storing it,
/// so the stored key always matches what the router produces at query time.
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class CacheWarmerBenchmarks
{
    private string? _plansDir;

    [GlobalSetup]
    public void Setup()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var c = Path.Combine(dir, "CffRoutingLayerDemo", "plans");
            if (Directory.Exists(c)) { _plansDir = c; break; }
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
    }

    [Benchmark(Description = "WarmAsync — regex rewriter, local cache")]
    public async Task WarmWithRegexRewriter()
    {
        if (_plansDir is null) return;

        var cache      = new InMemorySemanticCache();
        var rewriter   = new RegexIntentRewriter();
        var classifier = new RuleBasedClassifier();
        var registry   = AgentRegistry.BuildDefault();
        var executor   = new PlanExecutor();
        var warmer     = new CacheWarmer(cache, rewriter, classifier, registry, executor);

        await warmer.WarmAsync(_plansDir);
    }
}

// ── Shared YAML model (used by all benchmark files) ───────────────────────────

public sealed class BenchmarkQueryEntry
{
    public string Query          { get; set; } = "";
    public string Intent         { get; set; } = "";
    public bool   ExpectCacheHit { get; set; }
}

public sealed class BenchmarkScenario
{
    public string                   Name        { get; set; } = "";
    public string                   Description { get; set; } = "";
    public string?                  PlansDirectory { get; set; }
    public List<BenchmarkQueryEntry> Queries    { get; set; } = [];
}

public sealed class BenchmarkFile
{
    public List<BenchmarkScenario> Benchmarks { get; set; } = [];
}
