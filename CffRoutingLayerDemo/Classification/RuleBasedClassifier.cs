// Classification/RuleBasedClassifier.cs
namespace CffRoutingLayerDemo.Classification;

using CffRoutingLayerDemo.Core;

/// <summary>
/// Keyword-rule classifier. Classify returns the highest-scoring intent;
/// ClassifyAll returns every intent that has at least one keyword match
/// (used for multi-intent detection and compound-query routing).
/// </summary>
public sealed class RuleBasedClassifier : IIntentClassifier
{
    private sealed record IntentRule(
        string Intent,
        string AgentId,
        string[] Keywords,
        bool RequiresConfirmation = false
    );

    private static readonly IntentRule[] Rules =
    [
        new("GenerateCashFlowReport", "BookkeepingAgent",
            ["cash flow", "cashflow", "inflow", "outflow", "money came in", "money went out",
             "cash report", "cash position", "cash situation"]),

        new("CreateInvoice", "InvoiceAgent",
            ["create invoice", "make invoice", "new invoice", "bill a customer", "send invoice",
             "invoice for", "bill acme", "bill tech", "invoice ${"],
            RequiresConfirmation: true),

        new("ListInvoices", "InvoiceAgent",
            ["list invoice", "show invoice", "get invoice", "all invoice", "invoice history",
             "outstanding invoice", "invoices are", "my invoice"]),

        new("ReconcileAccount", "ReconciliationAgent",
            ["reconcile", "reconciliation", "bank statement", "match transaction",
             "match my bank", "recon", "bank recon"]),

        new("EstimateTaxLiability", "TaxAgent",
            ["tax liability", "tax estimate", "how much tax", "tax owe", "quarterly tax",
             "tax bill", "our taxes", "what are our tax"]),

        new("OptimizeTaxDeductions", "TaxAgent",
            ["tax write-off", "write-offs", "tax deduction", "tax optimization",
             "tax saving", "find tax", "tax write off"]),

        new("GenerateProfitLoss", "ReportingAgent",
            ["profit and loss", "profit & loss", "p&l", "income statement",
             "are we profitable", "how is the business", "net income", "ebit", "gross profit"]),

        new("AnalyzeProfitAnomaly", "AuditAgent",
            ["profit anomal", "anomaly", "anomalies", "audit profit",
             "profit irregulari", "flag profit", "detect anomal"]),

        new("ForecastCashRunway", "ForecastAgent",
            ["cash runway", "how long is our cash", "runway", "how long can we",
             "when do we run out", "months of cash"]),
    ];

    public IntentResult Classify(string normalizedText)
    {
        var lower = normalizedText.ToLowerInvariant();

        var best = Rules
            .Select(rule => new
            {
                Rule  = rule,
                Score = (double)rule.Keywords.Count(kw => lower.Contains(kw)) / rule.Keywords.Length
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best is null)
            return new IntentResult("Unknown", 0.1, [], "None", false);

        double confidence = Math.Min(0.70 + best.Score * 0.29, 0.99);
        return new IntentResult(
            best.Rule.Intent,
            confidence,
            ExtractEntities(lower, best.Rule.Intent),
            best.Rule.AgentId,
            best.Rule.RequiresConfirmation);
    }

    public IReadOnlyList<IntentResult> ClassifyAll(string normalizedText)
    {
        var lower = normalizedText.ToLowerInvariant();

        return Rules
            .Select(rule => new
            {
                Rule  = rule,
                Score = (double)rule.Keywords.Count(kw => lower.Contains(kw)) / rule.Keywords.Length
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x =>
            {
                double confidence = Math.Min(0.70 + x.Score * 0.29, 0.99);
                return new IntentResult(
                    x.Rule.Intent,
                    confidence,
                    ExtractEntities(lower, x.Rule.Intent),
                    x.Rule.AgentId,
                    x.Rule.RequiresConfirmation);
            })
            .ToList();
    }

    private static Dictionary<string, string> ExtractEntities(string lower, string intent) =>
        intent switch
        {
            "GenerateCashFlowReport" => new()
            {
                ["period"] = lower.Contains("last month") ? "last_30_days"
                           : lower.Contains("this month") ? "current_month"
                           : lower.Contains("ytd")        ? "YTD"
                           : "last_30_days"
            },
            "EstimateTaxLiability" or "OptimizeTaxDeductions" => new()
            {
                ["year"]       = DateTime.UtcNow.Year.ToString(),
                ["entityType"] = lower.Contains("s-corp") ? "S-Corp" : "LLC"
            },
            "ReconcileAccount" => new()
            {
                ["accountId"] = lower.Contains("checking") ? "CHK-001"
                              : lower.Contains("savings")  ? "SAV-001"
                              : "CHK-001",
                ["period"] = DateTime.UtcNow.ToString("MMMM")
            },
            "GenerateProfitLoss" => new()
            {
                ["period"] = lower.Contains("last year") ? "last_year"
                           : lower.Contains("ytd")       ? "YTD"
                           : "YTD"
            },
            _ => []
        };
}
