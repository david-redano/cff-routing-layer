// Cache/InMemorySemanticCache.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;

/// <summary>
/// In-memory semantic cache using bag-of-words cosine similarity.
/// No external dependencies — safe for test and offline use.
/// </summary>
public sealed class InMemorySemanticCache : ISemanticCache
{
    private readonly List<CacheEntry> _entries = [];
    private const double SimilarityThreshold = 0.70;

    public IReadOnlyList<CacheEntry> Entries => _entries.AsReadOnly();

    public CacheHit? Lookup(string normalizedText)
    {
        if (_entries.Count == 0) return null;

        var queryVector = Embed(normalizedText);

        return _entries
            .Select(e => new { Entry = e, Score = CosineSimilarity(queryVector, e.Vector) })
            .Where(x => x.Score >= SimilarityThreshold)
            .OrderByDescending(x => x.Score)
            .Select(x => new CacheHit(x.Entry.Intent, x.Entry.Plan, x.Score))
            .FirstOrDefault();
    }

    public void Store(string normalizedText, IntentResult intent, ExecutionPlan plan)
    {
        _entries.Add(new CacheEntry(
            NormalizedText: normalizedText,
            Vector:         Embed(normalizedText),
            Intent:         intent,
            Plan:           plan,
            StoredAt:       DateTime.UtcNow));
    }

    // ── Embedding ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Produces a fixed-length bag-of-words vector over a shared vocabulary
    /// of finance-domain tokens. Pure deterministic — no randomness.
    /// </summary>
    private static double[] Embed(string normalizedText)
    {
        var lower  = normalizedText.ToLowerInvariant();
        var tokens = lower.Split([' ', ',', '.', '?', '!', ';', ':', '-'],
                                  StringSplitOptions.RemoveEmptyEntries);
        var vector = new double[Vocabulary.Length];
        for (int i = 0; i < Vocabulary.Length; i++)
            vector[i] = tokens.Count(t => t == Vocabulary[i]);
        return vector;
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot  = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        return magA == 0 || magB == 0 ? 0 : dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }

    private static readonly string[] Vocabulary =
    [
        // reconciliation
        "reconcile", "reconciliation", "bank", "statement", "match", "recon",
        "transaction", "transactions", "account",
        // cash flow
        "cash", "flow", "cashflow", "inflow", "outflow", "position",
        // invoicing
        "invoice", "invoices", "bill", "billing", "outstanding", "create", "list",
        // tax
        "tax", "taxes", "liability", "estimate", "quarterly", "deduction", "write",
        // profit / loss
        "profit", "loss", "income", "ebit", "gross", "net",
        // anomaly / audit
        "anomaly", "anomalies", "audit",
        // forecasting
        "runway", "forecast", "predict", "project",
        // placeholders (appear after normalization)
        "${accountId}", "${customer}", "${amount}", "${period}", "${year}",
        // general structural tokens
        "month", "year", "quarter", "this", "last", "for", "report", "generate",
        "show", "analyze", "analyse", "and", "any",
    ];
}
