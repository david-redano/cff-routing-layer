// Agents/StubAgents.cs
namespace CffRoutingLayerDemo.Agents;

using CffRoutingLayerDemo.Core;

/// <summary>
/// Lightweight stub agents used by the rule-based routing path and tests.
/// Each builds a minimal ExecutionPlan without hitting any data store.
/// </summary>

public sealed class BookkeepingAgent : IAgent
{
    public string AgentId => "BookkeepingAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context) =>
        new($"plan-cf-{context.RequestId[..8]}", intent.Intent,
        [
            new(1, "FetchTransactions",  new() { ["period"] = intent.Entities.GetValueOrDefault("period", "last_30_days") }, []),
            new(2, "SumByCategory",      new() { ["transactions"] = "$step1" }, [1]),
            new(3, "ComputeNetCashFlow", new() { ["classified"]   = "$step2" }, [2]),
            new(4, "RenderSummary",      new() { ["net"]          = "$step3" }, [3]),
        ]);
}

public sealed class InvoiceAgent : IAgent
{
    public string AgentId => "InvoiceAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context) =>
        intent.Intent == "ListInvoices"
            ? new($"plan-inv-list-{context.RequestId[..8]}", intent.Intent,
              [
                  new(1, "FetchInvoices",  new() { ["status"] = "all" }, []),
                  new(2, "RenderList",     new() { ["invoices"] = "$step1" }, [1]),
              ])
            : new($"plan-inv-{context.RequestId[..8]}", intent.Intent,
              [
                  new(1, "LookupCustomer",    new() { ["name"] = intent.Entities.GetValueOrDefault("customer", "Customer") }, []),
                  new(2, "CalculateTotals",   new() { ["customer"] = "$step1" }, [1]),
                  new(3, "GenerateInvoiceDoc",new() { ["totals"]   = "$step2" }, [2]),
              ]);
}

public sealed class ReconciliationAgent : IAgent
{
    public string AgentId => "ReconciliationAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context) =>
        new($"plan-rec-{context.RequestId[..8]}", intent.Intent,
        [
            new(1, "LoadBankStatement",    new() { ["period"] = intent.Entities.GetValueOrDefault("period", "this month") }, []),
            new(2, "MatchTransactions",    new() { ["bank"] = "$step1" }, [1]),
            new(3, "GenerateReconcReport", new() { ["matches"] = "$step2" }, [2]),
        ]);
}

public sealed class TaxAgent : IAgent
{
    public string AgentId => "TaxAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context) =>
        intent.Intent == "OptimizeTaxDeductions"
            ? new($"plan-tax-opt-{context.RequestId[..8]}", intent.Intent,
              [
                  new(1, "FetchExpenses",     new() { ["year"] = intent.Entities.GetValueOrDefault("year", DateTime.UtcNow.Year.ToString()) }, []),
                  new(2, "IdentifyWriteOffs", new() { ["expenses"] = "$step1" }, [1]),
                  new(3, "RenderSuggestions", new() { ["writeoffs"] = "$step2" }, [2]),
              ])
            : new($"plan-tax-{context.RequestId[..8]}", intent.Intent,
              [
                  new(1, "FetchYTDRevenue",     new() { ["year"] = intent.Entities.GetValueOrDefault("year", DateTime.UtcNow.Year.ToString()) }, []),
                  new(2, "ComputeTaxableIncome",new() { ["revenue"] = "$step1" }, [1]),
                  new(3, "ApplyTaxBrackets",    new() { ["income"]  = "$step2" }, [2]),
              ]);
}

public sealed class ReportingAgent : IAgent
{
    public string AgentId => "ReportingAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context) =>
        new($"plan-pnl-{context.RequestId[..8]}", intent.Intent,
        [
            new(1, "FetchRevenue",       new() { ["period"] = intent.Entities.GetValueOrDefault("period", "YTD") }, []),
            new(2, "FetchExpenses",      new() { ["period"] = "$step1.period" }, [1]),
            new(3, "ComputeGrossProfit", new() { ["revenue"] = "$step1", ["expenses"] = "$step2" }, [1, 2]),
            new(4, "RenderPnL",          new() { ["gross"] = "$step3" }, [3]),
        ]);
}

public sealed class AuditAgent : IAgent
{
    public string AgentId => "AuditAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context) =>
        new($"plan-audit-{context.RequestId[..8]}", intent.Intent,
        [
            new(1, "FetchProfitSeries", new() { ["period"] = intent.Entities.GetValueOrDefault("period", "this month") }, []),
            new(2, "ComputeZScore",     new() { ["series"] = "$step1" }, [1]),
            new(3, "FlagAnomalies",     new() { ["zscores"] = "$step2" }, [2]),
        ]);
}

public sealed class ForecastAgent : IAgent
{
    public string AgentId => "ForecastAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context) =>
        new($"plan-fcast-{context.RequestId[..8]}", intent.Intent,
        [
            new(1, "FetchCurrentBalance", new() { ["period"] = "current" }, []),
            new(2, "FetchBurnRate",       new() { ["period"] = "last_90_days" }, [1]),
            new(3, "ComputeRunway",       new() { ["balance"] = "$step1", ["burn"] = "$step2" }, [1, 2]),
        ]);
}
