// Benchmarks/RoutingPipelineBenchmarks.cs
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

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class RoutingPipelineBenchmarks
{
    private RoutingEngine _engine = null!;
    private RoutingContext[] _contexts = [];

    // Queries loaded from YAML — avoids hardcoded strings in benchmark code
    public IEnumerable<string> Queries => LoadQueries();

    [ParamsSource(nameof(Queries))]
    public string Query { get; set; } = "";

    [GlobalSetup]
    public async Task Setup()
    {
        var cache      = new InMemorySemanticCache();
        var rewriter   = new RegexIntentRewriter();
        var classifier = new RuleBasedClassifier();
        var registry   = AgentRegistry.BuildDefault();
        var executor   = new PlanExecutor();

        _engine = new RoutingEngine(cache, classifier, registry, executor, rewriter);

        // Pre-warm cache with normalised sample queries
        var plansDir = FindDir("plans");
        if (plansDir is not null)
        {
            var warmer = new CacheWarmer(cache, rewriter, classifier, registry, executor);
            await warmer.WarmAsync(plansDir);
        }
    }

    [Benchmark]
    public async Task<string> RouteQuery()
    {
        var ctx = new RoutingContext(
            Guid.NewGuid().ToString(),
            Query,
            "BENCH-001",
            DateTime.UtcNow);
        return await _engine.HandleAsync(ctx);
    }

    private static IEnumerable<string> LoadQueries()
    {
        var file = FindFile("benchmarks/routing_benchmarks.yaml");
        if (file is null)
            return ["Generate a cash flow report for last month"];

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var doc = deserializer.Deserialize<BenchmarkFile>(File.ReadAllText(file));
        return doc.Benchmarks
            .FirstOrDefault()?.Queries
            .Select(q => q.Query)
            ?? ["Generate a cash flow report for last month"];
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
