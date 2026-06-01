// Demo/DemoScenarios.cs
namespace CffRoutingLayerDemo.Demo;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Demo scenarios loaded from <c>benchmarks/routing_benchmarks.yaml</c>.
/// Each benchmark suite becomes one <see cref="Scenario"/>; each query
/// within the suite becomes one <see cref="Turn"/>.
/// </summary>
public static class DemoScenarios
{
    public sealed record Turn(string Label, string UserMessage);
    public sealed record Scenario(string Name, string Description, Turn[] Turns);

    /// <summary>Active scenario list — populated by <see cref="LoadFromBenchmarks"/>
    /// at startup. Empty until that call completes.</summary>
    public static Scenario[] All { get; private set; } = [];

    // ── YAML loading ──────────────────────────────────────────────────────────

    private static readonly IDeserializer _deserializer =
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    /// <summary>
    /// Load scenarios from <paramref name="yamlPath"/> and append to <see cref="All"/>.
    /// Suites with no queries are skipped.  Falls back silently if the file is missing.
    /// </summary>
    public static void LoadFromBenchmarks(string yamlPath)
    {
        if (!File.Exists(yamlPath))
            return;

        var file = _deserializer.Deserialize<BenchmarkFile>(File.ReadAllText(yamlPath));

        var scenarios = file.Benchmarks
            .Where(s => s.Queries.Count > 0)
            .Select(suite => new Scenario(
                suite.Name,
                suite.Description,
                suite.Queries
                    .Select((q, i) =>
                    {
                        var label = !string.IsNullOrEmpty(q.Intent)
                            ? q.Intent
                            : $"Query {i + 1}";
                        if (!string.IsNullOrEmpty(q.Note))
                            label += $"  [{q.Note}]";
                        if (q.ExpectCacheHit)
                            label += "  (expect cache hit)";
                        if (q.ExpectRejected)
                            label += "  (expect rejection)";
                        if (!string.IsNullOrEmpty(q.ExpectFallback))
                            label += $"  (fallback: {q.ExpectFallback})";
                        return new Turn(label, q.Query);
                    })
                    .ToArray()))
            .ToArray();

        if (scenarios.Length > 0)
            All = [..All, ..scenarios];
    }

    // ── YAML model (private) ──────────────────────────────────────────────────

    private sealed class BenchmarkFile
    {
        public List<BenchmarkSuite> Benchmarks { get; set; } = [];
    }

    private sealed class BenchmarkSuite
    {
        public string              Name        { get; set; } = "";
        public string              Description { get; set; } = "";
        public List<BenchmarkQuery> Queries    { get; set; } = [];
    }

    private sealed class BenchmarkQuery
    {
        public string Query                    { get; set; } = "";
        public string Intent                   { get; set; } = "";
        public bool   ExpectCacheHit           { get; set; }
        public bool   ExpectRejected           { get; set; }
        public string ExpectFallback           { get; set; } = "";
        public string Note                     { get; set; } = "";
        public int    Round                    { get; set; }
        public double ExpectedMinConfidence    { get; set; }
    }
}
