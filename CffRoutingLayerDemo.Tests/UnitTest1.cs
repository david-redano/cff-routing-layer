// Tests/ClassificationTests.cs
namespace CffRoutingLayerDemo.Tests;

using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Normalization;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// ── YAML test model ───────────────────────────────────────────────────────────

file sealed class ClassificationTestCase
{
    public string Name           { get; set; } = "";
    public string Query          { get; set; } = "";
    public string ExpectedIntent { get; set; } = "";
    public string ExpectedAgent  { get; set; } = "";
}
file sealed class ClassificationTestFile
{
    public List<ClassificationTestCase> Tests { get; set; } = [];
}

file sealed class CacheTestCase
{
    public string Name          { get; set; } = "";
    public string FirstQuery    { get; set; } = "";
    public string SecondQuery   { get; set; } = "";
    public bool   ExpectCacheHit { get; set; }
}
file sealed class CacheTestFile
{
    public List<CacheTestCase> Tests { get; set; } = [];
}

// ── Helpers ───────────────────────────────────────────────────────────────────

file static class YamlTestLoader
{
    private static readonly IDeserializer Deserializer =
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    public static T Load<T>(string relativePath)
    {
        // Tests run from the project output dir; walk up to find the yaml files
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return Deserializer.Deserialize<T>(File.ReadAllText(candidate));
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        throw new FileNotFoundException($"Could not locate test YAML: {relativePath}");
    }
}

// ── Classification tests ──────────────────────────────────────────────────────

public sealed class ClassificationTests
{
    private static readonly RuleBasedClassifier Classifier  = new();
    private static readonly RegexIntentRewriter Rewriter    = new();

    public static IEnumerable<object[]> TestCases()
    {
        var file = YamlTestLoader.Load<ClassificationTestFile>(
            "CffRoutingLayerDemo/tests/classification_tests.yaml");
        return file.Tests.Select(t => new object[] { t.Name, t.Query, t.ExpectedIntent, t.ExpectedAgent });
    }

    [Theory]
    [MemberData(nameof(TestCases))]
    public async Task Classifies_SampleQuery_ToExpectedIntent(
        string name, string query, string expectedIntent, string expectedAgent)
    {
        var rewritten = await Rewriter.RewriteAsync(query);
        var result    = Classifier.Classify(rewritten.NormalizedText);

        Assert.Equal(expectedIntent, result.Intent);
        Assert.Equal(expectedAgent,  result.AgentId);
    }
}

// ── Cache tests ───────────────────────────────────────────────────────────────

public sealed class CacheTests
{
    private static readonly RegexIntentRewriter Rewriter   = new();
    private static readonly RuleBasedClassifier Classifier = new();
    private static readonly AgentRegistry       Registry   = AgentRegistry.BuildDefault();
    private static readonly PlanExecutor        Executor   = new();

    public static IEnumerable<object[]> TestCases()
    {
        var file = YamlTestLoader.Load<CacheTestFile>(
            "CffRoutingLayerDemo/tests/cache_tests.yaml");
        return file.Tests.Select(t => new object[] { t.Name, t.FirstQuery, t.SecondQuery, t.ExpectCacheHit });
    }

    [Theory]
    [MemberData(nameof(TestCases))]
    public async Task SemanticCache_ReturnsExpectedHit(
        string name, string firstQuery, string secondQuery, bool expectHit)
    {
        var cache  = new InMemorySemanticCache();
        var engine = new RoutingEngine(cache, Classifier, Registry, Executor, Rewriter);
        var ctx1   = MakeContext(firstQuery);

        // First query — populates cache
        await engine.HandleAsync(ctx1);

        // Second query — should hit (or miss) based on semantic similarity
        var ctx2     = MakeContext(secondQuery);
        var rewritten = await Rewriter.RewriteAsync(secondQuery);
        var hit       = cache.Lookup(rewritten.NormalizedText);

        Assert.Equal(expectHit, hit is not null);
    }

    private static RoutingContext MakeContext(string msg) =>
        new(Guid.NewGuid().ToString(), msg, "TEST-001", DateTime.UtcNow);
}

// ── Cache warmer tests ────────────────────────────────────────────────────────

public sealed class CacheWarmerTests
{
    [Fact]
    public async Task Warmer_StoresNormalisedKeys_NotRawSampleQueries()
    {
        var cache      = new InMemorySemanticCache();
        var rewriter   = new RegexIntentRewriter();
        var classifier = new RuleBasedClassifier();
        var registry   = AgentRegistry.BuildDefault();
        var executor   = new PlanExecutor();
        var warmer     = new CacheWarmer(cache, rewriter, classifier, registry, executor);

        // Find plans directory relative to test output
        var plansDir = FindPlansDir();
        if (plansDir is null)
        {
            // Skip gracefully if YAML files aren't present (CI without content)
            return;
        }

        await warmer.WarmAsync(plansDir);
        Assert.True(cache.Entries.Count > 0, "Cache should have at least one entry after warming");

        // Verify: no raw sample query like "Create an invoice for TechVentures LLC for $4,500"
        // should appear verbatim as a cache key — it must be normalised
        foreach (var entry in cache.Entries)
        {
            Assert.DoesNotContain("TechVentures LLC", entry.NormalizedText);
            Assert.DoesNotContain("$4,500",           entry.NormalizedText);
            Assert.DoesNotContain("CHK-001",           entry.NormalizedText);
        }
    }

    [Fact]
    public async Task Warmer_NormalisedKey_HitsOnParaphrase()
    {
        var cache      = new InMemorySemanticCache();
        var rewriter   = new RegexIntentRewriter();
        var classifier = new RuleBasedClassifier();
        var registry   = AgentRegistry.BuildDefault();
        var executor   = new PlanExecutor();
        var warmer     = new CacheWarmer(cache, rewriter, classifier, registry, executor);

        var plansDir = FindPlansDir();
        if (plansDir is null) return;

        await warmer.WarmAsync(plansDir);

        // A query with different PII but same structure should hit the cache
        var paraphrase = await rewriter.RewriteAsync("Create an invoice for Global Corp for $2,200");
        var hit        = cache.Lookup(paraphrase.NormalizedText);

        Assert.NotNull(hit);
        Assert.Equal("CreateInvoice", hit!.Intent.Intent);
    }

    private static string? FindPlansDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "CffRoutingLayerDemo", "plans");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir) ?? dir;
        }
        return null;
    }
}

// ── Rewriter normalisation tests ──────────────────────────────────────────────

public sealed class RewriterTests
{
    private static readonly RegexIntentRewriter Rewriter = new();

    [Theory]
    [InlineData("Create an invoice for TechVentures LLC for $4,500", "${customer}", "${amount}")]
    [InlineData("Reconcile account CHK-001 for May",                  "${accountId}", "${period}")]
    [InlineData("Estimate tax for Acme Corp S-Corp in 2024",          "${customer}", "${year}")]
    public async Task Rewriter_ExtractsPIIEntities(string query, params string[] expectedPlaceholders)
    {
        var result = await Rewriter.RewriteAsync(query);

        foreach (var ph in expectedPlaceholders)
            Assert.Contains(ph, result.NormalizedText);
    }

    [Theory]
    [InlineData("P&L report for Q3",         "profit and loss")]
    [InlineData("pnl for this year",          "profit and loss")]
    [InlineData("recon the account",          "reconcile")]
    [InlineData("bill TechVentures for work", "invoice")]
    public async Task Rewriter_NormalisesSurfaceForms(string query, string expectedToken)
    {
        var result = await Rewriter.RewriteAsync(query);
        Assert.Contains(expectedToken, result.NormalizedText, StringComparison.OrdinalIgnoreCase);
    }
}

