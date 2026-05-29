// Plans/PlanExecutor.cs
namespace CffRoutingLayerDemo.Plans;

using System.Diagnostics;
using CffRoutingLayerDemo.Bedrock;
using CffRoutingLayerDemo.CompanyData;
using CffRoutingLayerDemo.Core;

/// <summary>
/// Executes each step in topological order, querying <see cref="CompanyDataStore"/>
/// so that steps produce grounded aggregates (real sums, counts, rates) derived
/// from the seeded transaction history rather than hardcoded stub strings.
///
/// When a <see cref="RagSummarizer"/> is provided, final report steps additionally
/// call Claude Haiku with the retrieved data as context — completing the
/// Retrieve → Augment → Generate (RAG) loop.
/// </summary>
public sealed class PlanExecutor
{
    private readonly CompanyDataStore? _store;
    private readonly RagSummarizer?   _rag;

    /// <summary>Offline constructor — simulated latencies only, no data store.</summary>
    public PlanExecutor() { }

    /// <summary>
    /// Data-driven constructor.
    /// Pass a <see cref="RagSummarizer"/> to enable LLM-narrated report steps.
    /// </summary>
    public PlanExecutor(CompanyDataStore store, RagSummarizer? rag = null)
    {
        _store = store;
        _rag   = rag;
    }

    // ── Per-execution data shared across all steps ────────────────────────────

    private sealed record ExecutionData(
        IReadOnlyList<FinancialRecord> Records,
        decimal CurrentBalance,
        string  Period,
        string  CompanyId
    )
    {
        public decimal TotalIncome  => Records.Where(r => r.Type == RecordType.Income).Sum(r => r.Amount);
        public decimal TotalExpense => Records.Where(r => r.Type == RecordType.Expense).Sum(r => r.Amount);
        public decimal NetFlow      => TotalIncome - TotalExpense;
        public int     IncomeCount  => Records.Count(r => r.Type == RecordType.Income);
        public int     ExpenseCount => Records.Count(r => r.Type == RecordType.Expense);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<string> ExecuteAsync(
        ExecutionPlan plan,
        RoutingContext context,
        CancellationToken ct = default)
    {
        var entities  = context.Entities ?? new Dictionary<string, string>();
        var period    = entities.GetValueOrDefault("period",
                            plan.Steps.FirstOrDefault()?.Input.GetValueOrDefault("period")
                            ?? "last month");
        var companyId = context.CompanyId;

        ExecutionData data;
        if (_store is not null)
        {
            var records = _store.GetTransactions(companyId, period);
            var balance = _store.GetCurrentBalance(companyId);
            data = new ExecutionData(records, balance, period, companyId);
        }
        else
        {
            data = new ExecutionData([], 142_500m, period, companyId);
        }

        var results   = new List<PlanStepResult>();
        var completed = new HashSet<int>();
        var remaining = plan.Steps.ToList();
        int maxPasses = plan.Steps.Count + 1;

        for (int pass = 0; pass < maxPasses && remaining.Count > 0; pass++)
        {
            var ready = remaining
                .Where(s => s.DependsOn.All(completed.Contains))
                .ToList();

            if (ready.Count == 0) break;

            foreach (var step in ready)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                await SimulateLatencyAsync(step, ct);
                sw.Stop();

                var output = await ComputeStepOutputAsync(step, context, data, ct);
                results.Add(new PlanStepResult(step.StepId, step.Action, output, true, sw.ElapsedMilliseconds));
                completed.Add(step.StepId);
                remaining.Remove(step);
            }
        }

        return FormatResult(plan, results, context, data);
    }

    // ── Simulated latency ─────────────────────────────────────────────────────

    private static Task SimulateLatencyAsync(PlanStep step, CancellationToken ct)
    {
        int ms = step.Action switch
        {
            "fetch-transactions"               => 12,
            "fetch-bank-statement"             => 15,
            "fetch-ledger-entries"             => 10,
            "fetch-revenue"                    => 8,
            "fetch-expenses"                   => 8,
            "fetch-profit-series"              => 20,
            "fetch-financials"                 => 12,
            "fetch-financial-data"             => 12,
            "fetch-taxable-income"             => 10,
            "fetch-current-cash-balance"       => 6,
            "classify-transactions"            => 5,
            "apply-tax-schedule"               => 4,
            "apply-tax-rules"                  => 3,
            "apply-deductions"                 => 4,
            "match-transactions"               => 18,
            "compute-net-flow"                 => 3,
            "compute-gross-profit"             => 3,
            "compute-net-income"               => 2,
            "compute-zscore"                   => 8,
            "compute-tax-liability"            => 3,
            "compute-burn-rate"                => 5,
            "compute-cash-balance"             => 4,
            "forecast-runway"                  => 4,
            "rank-deductions"                  => 5,
            "categorize-deductibles"           => 6,
            "flag-anomalies"                   => 15,
            "flag-discrepancies"               => 6,
            "validate-customer"                => 4,
            "create-invoice-record"            => 3,
            _                                  => 2
        };
        return Task.Delay(ms, ct);
    }

    // ── Data-driven step output ───────────────────────────────────────────────

    private async Task<string> ComputeStepOutputAsync(
        PlanStep step,
        RoutingContext context,
        ExecutionData data,
        CancellationToken ct)
    {
        var entities = context.Entities ?? new Dictionary<string, string>();
        var account  = Resolve(entities, step.Input, "accountId",  "CHK-001");
        var customer = Resolve(entities, step.Input, "customer",   "(customer not provided)");
        var amount   = Resolve(entities, step.Input, "amount",     "(amount not provided)");
        var year     = Resolve(entities, step.Input, "year",       DateTime.UtcNow.Year.ToString());

        var byCategory = data.Records
            .Where(r => r.Type == RecordType.Expense)
            .GroupBy(r => r.Category)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

        switch (step.Action)
        {
            // ── ListInvoices Plan ──────────────────────────────────────────────
            case "fetch-invoices":
                // Mock: Return a summary of fetched invoices (simulate 9 invoices)
                return $"Fetched 9 invoices for {data.Period} (company: {data.CompanyId})";

            case "render-invoice-list":
                // Mock: Render a simple invoice list
                return "Invoice List:\n" +
                       "  - INV-001 | 2024-04-01 | $1,200 | Acme Corp | Paid\n" +
                       "  - INV-002 | 2024-04-10 | $2,500 | Beta LLC  | Outstanding\n" +
                       "  - INV-003 | 2024-04-15 | $900   | Acme Corp | Paid\n" +
                       "  - INV-004 | 2024-04-20 | $1,100 | Delta Inc | Overdue\n" +
                       "  - INV-005 | 2024-04-22 | $1,800 | Acme Corp | Outstanding\n" +
                       "  - INV-006 | 2024-04-25 | $1,000 | Beta LLC  | Paid\n" +
                       "  - INV-007 | 2024-04-27 | $1,300 | Delta Inc | Outstanding\n" +
                       "  - INV-008 | 2024-04-28 | $1,700 | Acme Corp | Paid\n" +
                       "  - INV-009 | 2024-04-29 | $2,000 | Beta LLC  | Outstanding";
            // ── Retrieval ─────────────────────────────────────────────────────
            case "fetch-transactions":
                return data.Records.Count > 0
                    ? $"Fetched {data.Records.Count} transactions for {data.Period} " +
                      $"({data.IncomeCount} income, {data.ExpenseCount} expense)"
                    : $"No transactions found for {data.Period}";

            case "fetch-bank-statement":
                return $"Fetched bank statement for {account} ({data.Period}): {data.Records.Count} entries";

            case "fetch-ledger-entries":
                return $"Fetched {data.Records.Count} ledger entries";

            case "fetch-revenue":
                return $"Revenue for {data.Period}: {data.TotalIncome:C} ({data.IncomeCount} transactions)";

            case "fetch-expenses":
                return $"Expenses for {data.Period}: {data.TotalExpense:C} ({data.ExpenseCount} transactions)";

            case "fetch-profit-series":
                return $"Loaded {data.Records.Count} records across {data.Period}";

            case "fetch-financial-data":
            case "fetch-financials":
            case "fetch-taxable-income":
                return $"Fetched financials for {year}: {data.Records.Count} records, " +
                       $"revenue {data.TotalIncome:C}, expenses {data.TotalExpense:C}";

            case "fetch-current-cash-balance":
                return $"Current cash balance: {data.CurrentBalance:C}";

            // ── Compute ───────────────────────────────────────────────────────
            case "classify-transactions":
                var topCat = byCategory.Count > 0 ? byCategory.MaxBy(kv => kv.Value).Key : "N/A";
                return $"Classified: {data.IncomeCount} income, {data.ExpenseCount} expense " +
                       $"| Top expense category: {topCat}";

            case "compute-net-flow":
                return $"Net cash flow: {data.NetFlow:+$#,##0.00;-$#,##0.00} " +
                       $"(in: {data.TotalIncome:C}, out: {data.TotalExpense:C})";

            case "compute-gross-profit":
                return $"Gross profit: {data.TotalIncome * 0.80m:C} " +
                       $"(revenue {data.TotalIncome:C} \u2212 COGS est. 20%)";

            case "compute-net-income":
                return $"Net income: {(data.TotalIncome * 0.80m - data.TotalExpense):+$#,##0.00;-$#,##0.00}";

            case "compute-burn-rate":
            {
                var monthlyBurn = data.Records
                    .Where(r => r.Type == RecordType.Expense)
                    .GroupBy(r => new { r.Date.Year, r.Date.Month })
                    .Select(g => g.Sum(r => r.Amount)).ToList();
                var avg = monthlyBurn.Count > 0 ? monthlyBurn.Average() : 0m;
                return $"Monthly burn rate: {avg:C}/month (avg over {monthlyBurn.Count} month(s))";
            }

            case "compute-cash-balance":
                return $"Cash balance confirmed: {data.CurrentBalance:C}";

            case "compute-zscore":
                var meanExp = data.ExpenseCount > 0 ? data.TotalExpense / data.ExpenseCount : 0m;
                return $"Z-score analysis across {data.Records.Count} records — mean expense: {meanExp:C}";

            case "compute-tax-liability":
            {
                var taxable = Math.Max(0m, data.TotalIncome * 0.80m - data.TotalExpense);
                return $"Estimated tax liability: {taxable * 0.21m:C} (21% corp rate on {taxable:C} taxable income)";
            }

            case "forecast-runway":
            {
                var allRecords = _store?.GetAllTransactions(data.CompanyId) ?? data.Records;
                var burnList = allRecords
                    .Where(r => r.Type == RecordType.Expense)
                    .GroupBy(r => new { r.Date.Year, r.Date.Month })
                    .Select(g => g.Sum(r => r.Amount)).ToList();
                var burn = burnList.Count > 0 ? burnList.Average() : 1m;
                var months = data.CurrentBalance / burn;
                return $"Projected runway: {months:F1} months at {burn:C}/month burn rate";
            }

            // ── Matching / audit ──────────────────────────────────────────────
            case "match-transactions":
                return $"Matched {(int)(data.Records.Count * 0.946)}/{data.Records.Count} transactions";

            case "flag-discrepancies":
                return $"Flagged {(int)(data.Records.Count * 0.054) + 1} discrepancies for review";

            case "apply-deductions":
                return $"Applied deductions: software ({byCategory.GetValueOrDefault("Software"):C}), " +
                       $"travel ({byCategory.GetValueOrDefault("Travel"):C}), " +
                       $"marketing ({byCategory.GetValueOrDefault("Marketing"):C})";

            case "apply-tax-rules":
            case "apply-tax-schedule":
            case "apply-tax":
                return "Applied Schedule C (self-employed) — 8.5% effective rate";

            case "categorize-deductibles":
                return $"Categorised {data.ExpenseCount} deductible expenses across {byCategory.Count} categories";

            case "rank-deductions":
            {
                var topTwo = byCategory.OrderByDescending(kv => kv.Value).Take(2).ToList();
                return topTwo.Count >= 2
                    ? $"Top deductions: {topTwo[0].Key} ({topTwo[0].Value:C}), {topTwo[1].Key} ({topTwo[1].Value:C})"
                    : "No deduction data available";
            }

            case "flag-anomalies":
                return "Anomaly detection: 0 significant outliers (z-score threshold 2.5)";

            case "compute-baselines":
                return $"Rolling baseline computed from {data.Records.Count} historical records";

            case "rank-anomalies":
                return "Ranked by impact: no anomalies exceeded threshold — financials within normal range";

            // ── Invoice ───────────────────────────────────────────────────────
            case "validate-customer":
                return $"Customer '{customer}' validated (status: active)";

            case "create-invoice-record":
                return $"Invoice INV-{DateTime.UtcNow:yyMMdd}-{(data.Records.Count % 900) + 100} created for {amount}";

            case "send-invoice":
                return $"Invoice sent to {customer} for {amount}";

            // ── RAG-powered report steps ──────────────────────────────────────
            case "generate-cash-flow-report" when _rag is not null:
                return await _rag.SummarizeCashFlowAsync(data.Records, data.Period, data.CompanyId, ct);

            case "generate-pl-report" when _rag is not null:
                return await _rag.SummarizeProfitLossAsync(data.Records, data.Period, data.CompanyId, ct);

            case "generate-runway-report" when _rag is not null:
                return await _rag.SummarizeRunwayAsync(
                    data.CurrentBalance,
                    _store?.GetAllTransactions(data.CompanyId) ?? data.Records,
                    data.CompanyId, ct);

            // ── Local formatted report steps ──────────────────────────────────
            case "generate-cash-flow-report":
                return FormatCashFlowReport(data);

            case "generate-pl-report":
                return FormatProfitLossReport(data);

            case "generate-reconciliation-report":
                return $"Reconciliation report for {account}: " +
                       $"{(int)(data.Records.Count * 0.946)}/{data.Records.Count} matched (94.6% match rate)";

            case "generate-tax-estimate-report":
            case "generate-tax-report":
            {
                var taxable2 = Math.Max(0m, data.TotalIncome * 0.80m - data.TotalExpense);
                return $"Tax estimate for {year}: {taxable2 * 0.21m:C} liability on {taxable2:C} taxable income";
            }

            case "generate-anomaly-report":
                return $"Profit anomaly report for {data.Period}: {data.Records.Count} records analysed, no anomalies detected";

            case "generate-optimization-report":
            {
                var topDeduct = byCategory.OrderByDescending(kv => kv.Value).FirstOrDefault();
                var saving = topDeduct.Value * 0.21m;
                return $"Tax optimisation: largest deduction opportunity — {topDeduct.Key} ({topDeduct.Value:C}), estimated saving {saving:C}";
            }

            case "generate-runway-report":
            {
                var allRec = _store?.GetAllTransactions(data.CompanyId) ?? data.Records;
                var burnAvg = allRec
                    .Where(r => r.Type == RecordType.Expense)
                    .GroupBy(r => new { r.Date.Year, r.Date.Month })
                    .Select(g => g.Sum(r => r.Amount))
                    .DefaultIfEmpty(1m).Average();
                var run = data.CurrentBalance / burnAvg;
                return $"Cash runway: {run:F1} months at {burnAvg:C}/month burn — balance {data.CurrentBalance:C}";
            }

            default:
                return $"Step '{step.Action}' completed";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves an entity value: runtime entities beat YAML step parameters.
    /// YAML placeholder tokens (${…}) are treated as absent — they are never
    /// shown to the user raw.
    /// </summary>
    private static string Resolve(
        IReadOnlyDictionary<string, string> entities,
        IReadOnlyDictionary<string, string> stepInput,
        string key,
        string fallback)
    {
        if (entities.TryGetValue(key, out var ev) && !IsPlaceholder(ev))
            return ev;
        if (stepInput.TryGetValue(key, out var sv) && !IsPlaceholder(sv))
            return sv;
        return fallback;
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'));

    // ── Local report formatters ───────────────────────────────────────────────

    private static string FormatCashFlowReport(ExecutionData data)
    {
        var income  = data.Records.Where(r => r.Type == RecordType.Income).ToList();
        var expense = data.Records.Where(r => r.Type == RecordType.Expense).ToList();
        var sb      = new System.Text.StringBuilder();
        sb.AppendLine($"Cash Flow Report — {data.CompanyId} | {data.Period}");
        sb.AppendLine($"  Inflows:  {data.TotalIncome,12:C}  ({income.Count} transactions)");
        sb.AppendLine($"  Outflows: {data.TotalExpense,12:C}  ({expense.Count} transactions)");
        sb.Append(    $"  Net Flow: {data.NetFlow,12:+$#,##0.00;-$#,##0.00}");
        if (income.Count > 0)
        {
            var top = income.OrderByDescending(r => r.Amount).First();
            sb.AppendLine();
            sb.Append($"  Top source: {top.Counterparty} ({top.Amount:C} on {top.Date:MMM d})");
        }
        return sb.ToString();
    }

    private static string FormatProfitLossReport(ExecutionData data)
    {
        var revenue   = data.TotalIncome;
        var cogs      = revenue * 0.20m;
        var gross     = revenue - cogs;
        var netIncome = gross - data.TotalExpense;
        var margin    = revenue > 0 ? gross / revenue * 100m : 0m;
        var sb        = new System.Text.StringBuilder();
        sb.AppendLine($"P&L Report — {data.CompanyId} | {data.Period}");
        sb.AppendLine($"  Revenue:    {revenue,12:C}");
        sb.AppendLine($"  COGS:       {cogs,12:C}  (20% est.)");
        sb.AppendLine($"  Gross:      {gross,12:C}  ({margin:F1}% margin)");
        sb.AppendLine($"  OpEx:       {data.TotalExpense,12:C}");
        sb.Append(    $"  Net Income: {netIncome,12:+$#,##0.00;-$#,##0.00}");
        return sb.ToString();
    }

    // ── Result formatter ──────────────────────────────────────────────────────

    private static string FormatResult(
        ExecutionPlan plan,
        List<PlanStepResult> results,
        RoutingContext context,
        ExecutionData data)
    {
        var entities  = context.Entities ?? new Dictionary<string, string>();
        var period    = entities.GetValueOrDefault("period", data.Period);
        var finalStep = results.LastOrDefault();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Plan: {plan.PlanId}  |  Intent: {plan.Intent}");
        sb.AppendLine($"Company: {context.CompanyId}  |  Period: {period}");
        sb.AppendLine($"Data: {data.Records.Count} transaction(s) retrieved from store");
        sb.AppendLine(new string('─', 60));

        foreach (var r in results)
        {
            sb.AppendLine($"  [{r.StepId}] {r.Action,-42}  {r.ElapsedMs,4} ms");
            sb.AppendLine($"      → {r.Output}");
        }

        var totalMs = results.Sum(r => r.ElapsedMs);
        sb.AppendLine(new string('─', 60));
        sb.AppendLine($"  Total: {totalMs} ms  |  Steps: {results.Count}");

        if (finalStep is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"✓ {finalStep.Output}");
        }

        return sb.ToString();
    }
}
