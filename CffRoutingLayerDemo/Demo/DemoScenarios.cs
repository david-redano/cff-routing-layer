// Demo/DemoScenarios.cs
namespace CffRoutingLayerDemo.Demo;

/// <summary>
/// Pre-set demo scenarios exercising all 8 intents + cache hits + coreference.
/// Each scenario has a descriptive label and a list of turns.
/// Multi-turn scenarios show coreference resolution via conversation history.
/// </summary>
public static class DemoScenarios
{
    public sealed record Turn(string Label, string UserMessage);
    public sealed record Scenario(string Name, string Description, Turn[] Turns);

    public static readonly Scenario[] All =
    [
        new("Cash Flow Report",
            "Generate a cash flow report for last month",
            [new("Fresh request", "Generate a cash flow report for Acme Corp last month")]),

        new("Create Invoice",
            "Create a new invoice for a customer",
            [new("Fresh request", "Create an invoice for TechVentures LLC for $4,500 consulting")]),

        new("Reconcile Account",
            "Reconcile a bank account",
            [new("Fresh request", "Reconcile account CHK-001 for May")]),

        new("Tax Liability",
            "Estimate quarterly tax liability",
            [new("Fresh request", "Estimate tax liability for Q2 2024 for Acme Corp S-Corp")]),

        new("Profit & Loss",
            "Generate a P&L statement",
            [new("Fresh request", "Generate profit and loss report for FY2024")]),

        new("Profit Anomaly",
            "Analyse profit anomalies",
            [new("Fresh request", "Analyze profit anomaly for Acme Corp last 6 months")]),

        new("Tax Optimisation",
            "Optimise tax deductions",
            [new("Fresh request", "Optimize tax deductions for Acme Corp for 2024")]),

        new("Cash Runway",
            "Forecast cash runway",
            [new("Fresh request", "Forecast cash runway for Acme Corp")]),

        new("Cache Hit Demo",
            "Paraphrase of earlier cash flow request — should hit semantic cache",
            [new("Paraphrase (cache hit)", "Show me the cash flow summary for last month for Acme")]),

        new("Coreference — Account",
            "Coreference resolution across turns (requires LLM rewriter)",
            [
                new("Turn 1 — establish context",  "Reconcile account CHK-001 for May"),
                new("Turn 2 — coreference 'same'", "Now reconcile the same account for June"),
            ]),

        new("Coreference — Customer",
            "Customer coreference resolution across turns",
            [
                new("Turn 1 — establish context",  "Create an invoice for TechVentures LLC for $4,500"),
                new("Turn 2 — coreference 'them'", "Create another invoice for them for $2,200"),
            ]),

        new("Out-of-domain → Streaming",
            "Out-of-domain question handled by streaming fallback",
            [new("Streamed", "What's the best way to negotiate vendor contracts?")]),
    ];
}
