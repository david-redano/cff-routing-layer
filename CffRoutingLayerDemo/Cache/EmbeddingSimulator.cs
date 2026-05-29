// Cache/EmbeddingSimulator.cs
namespace CffRoutingLayerDemo.Cache;

/// <summary>
/// Produces keyword-dimension vectors that simulate semantic similarity
/// without any external API. Phrases sharing domain keywords score high
/// cosine similarity; unrelated phrases score near zero.
/// </summary>
public sealed class EmbeddingSimulator
{
    private static readonly string[] Dimensions =
    [
        "cashflow", "cash", "flow", "inflow", "outflow",
        "invoice", "bill", "customer", "billing",
        "reconcile", "reconciliation", "bank", "statement", "match",
        "tax", "taxes", "liability", "deduction", "bracket", "write-off",
        "profit", "loss", "revenue", "expense", "income", "ebit",
        "report", "summary", "forecast", "budget",
        "balance", "account", "transaction", "payment", "vendor",
        "runway", "burn", "sustain", "anomaly", "drop", "decline"
    ];

    public double[] Embed(string text)
    {
        var lower = text.ToLowerInvariant();
        return Dimensions
            .Select(d => lower.Contains(d)
                ? 1.0 + lower.Split(' ').Count(w => w.Contains(d)) * 0.15
                : 0.0)
            .ToArray();
    }
}
