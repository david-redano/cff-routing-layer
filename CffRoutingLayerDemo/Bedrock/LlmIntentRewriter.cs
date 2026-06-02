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
           - Dollar amounts like $1,234.56 or $500 → ${amount}
           - Invoice line-item quantities (e.g. "4 bikes", "10 units") → the number becomes ${quantity},
             the item name (e.g. "bikes", "units") becomes ${itemDescription}
           - Unit prices in invoice context → ${unitPrice}; recognised patterns:
               • "at N" or "at $N"           ("4 bikes at 200", "at $4.99")
               • "N each" / "N apiece"        ("400 each", "$5 apiece")
               • "N per unit" / "N per item"  ("150 per unit")
               • "$N each" / "$N/unit"        ("$200 each")
             IMPORTANT: bare standalone numbers that are NOT preceded or followed by a price
             indicator (at/each/per unit/apiece) must NOT be replaced with ${unitPrice}.
           - COREFERENCE PRECEDENCE: if the current message explicitly states a new value for
             any entity (customer, amount, unitPrice, quantity, dueDate, etc.), ALWAYS use the
             current message value. Only resolve from history when the current message contains
             NO explicit value for that entity.
           - Due dates → ${dueDate}. Recognised forms (note: "due data" is a common typo for "due date"):
               • Relative:  "due in 7 days", "due date in 2 weeks", "due next Friday"
               • Specific:  "due 7th June", "due on June 7th", "due date coming 7th June",
                            "due 2026-06-07", "due 06/07/2026"
               • With typo: "due data coming 7th June" → dueDate: "7th June"
             IMPORTANT: use ${dueDate} for invoice due dates, NEVER ${period}. ${period} is ONLY for
             financial reporting windows (last month, Q2 2024, YTD, etc.).
             IMPORTANT: do NOT capture abstract nouns as dueDate. Words like "deadline", "expiry",
             "due date" (the phrase alone), "close to deadline", "near expiry" are status filter
             phrases — they carry no concrete date value and must NOT be captured as ${dueDate}.
             Only capture dueDate when the message states an actual date, day count, or named
             relative period (e.g. "in 7 days", "next Friday", "7th June").
           - Calendar periods (this month, last 30 days, Q2 2024, January, YTD) → ${period}
           - 4-digit fiscal years (2024, 2025) → ${year}
           - Legal entity types (LLC, S-Corp, C-Corp, sole proprietor) → ${entityType}
           - Payment terms (e.g. "net 30", "net 60", "net-45", "due on receipt", "COD", "2/10 net 30") → ${paymentTerms}
           - Tax or interest rates explicitly stated (e.g. "21% corporate rate", "8.5% effective rate",
             "tax rate of 15%", "at 6% interest") → ${taxRate}; capture the numeric value with the % sign.
             IMPORTANT: do NOT capture percentage discounts ("10% off") as taxRate — those are ${discount}.
           - Payment method (e.g. "by check", "via ACH", "wire transfer", "credit card", "bank transfer",
             "cash", "cheque", "Zelle", "PayPal") → ${paymentMethod}
           - Discounts (e.g. "10% off", "15% discount", "$50 off", "10 percent off") → ${discount};
             capture the value including the % or $ sign.

        2. Surface normalisation (no entity capture — just rename):
           - P&L / pnl / profit & loss → "profit and loss"
             IMPORTANT: "profit anomaly", "profit anomalies", or any phrase containing "anomaly"
             must NEVER be changed to "profit and loss". Only exact abbreviations
             (P&L, pnl, p & l) or the phrase "profit & loss" trigger this rule.
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
        7. CRITICAL — entity values are ALWAYS the actual text extracted from the user message.
           NEVER put a ${slotName} placeholder as an entity value. The ${...} syntax belongs ONLY
           in normalizedText. If you replaced "marc" with ${customer} in normalizedText, the
           entities object must contain "customer": "marc" — not "customer": "${customer}".

                Response format (JSON only — include ONLY slots that have a real value):
                {
                    "normalizedText": "i want to create an invoice for ${customer} for ${quantity} ${itemDescription} at ${unitPrice}, due ${dueDate}",
                    "entities": {
                        "customer": "Marc",
                        "quantity": "4",
                        "itemDescription": "bikes",
                        "unitPrice": "200",
                        "dueDate": "7 days"
                    },
                    "capabilities": ["invoicing"] // array of keywords or features relevant to the user request
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
                MaxTokens   = 500,
                Temperature = 0f   // deterministic
            }
        };

        try
        {
            var response = await _client.ConverseAsync(request, ct);
            var json     = response.Output.Message.Content[0].Text.Trim();
            var result   = ParseJson(rawText, json);

            // Regex fallback: if the LLM extracted no entity slots (e.g. unusual format
            // like "400 bikes, 400 each" or "due data coming 7th June"), fall back to the
            // local regex engine as a safety net so slots are never silently lost.
            if (result.ExtractedEntities.Count == 0)
            {
                var regex = new IntentRewriterEngine().Rewrite(rawText);
                if (regex.ExtractedEntities.Count > 0)
                    return new RewrittenIntent(
                        result.OriginalText,
                        // Prefer LLM normalized text when it actually changed something
                        result.NormalizedText != rawText ? result.NormalizedText : regex.NormalizedText,
                        regex.ExtractedEntities,
                        result.Capabilities);
            }

            return result;
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
                    // Reject values that are still placeholder tokens (e.g. "${dueDate}").
                    // The LLM occasionally echoes the normalizedText placeholder instead of
                    // the real extracted value; treating these as absent is safer.
                    if (!string.IsNullOrWhiteSpace(val)
                        && !(val.StartsWith("${", StringComparison.Ordinal) && val.EndsWith("}")))
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
