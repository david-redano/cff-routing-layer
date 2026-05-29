// Classification/RuleBasedClassifier.cs
namespace CffRoutingLayerDemo.Classification;

using CffRoutingLayerDemo.Core;

/// <summary>
/// Keyword-rule classifier. Matches the highest-scoring intent pattern.
/// Replace with an LLM-backed classifier in production.
/// </summary>
public sealed class RuleBasedClassifier : IIntentClassifier
{
    private record IntentRule(
        string Intent,
        string AgentId,
        string[] Keywords,
        bool RequiresConfirmation = false
    );

    private static readonly IntentRule[] Rules =
    [
        new("GenerateCashFlowReport", "BookkeepingAgent",
            ["cash flow", "cashflow", "inflow", "outflow", "money came in", "money went out", "cash report", "cash position", "cash situation"]),

        new("CreateInvoice", "InvoiceAgent",
            ["create invoice", "make invoice", "new invoice", "bill a customer", "send invoice", "invoice for", "bill ", "invoice "],
            RequiresConfirmation: true),

        new("ReconcileAccount", "ReconciliationAgent",
            ["reconcile", "reconciliation", "bank statement", "match transactions", "match my bank"]),

        new("EstimateTaxLiability", "TaxAgent",
            ["tax liability", "tax estimate", "how much tax", "tax owe", "quarterly tax", "tax bill", "taxes"]),

        new("GenerateProfitLoss", "ReportingAgent",
            ["profit and loss", "profit & loss", "p&l", "income statement", "are we profitable",
             "how is the business", "net income", "ebit", "gross profit"]),

        new("AnalyzeProfitAnomaly", "ProfitAuditAgent",
            ["profit drop", "why did my profit", "expenses increase", "revenue decline",
             "profit decrease", "why did profit", "profit fell", "margin drop",
             "profit anomaly", "profit anomalies", "anomaly", "anomalies", "unusual profit"]),

        new("OptimizeTaxDeductions", "TaxOptimizationAgent",
            ["tax write-off", "write-off", "deductions can i claim", "tax deduction",
             "tax optimization", "reduce my taxes", "tax savings"]),

        new("ForecastCashRunway", "CashRunwayAgent",
            ["cash runway", "how long can", "sustain current spending", "runway",
             "burn rate", "how many months", "run out of cash"])
    ];

    public IntentResult Classify(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();

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
            return new IntentResult("Unknown", 0.1, new(), "None", false);

        // Confidence scales with keyword match ratio, capped at 0.99
        double confidence = Math.Min(0.70 + best.Score * 0.29, 0.99);

        var entities = ExtractEntities(lower, best.Rule.Intent);

        return new IntentResult(
            best.Rule.Intent,
            confidence,
            entities,
            best.Rule.AgentId,
            best.Rule.RequiresConfirmation
        );
    }

    public IReadOnlyList<IntentResult> ClassifyAll(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();

        var matches = Rules
            .Select(rule => new
            {
                Rule  = rule,
                Score = (double)rule.Keywords.Count(kw => lower.Contains(kw)) / rule.Keywords.Length
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => new IntentResult(
                x.Rule.Intent,
                Math.Min(0.70 + x.Score * 0.29, 0.99),
                ExtractEntities(lower, x.Rule.Intent),
                x.Rule.AgentId,
                x.Rule.RequiresConfirmation))
            .ToList();

        return matches.Count > 0
            ? matches
            : [new IntentResult("Unknown", 0.1, new(), "None", false)];
    }

    private static Dictionary<string, string> ExtractEntities(string lower, string intent) =>
        intent switch
        {
            "GenerateCashFlowReport" => new()
            {
                ["period"] = lower.Contains("last month")    ? "last_30_days"
                           : lower.Contains("this month")    ? "current_month"
                           : lower.Contains("ytd")           ? "YTD"
                           : "last_30_days"
            },
            "EstimateTaxLiability" => new()
            {
                ["year"]       = DateTime.UtcNow.Year.ToString(),
                ["entityType"] = lower.Contains("llc")    ? "LLC"
                               : lower.Contains("s-corp") ? "S-Corp"
                               : "LLC"
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
                ["period"] = lower.Contains("ytd")       ? "YTD"
                           : lower.Contains("last year") ? "last_year"
                           : "YTD"
            },
            _ => new()
        };
}
