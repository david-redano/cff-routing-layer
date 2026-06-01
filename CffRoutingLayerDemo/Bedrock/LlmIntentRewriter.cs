// Bedrock/LlmIntentRewriter.cs
namespace CffRoutingLayerDemo.Bedrock;

using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Config;
using CffRoutingLayerDemo.Conversation;
using CffRoutingLayerDemo.Normalization;

/// <summary>
/// LLM-backed intent rewriter that uses a single lightweight Bedrock call
/// (Claude Haiku, max_tokens=200, temperature=0) to:
///   1. Replace PII tokens with typed placeholders (${customer}, ${amount}, …)
///   2. Normalise surface variants (P&amp;L → "profit and loss", etc.)
///   3. Resolve coreferences using <see cref="ConversationHistory"/>
///      ("same account" → actual account ID from a prior turn)
///
/// Falls back to returning the original text unchanged if the model returns
/// an unparseable response, so the pipeline always continues.
/// </summary>
public sealed class LlmIntentRewriter : IIntentRewriter, IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;

    private static readonly string SystemPrompt = """
        You are a concise intent normalizer for a financial accounting assistant.
        Given optional conversation context and a user message, return a JSON object ONLY — no prose.

        Rules:
        1. PII replacement (replace with placeholder, capture real value in entities):
           - Customer/company names after "for", "to", "from", "by" → ${customer}
           - Account IDs like CHK-001, SAV-9901 → ${accountId}
           - Dollar amounts like $1,234.56 → ${amount}
           - Calendar periods (this month, last 30 days, Q2 2024, January, YTD) → ${period}
           - 4-digit fiscal years (2024, 2025) → ${year}
           - Legal entity types (LLC, S-Corp, C-Corp, sole proprietor) → ${entityType}

        2. Surface normalisation (no entity capture — just rename):
           - P&L / pnl / profit & loss → "profit and loss"
           - A/R / accounts receivable → "accounts receivable"
           - A/P / accounts payable → "accounts payable"
           - recon / reconciliation → "reconcile"
           - bill / billing → "invoice"

        3. Coreference resolution using conversation context:
           - "same account" / "that account" → resolve to accountId from history
           - "that customer" / "them" → resolve to customer from history
           - "same period" / "last time" → resolve to period from history
           - "again" with no new entity → reuse most recent matching entity

        4. Period resolution using the runtime context block (second system message):
           - "today" / "now"            → current date
           - "this month" / "MTD"       → current month name + year
           - "last month"               → previous month name + year
           - "this quarter" / "current quarter" → current quarter label (e.g. Q2 2026)
           - "last quarter"             → previous quarter label
           - "this year" / "YTD"        → current fiscal year
           - "last year"                → previous fiscal year
           Always store the resolved human-readable label as the entity value.

        5. If nothing needs changing, return the original message as normalizedText.
        6. Omit any entity key where the value is empty or unknown.

                Response format (JSON only):
                {
                    "normalizedText": "...",
                    "entities": {
                        "customer": "",
                        "accountId": "",
                        "amount": "",
                        "period": "",
                        "year": "",
                        "entityType": ""
                    },
                    "capabilities": ["profit-loss", "reporting"] // array of keywords or features relevant to the user request
                }
        """;

    /// <summary>
    /// Builds a second system block injected at call time with live date/period context
    /// so the LLM can resolve relative references like "this month" or "last quarter".
    /// </summary>
    private static string BuildRuntimeContext()
    {
        var now          = DateTime.UtcNow;
        var currentQ     = (now.Month - 1) / 3 + 1;
        var prevMonth    = now.AddMonths(-1);
        var prevQ        = currentQ == 1 ? 4 : currentQ - 1;
        var prevQYear    = currentQ == 1 ? now.Year - 1 : now.Year;
        var fiscalYTDStart = new DateTime(now.Year, 1, 1);

        return $"""
            RUNTIME CONTEXT (use these values when resolving relative date/period references):
            - Current date:           {now:yyyy-MM-dd} ({now:dddd})
            - Current month:          {now:MMMM yyyy}
            - Previous month:         {prevMonth:MMMM yyyy}
            - Current quarter:        Q{currentQ} {now.Year}
            - Previous quarter:       Q{prevQ} {prevQYear}
            - Current fiscal year:    {now.Year}
            - Previous fiscal year:   {now.Year - 1}
            - YTD start:              {fiscalYTDStart:yyyy-MM-dd}
            - Current day of week:    {now:dddd}
            """;
    }

    public LlmIntentRewriter(AppConfig config)
    {
        _modelId = config.BedrockLlmModelId;
        _client  = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    /// <inheritdoc/>
    public async Task<RewrittenIntent> RewriteAsync(
        string rawText,
        ConversationHistory? history = null,
        CancellationToken ct = default)
    {
        var context = history?.BuildContext(recentCount: 4);
        var userContent = BuildUserContent(rawText, context);

        var request = new ConverseRequest
        {
            ModelId = _modelId,
            System  =
            [
                new SystemContentBlock { Text = SystemPrompt },
                new SystemContentBlock { Text = BuildRuntimeContext() }
            ],
            Messages =
            [
                new Message
                {
                    Role    = ConversationRole.User,
                    Content = [new ContentBlock { Text = userContent }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = 200,
                Temperature = 0f   // deterministic
            }
        };

        try
        {
            var response = await _client.ConverseAsync(request, ct);
            var json     = response.Output.Message.Content[0].Text.Trim();
            return ParseJson(rawText, json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Graceful degradation: log and return original text
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [Rewriter] LLM call failed ({ex.GetType().Name}) — using raw text.");
            Console.ResetColor();
            return new RewrittenIntent(rawText, rawText,
                new Dictionary<string, string>());
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildUserContent(string rawText, string? context)
    {
        if (string.IsNullOrEmpty(context))
            return $"User message: \"{rawText}\"";

        return $"""
            Conversation context:
            {context}

            User message: "{rawText}"
            """;
    }

    private static RewrittenIntent ParseJson(string original, string json)
    {
        // Strip accidental markdown fences
        var cleaned = json.TrimStart('`');
        if (cleaned.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[4..];
        cleaned = cleaned.TrimEnd('`').Trim();

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var normalizedText = root.TryGetProperty("normalizedText", out var nt)
                ? nt.GetString() ?? original
                : original;

            var entities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("entities", out var ents))
                foreach (var prop in ents.EnumerateObject())
                {
                    var val = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        entities[prop.Name] = val;
                }

            var capabilities = new List<string>();
            if (root.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
            {
                foreach (var cap in caps.EnumerateArray())
                {
                    var val = cap.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        capabilities.Add(val);
                }
            }

            return new RewrittenIntent(original, normalizedText, entities, capabilities);
        }
        catch
        {
            return new RewrittenIntent(original, original,
                new Dictionary<string, string>(), new List<string>());
        }
    }

    public void Dispose() => _client.Dispose();
}
