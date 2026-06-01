// Benchmarks/RewriterBenchmarks.cs
namespace CffRoutingLayerDemo.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using CffRoutingLayerDemo.Normalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Compares RegexIntentRewriter (no I/O) vs LlmIntentRewriter (Bedrock call).
/// Queries are loaded from <c>benchmarks/rewriter_benchmarks.yaml</c> (suite 0 only —
/// surface normalisation + PII extraction cases).
/// Run with USE_LLM_REWRITER=false first (regex only) to get a baseline,
/// then with live Bedrock credentials for the full comparison.
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class RewriterBenchmarks
{
    private static readonly RegexIntentRewriter RegexRewriter = new();

    // ── YAML model (private) ──────────────────────────────────────────────────

    private sealed class BenchmarkFile
    {
        public List<BenchmarkSuite> Benchmarks { get; set; } = [];
    }
    private sealed class BenchmarkSuite
    {
        public string Name { get; set; } = "";
        public List<BenchmarkQuery> Queries { get; set; } = [];
    }
    private sealed class BenchmarkQuery
    {
        public string Query { get; set; } = "";
    }

    private static IEnumerable<string> LoadQueriesFromYaml()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "benchmarks", "rewriter_benchmarks.yaml");
            if (File.Exists(candidate))
            {
                var des = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                var file = des.Deserialize<BenchmarkFile>(File.ReadAllText(candidate));
                // Use only the first suite (surface normalisation) for micro-benchmarks
                var suite = file.Benchmarks.FirstOrDefault(s => s.Queries.Count > 0);
                if (suite is not null)
                    return suite.Queries.Select(q => q.Query).Where(q => !string.IsNullOrEmpty(q));
            }
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        // Fallback if YAML not found
        return ["Generate a cash flow report for last month", "Generate P&L for FY2024"];
    }

    [ParamsSource(nameof(Queries))]
    public string Query { get; set; } = "";

    public static IEnumerable<string> Queries => LoadQueriesFromYaml();

    [Benchmark(Baseline = true, Description = "Regex rewriter (no I/O)")]
    public async Task RegexRewrite()
        => await RegexRewriter.RewriteAsync(Query);
}
