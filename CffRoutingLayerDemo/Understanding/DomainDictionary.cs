// Understanding/DomainDictionary.cs
namespace CffRoutingLayerDemo.Understanding;

/// <summary>
/// Keyword-based domain classifier. Maps natural language signals to structured domain identifiers.
/// </summary>
public sealed class DomainDictionary
{
    private static readonly Dictionary<string, (string Domain, string SubDomain)[]> KeywordMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cash"]         = [("finance", "cashflow")],
        ["flow"]         = [("finance", "cashflow")],
        ["invoice"]      = [("finance", "invoicing")],
        ["bill"]         = [("finance", "invoicing")],
        ["billing"]      = [("finance", "invoicing")],
        ["reconcile"]    = [("finance", "reconciliation")],
        ["recon"]        = [("finance", "reconciliation")],
        ["tax"]          = [("finance", "tax")],
        ["profit"]       = [("finance", "reporting")],
        ["loss"]         = [("finance", "reporting")],
        ["p&l"]          = [("finance", "reporting")],
        ["pnl"]          = [("finance", "reporting")],
        ["revenue"]      = [("finance", "reporting")],
        ["expense"]      = [("finance", "reporting")],
        ["balance"]      = [("finance", "cashflow")],
        ["budget"]       = [("finance", "forecasting")],
        ["forecast"]     = [("finance", "forecasting")],
        ["runway"]       = [("finance", "forecasting")],
        ["payroll"]      = [("finance", "payroll")],
        ["vendor"]       = [("finance", "invoicing")],
        ["anomaly"]      = [("finance", "audit")],
        ["income"]        = [("finance", "reporting")],
        ["net income"]    = [("finance", "reporting")],
        ["net profit"]    = [("finance", "reporting")],
        ["earnings"]      = [("finance", "reporting")],
        ["sales"]        = [("sales", "orders")],
        ["order"]        = [("sales", "orders")],
        ["customer"]     = [("sales", "crm")],
        ["inventory"]    = [("inventory", "stock")],
        ["stock"]        = [("inventory", "stock")],
        ["payroll"]      = [("hr", "payroll")],
    };

    // Compound phrases scored at weight 2 — they override single-keyword ties.
    // E.g. "sales invoice" beats a lone "invoice" pointing to finance.
    private static readonly (string Phrase, string Domain, string SubDomain)[] PhraseMap =
    [
        ("sales invoice",  "sales", "invoicing"),
        ("sales order",    "sales", "orders"),
        ("purchase order", "finance", "invoicing"),
    ];

    public (string Domain, string SubDomain, float Confidence) Classify(string text)
    {
        var scores = new Dictionary<(string, string), int>();

        // Compound phrases carry weight 2 and are checked first so they can
        // decisively beat a single-keyword match pointing to a different domain.
        foreach (var (phrase, domain, subDomain) in PhraseMap)
        {
            if (!text.Contains(phrase, StringComparison.OrdinalIgnoreCase)) continue;
            var key = (domain, subDomain);
            scores[key] = scores.GetValueOrDefault(key) + 2;
        }

        foreach (var (keyword, domains) in KeywordMap)
        {
            if (!text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (domain, subDomain) in domains)
            {
                var key = (domain, subDomain);
                scores[key] = scores.GetValueOrDefault(key) + 1;
            }
        }

        if (scores.Count == 0)
            return ("general", "unknown", 0.2f);

        var best = scores.MaxBy(kv => kv.Value);
        var confidence = Math.Min(0.95f, best.Value * 0.3f);
        return (best.Key.Item1, best.Key.Item2, confidence);
    }
}
