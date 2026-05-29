// Bedrock/RagSummarizer.cs
namespace CffRoutingLayerDemo.Bedrock;

using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.CompanyData;
using CffRoutingLayerDemo.Config;

/// <summary>
/// RAG report generator: takes structured financial data retrieved from
/// <see cref="CompanyDataStore"/> and passes it to Claude Haiku as context
/// to produce a grounded natural-language analysis.
///
/// This is the "Augment → Generate" half of the RAG pipeline:
///   Retrieve (CompanyDataStore) → Augment (prompt + data) → Generate (Claude).
/// </summary>
public sealed class RagSummarizer : IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;

    private const string SystemPrompt = """
        You are a concise financial analyst assistant.
        You will receive structured transaction data for a company and a specific question.
        Provide a clear, factual analysis in 3-5 sentences.
        Use specific dollar amounts and dates from the data.
        Do NOT fabricate numbers that are not in the provided data.
        Do NOT use markdown headers or bullet lists — write in plain prose.
        """;

    public RagSummarizer(AppConfig config)
    {
        var awsConfig = new AmazonBedrockRuntimeConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(config.AwsRegion) };
        _client  = new AmazonBedrockRuntimeClient(awsConfig);
        _modelId = config.BedrockRewriterModelId;
    }

    /// <summary>
    /// Generate a narrated cash flow report from retrieved transaction data.
    /// </summary>
    public Task<string> SummarizeCashFlowAsync(
        IReadOnlyList<FinancialRecord> records,
        string period,
        string companyId,
        CancellationToken ct = default)
    {
        var income  = records.Where(r => r.Type == RecordType.Income).ToList();
        var expense = records.Where(r => r.Type == RecordType.Expense).ToList();
        var netFlow = income.Sum(r => r.Amount) - expense.Sum(r => r.Amount);

        var context = $"""
            Company: {companyId}
            Period: {period}
            Total Inflows: {income.Sum(r => r.Amount):C} ({income.Count} transactions)
            Total Outflows: {expense.Sum(r => r.Amount):C} ({expense.Count} transactions)
            Net Cash Flow: {netFlow:+$#,##0.00;-$#,##0.00}

            Top income sources:
            {string.Join("\n", income.OrderByDescending(r => r.Amount).Take(3).Select(r => $"  {r.Date:MMM d}: {r.Counterparty} +{r.Amount:C} ({r.Category})"))}

            Largest expenses:
            {string.Join("\n", expense.OrderByDescending(r => r.Amount).Take(3).Select(r => $"  {r.Date:MMM d}: {r.Counterparty} -{r.Amount:C} ({r.Category})"))}
            """;

        return CallClaudeAsync($"Provide a cash flow analysis for {companyId} for {period}.", context, ct);
    }

    /// <summary>Generate a narrated P&amp;L summary from retrieved transaction data.</summary>
    public Task<string> SummarizeProfitLossAsync(
        IReadOnlyList<FinancialRecord> records,
        string period,
        string companyId,
        CancellationToken ct = default)
    {
        var revenue  = records.Where(r => r.Type == RecordType.Income).Sum(r => r.Amount);
        var expenses = records.Where(r => r.Type == RecordType.Expense).Sum(r => r.Amount);
        var cogs     = revenue * 0.20m; // simplified: 20% of revenue as COGS
        var gross    = revenue - cogs;
        var netIncome = gross - expenses;

        var byCategory = records
            .Where(r => r.Type == RecordType.Expense)
            .GroupBy(r => r.Category)
            .Select(g => $"  {g.Key}: {g.Sum(r => r.Amount):C}")
            .OrderByDescending(s => s);

        var context = $"""
            Company: {companyId}
            Period: {period}
            Revenue: {revenue:C}
            COGS (est. 20%): {cogs:C}
            Gross Profit: {gross:C} ({(gross / revenue * 100):F1}% margin)
            Total Operating Expenses: {expenses:C}
            Net Income: {netIncome:+$#,##0.00;-$#,##0.00}

            Expense breakdown:
            {string.Join("\n", byCategory)}
            """;

        return CallClaudeAsync($"Provide a profit and loss analysis for {companyId} for {period}.", context, ct);
    }

    /// <summary>Generate a narrated cash runway forecast.</summary>
    public Task<string> SummarizeRunwayAsync(
        decimal currentBalance,
        IReadOnlyList<FinancialRecord> recentRecords,
        string companyId,
        CancellationToken ct = default)
    {
        // Group expenses by month to compute monthly burn rate
        var monthlyBurn = recentRecords
            .Where(r => r.Type == RecordType.Expense)
            .GroupBy(r => new { r.Date.Year, r.Date.Month })
            .Select(g => g.Sum(r => r.Amount))
            .ToList();

        var avgBurn   = monthlyBurn.Count > 0 ? monthlyBurn.Average() : 0m;
        var runway    = avgBurn > 0 ? currentBalance / avgBurn : 0m;

        var context = $"""
            Company: {companyId}
            Current Cash Balance: {currentBalance:C}
            Monthly Burn Rate (last {monthlyBurn.Count} months avg): {avgBurn:C}
            Projected Runway: {runway:F1} months

            Monthly expense breakdown:
            {string.Join("\n", recentRecords
                .Where(r => r.Type == RecordType.Expense)
                .GroupBy(r => new { r.Date.Year, r.Date.Month })
                .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
                .Select(g => $"  {new DateTime(g.Key.Year, g.Key.Month, 1):MMM yyyy}: {g.Sum(r => r.Amount):C}"))}
            """;

        return CallClaudeAsync($"Provide a cash runway analysis for {companyId}.", context, ct);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task<string> CallClaudeAsync(
        string question,
        string dataContext,
        CancellationToken ct)
    {
        try
        {
            var request = new ConverseRequest
            {
                ModelId = _modelId,
                System  = [new SystemContentBlock { Text = SystemPrompt }],
                Messages =
                [
                    new Message
                    {
                        Role    = ConversationRole.User,
                        Content =
                        [
                            new ContentBlock
                            {
                                Text = $"Financial data:\n\n{dataContext}\n\nQuestion: {question}"
                            }
                        ]
                    }
                ],
                InferenceConfig = new InferenceConfiguration
                {
                    MaxTokens   = 400,
                    Temperature = 0.3f
                }
            };

            var response = await _client.ConverseAsync(request, ct);
            return response.Output.Message.Content[0].Text.Trim();
        }
        catch (Exception ex)
        {
            // Degrade gracefully — pipeline continues with formatted fallback
            return $"[RAG generation unavailable: {ex.GetType().Name}]";
        }
    }

    public void Dispose() => _client.Dispose();
}
