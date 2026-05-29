// Plans/PlanExecutor.cs
namespace CffRoutingLayerDemo.Plans;

using System.Diagnostics;
using CffRoutingLayerDemo.Core;

/// <summary>
/// Simulated plan executor for demo purposes.
/// Executes each step in topological order (respecting DependsOn) and
/// returns a human-readable report string.
///
/// In production, each step would dispatch to a real agent micro-service,
/// function call, or tool invocation.
/// </summary>
public sealed class PlanExecutor
{
    /// <summary>
    /// Execute all steps in the plan and return a formatted result string.
    /// </summary>
    public async Task<string> ExecuteAsync(
        ExecutionPlan plan,
        RoutingContext context,
        CancellationToken ct = default)
    {
        var results = new List<PlanStepResult>();
        var completed = new HashSet<int>();

        // Simple topological execution: keep passing over steps until all are done
        var remaining = plan.Steps.ToList();
        int maxPasses = plan.Steps.Count + 1; // guard against cycles

        for (int pass = 0; pass < maxPasses && remaining.Count > 0; pass++)
        {
            var ready = remaining
                .Where(s => s.DependsOn.All(completed.Contains))
                .ToList();

            if (ready.Count == 0) break; // cycle guard

            foreach (var step in ready)
            {
                ct.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                await SimulateStepAsync(step, context, ct);
                sw.Stop();

                var output = SimulateStepOutput(step, plan.Intent, context);
                results.Add(new PlanStepResult(step.StepId, step.Action, output, true, sw.ElapsedMilliseconds));
                completed.Add(step.StepId);
                remaining.Remove(step);
            }
        }

        return FormatResult(plan, results, context);
    }

    // ── Simulated step execution ──────────────────────────────────────────────

    private static Task SimulateStepAsync(
        PlanStep step,
        RoutingContext context,
        CancellationToken ct)
    {
        // Simulate variable latency (deterministic for benchmarks: seeded by step)
        int ms = step.Action switch
        {
            "fetch-transactions"           => 12,
            "fetch-bank-statement"         => 15,
            "fetch-ledger-entries"         => 10,
            "fetch-revenue"                => 8,
            "fetch-expenses"               => 8,
            "fetch-historical-profit"      => 20,
            "fetch-financials"             => 12,
            "fetch-financial-data"         => 12,
            "fetch-current-cash-balance"   => 6,
            "classify-transactions"        => 5,
            "apply-tax-schedule"           => 4,
            "apply-tax-rules"              => 3,
            "match-transactions"           => 18,
            "compute-net-flow"             => 3,
            "compute-gross-profit"         => 3,
            "compute-net-income"           => 2,
            "compute-baselines"            => 8,
            "compute-liability"            => 3,
            "compute-monthly-burn-rate"    => 5,
            "detect-anomalies"             => 15,
            "rank-anomalies"               => 4,
            "rank-deductions-by-impact"    => 5,
            "identify-deduction-opportunities" => 10,
            "project-runway-months"        => 4,
            "flag-discrepancies"           => 6,
            "validate-customer"            => 4,
            "create-invoice-draft"         => 3,
            _                              => 2
        };
        return Task.Delay(ms, ct);
    }

    private static string SimulateStepOutput(
        PlanStep step,
        string intent,
        RoutingContext context)
    {
        var entities = context.Entities ?? new Dictionary<string, string>();
        var period   = entities.GetValueOrDefault("period",   "current period");
        var account  = entities.GetValueOrDefault("accountId","[account]");
        var customer = entities.GetValueOrDefault("customer", "[customer]");
        var amount   = entities.GetValueOrDefault("amount",   "$0.00");
        var year     = entities.GetValueOrDefault("year",     DateTime.UtcNow.Year.ToString());

        return step.Action switch
        {
            "fetch-transactions"              => $"Fetched 124 transactions for {period}",
            "classify-transactions"           => "Classified: 87 operating, 23 investing, 14 financing",
            "compute-net-flow"                => "Net cash flow: +$34,210.00",
            "generate-cash-flow-report"       => $"Cash flow report generated for {period}",
            "validate-customer"               => $"Customer '{customer}' validated (status: active)",
            "create-invoice-draft"            => $"Invoice draft INV-{DateTime.UtcNow:yyMMdd}-001 created for {amount}",
            "apply-tax-rules"                 => "Tax rules applied: 8.5% sales tax",
            "issue-invoice"                   => $"Invoice issued to {customer} for {amount}",
            "fetch-bank-statement"            => $"Fetched bank statement for {account} ({period}): 203 entries",
            "fetch-ledger-entries"            => "Fetched 198 ledger entries",
            "match-transactions"              => "Matched 192/203 transactions",
            "flag-discrepancies"              => "Flagged 11 discrepancies for review",
            "generate-reconciliation-report"  => $"Reconciliation report for {account}: 94.6% match rate",
            "fetch-financial-data"            => $"Fetched financial data for {year}",
            "apply-tax-schedule"              => "Applied Schedule C (self-employed)",
            "compute-liability"               => "Estimated tax liability: $18,430.00",
            "generate-tax-report"             => $"Tax liability report for {year}: $18,430.00",
            "fetch-revenue"                   => "Revenue: $245,000.00",
            "fetch-expenses"                  => "Expenses: $178,320.00",
            "compute-gross-profit"            => "Gross profit: $66,680.00",
            "compute-net-income"              => "Net income: $51,200.00",
            "generate-pl-report"              => $"P&L report for {period}: Revenue $245K, Net Income $51.2K",
            "fetch-historical-profit"         => "Loaded 24 months of profit history",
            "compute-baselines"               => "Computed rolling 6-month profit baselines",
            "detect-anomalies"                => "Detected 3 anomalies (z-score > 2.0)",
            "rank-anomalies"                  => "Ranked by impact: March spike (+$22K), July dip (-$14K)",
            "generate-audit-report"           => "Profit anomaly audit complete — 3 anomalies flagged",
            "fetch-financials"                => "Fetched full financials for current fiscal year",
            "identify-deduction-opportunities"=> "Identified 7 deduction opportunities (home office, vehicle, software)",
            "rank-deductions-by-impact"       => "Top deduction: home office ($8,400), software ($3,200)",
            "generate-optimization-report"    => "Tax optimization: potential savings of $12,650",
            "fetch-current-cash-balance"      => "Current cash balance: $142,500.00",
            "compute-monthly-burn-rate"       => "Monthly burn rate: $28,300.00",
            "project-runway-months"           => "Projected runway: 5.0 months",
            "generate-runway-report"          => "Cash runway forecast: 5 months at current burn rate",
            _                                 => $"Step '{step.Action}' completed"
        };
    }

    // ── Report formatting ─────────────────────────────────────────────────────

    private static string FormatResult(
        ExecutionPlan plan,
        List<PlanStepResult> results,
        RoutingContext context)
    {
        var entities = context.Entities ?? new Dictionary<string, string>();
        var period   = entities.GetValueOrDefault("period",   "current period");
        var finalStep = results.LastOrDefault();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Plan: {plan.PlanId}  |  Intent: {plan.Intent}");
        sb.AppendLine($"Company: {context.CompanyId}  |  Period: {period}");
        sb.AppendLine(new string('─', 60));

        foreach (var r in results)
        {
            sb.AppendLine($"  [{r.StepId}] {r.Action,-38}  {r.ElapsedMs,4} ms");
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
