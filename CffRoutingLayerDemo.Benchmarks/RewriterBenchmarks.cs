// Benchmarks/RewriterBenchmarks.cs
namespace CffRoutingLayerDemo.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using CffRoutingLayerDemo.Normalization;

/// <summary>
/// Compares RegexIntentRewriter (no I/O) vs LlmIntentRewriter (Bedrock call).
/// Run with USE_LLM_REWRITER=false first (regex only) to get a baseline,
/// then with live Bedrock credentials for the full comparison.
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class RewriterBenchmarks
{
    private static readonly RegexIntentRewriter RegexRewriter = new();

    // Queries that exercise all normalisation paths: PII, surface forms, paraphrases
    private static readonly string[] BenchmarkQueries =
    [
        "Generate a cash flow report for last month",
        "Create an invoice for TechVentures LLC for $4,500",
        "Reconcile account CHK-001 for May",
        "Estimate tax liability for Q2 2024 for Acme Corp S-Corp",
        "Generate P&L for FY2024",
        "Analyze profit anomaly for Acme Corp last 6 months",
        "Optimize tax deductions for Acme Corp for 2024",
        "Forecast cash runway for Acme Corp",
    ];

    [ParamsSource(nameof(Queries))]
    public string Query { get; set; } = "";

    public static IEnumerable<string> Queries => BenchmarkQueries;

    [Benchmark(Baseline = true, Description = "Regex rewriter (no I/O)")]
    public async Task RegexRewrite()
        => await RegexRewriter.RewriteAsync(Query);
}
