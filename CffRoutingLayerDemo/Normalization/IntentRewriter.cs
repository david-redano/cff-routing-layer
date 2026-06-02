// Normalization/IntentRewriter.cs
namespace CffRoutingLayerDemo.Normalization;

using System.Text.RegularExpressions;
using CffRoutingLayerDemo.Conversation;

// ── Public interface ──────────────────────────────────────────────────────────

/// <summary>
/// Rewrites raw user text into a PII-free canonical form and extracts named entities.
/// Implementations: <see cref="RegexIntentRewriter"/> (local, no I/O) and
/// <see cref="LlmIntentRewriter"/> (Bedrock-backed, supports coreference via history).
/// </summary>
public interface IIntentRewriter
{
    Task<RewrittenIntent> RewriteAsync(
        string rawText,
        ConversationHistory? history = null,
        CancellationToken ct = default);
}

// ── Regex implementation (no-I/O fallback) ────────────────────────────────────

/// <summary>
/// Pure-regex implementation of <see cref="IIntentRewriter"/>.
/// No I/O, &lt; 1 ms per call. Used when <c>USE_LLM_REWRITER=false</c>.
/// Cannot resolve coreferences (e.g. "same account as before") — use
/// <see cref="LlmIntentRewriter"/> for that capability.
/// </summary>
public sealed class RegexIntentRewriter : IIntentRewriter
{
    private static readonly IntentRewriterEngine _engine = new();

    public Task<RewrittenIntent> RewriteAsync(
        string rawText,
        ConversationHistory? history = null,
        CancellationToken ct = default)
        => Task.FromResult(_engine.Rewrite(rawText));
}

// ── Internal engine (shared by RegexIntentRewriter) ───────────────────────────

/// <summary>
/// Internal regex engine. Not part of the public API — use
/// <see cref="RegexIntentRewriter"/> or <see cref="LlmIntentRewriter"/>.
/// </summary>
internal sealed class IntentRewriterEngine
{
    private static readonly Regex _invoiceItemPattern = new(
        @"\b(\d+)\s+([a-zA-Z]+(?:\s+[a-zA-Z]+)?)\s+at\s+(\d+(?:\.\d{2})?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Standalone unit-price suffix: "400 each", "150 per unit", "$5 apiece", "200 per item"
    private static readonly Regex _unitPriceSuffixPattern = new(
        @"(?:\$)?(\d+(?:\.\d{2})?)\s+(?:each|apiece|per\s+unit|per\s+item)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Combined items + price: "400 bikes, 400 each" / "5 hours 150 per unit" (no "at" separator)
    private static readonly Regex _itemsAndPricePattern = new(
        @"\b(\d+)\s+([a-zA-Z]+(?:\s+[a-zA-Z]+)?),?\s+(\d+(?:\.\d{2})?)\s+(?:each|apiece|per\s+unit|per\s+item)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex _dueDatePattern = new(
        @"\bdue\s+(?:date\s+)?in\s+(\d+\s+(?:days?|weeks?|months?))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Specific-date due dates: "due (date/data) (on/by/coming) DATE"
    // Handles typo "due data" and formats: "7th June", "June 7th", "YYYY-MM-DD", "MM/DD"
    private static readonly Regex _specificDueDatePattern = new(
        @"\bdue\s+(?:dat[ae]\s+)?(?:on\s+|by\s+|coming\s+)?(" +
        @"\d{1,2}(?:st|nd|rd|th)?\s+(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)(?:\s+\d{4})?|" +
        @"(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2}(?:st|nd|rd|th)?(?:\s+\d{4})?|" +
        @"\d{1,2}[/\-]\d{1,2}(?:[/\-]\d{2,4})?|" +
        @"\d{4}[/\-]\d{2}[/\-]\d{2})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches "10% off", "15% discount", "10 percent off", "$50 off"
    private static readonly Regex _discountPattern = new(
        @"\b(\d+(?:\.\d{1,2})?)\s*(?:%|percent)\s+(?:off|discount)|(?:\$[\d,]+(?:\.\d{2})?)\s+(?:off|discount)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── PII extraction rules (order: most-specific first) ─────────────────

    private static readonly (Regex Pattern, string Placeholder, string EntityKey)[] PiiRules =
    [
        // Account IDs  e.g. CHK-001, SAV-9901, ACC-12345
        (new Regex(@"\b([A-Z]{2,4}-\d{3,6})\b", RegexOptions.Compiled),
            "${accountId}", "accountId"),

        // Payment terms  e.g. net 30, net-60, COD, due on receipt, 2/10 net 30
        (new Regex(
            @"\b(net[\s\-]\d+|due\s+on\s+receipt|cash\s+on\s+delivery|COD|\d+\/\d+\s+net\s+\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${paymentTerms}", "paymentTerms"),

        // Monetary amounts  e.g. $1,234.56  $500
        (new Regex(@"\$[\d,]+(?:\.\d{2})?", RegexOptions.Compiled),
            "${amount}", "amount"),

        // Tax / interest rate \u2014 "21% corporate rate", "8.5% effective rate", "6% interest rate"
        (new Regex(
            @"\b(\d+(?:\.\d{1,2})?%)\s+(?:corporate|effective|tax|interest)\s+rate\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${taxRate}", "taxRate"),

        // Tax rate \u2014 "tax rate of 21%", "rate of 8.5%"
        (new Regex(
            @"\b(?:tax\s+)?rate\s+of\s+(\d+(?:\.\d{1,2})?%)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${taxRate}", "taxRate"),

        // Quarter + year  e.g. Q1 2024, Q3 2025
        (new Regex(@"\bQ([1-4])\s*(20\d{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${period}", "period"),

        // Relative periods
        (new Regex(
            @"\b(last\s+\d+\s+days?|this\s+month|last\s+month|year[\s\-]to[\s\-]date|ytd|last\s+quarter|this\s+quarter|last\s+year|this\s+year|next\s+year|last\s+fiscal\s+year|this\s+fiscal\s+year)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${period}", "period"),

        // Named months  e.g. January, Feb, March
        (new Regex(
            @"\b(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${period}", "period"),

        // Tax / fiscal year  e.g. 2024, 2025
        (new Regex(@"\b(20\d{2})\b", RegexOptions.Compiled),
            "${year}", "year"),

        // Legal entity types
        (new Regex(
            @"\b(LLC|S-Corp|C-Corp|sole\s+proprietor|partnership|S\s+Corp|C\s+Corp)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${entityType}", "entityType"),

        // Payment method — unambiguous terms only ("check" excluded: too many false positives)
        (new Regex(
            @"\b(ACH|wire\s+transfer|bank\s+transfer|credit\s+card|debit\s+card|cheque|electronic\s+transfer|Zelle|PayPal)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${paymentMethod}", "paymentMethod"),

        // Customer / company name: title-case word(s) after relational preposition
        (new Regex(
            @"\b(?:for|to|from|by)\s+((?:[A-Z][a-zA-Z&]+)(?:\s+(?:[A-Z][a-zA-Z&]+)){0,3})",
            RegexOptions.Compiled),
            "for ${customer}", "customer"),
    ];

    // ── Surface normalisation dictionary ──────────────────────────────────

    private static readonly (Regex Pattern, string Replacement)[] NormalisationRules =
    [
        (new Regex(@"\bp\s*[&and]+\s*l\b|\bpnl\b|\bprofit\s+&\s+loss\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "profit and loss"),
        (new Regex(@"\ba[/]?r\b(?!\w)|\baccounts?\s+receivable\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "accounts receivable"),
        (new Regex(@"\ba[/]?p\b(?!\w)|\baccounts?\s+payable\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "accounts payable"),
        (new Regex(@"\brecon(?:ciliation)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "reconcile"),
        (new Regex(@"\bbill(?:ing)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "invoice"),
    ];

    // ── Public API ────────────────────────────────────────────────────────

    public RewrittenIntent Rewrite(string rawText)
    {
        var entities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text     = rawText.Trim();

        // Step 0a: pre-process invoice line-item pattern "N items at PRICE"
        // Must run before PII rules so quantity/unitPrice aren't swallowed by other rules.
        var itemMatch = _invoiceItemPattern.Match(text);
        if (itemMatch.Success)
        {
            entities["quantity"]        = itemMatch.Groups[1].Value;
            entities["itemDescription"] = itemMatch.Groups[2].Value;
            entities["unitPrice"]       = itemMatch.Groups[3].Value;
            text = _invoiceItemPattern.Replace(text, "${quantity} ${itemDescription} at ${unitPrice}");
        }

        // Step 0a-bis: combined "N items, PRICE each" — no "at" separator
        // Runs after the "at PRICE" pattern and before the standalone suffix so it handles
        // phrases like "400 bikes, 400 each" or "5 hours 150 per unit".
        if (!entities.ContainsKey("unitPrice"))
        {
            var comboMatch = _itemsAndPricePattern.Match(text);
            if (comboMatch.Success)
            {
                entities["quantity"]        = comboMatch.Groups[1].Value;
                entities["itemDescription"] = comboMatch.Groups[2].Value;
                entities["unitPrice"]       = comboMatch.Groups[3].Value;
                text = _itemsAndPricePattern.Replace(text,
                    "${quantity} ${itemDescription} at ${unitPrice}");
            }
        }

        // Step 0a′: standalone unit-price suffix "N each", "N per unit", "N apiece", "$N each"
        // Runs after the "at PRICE" pattern so it only fires when no "at" form was found.
        if (!entities.ContainsKey("unitPrice"))
        {
            var upMatch = _unitPriceSuffixPattern.Match(text);
            if (upMatch.Success)
            {
                entities["unitPrice"] = upMatch.Groups[1].Value;
                text = _unitPriceSuffixPattern.Replace(text, "${unitPrice} each");
            }
        }

        // Step 0b: pre-process relative due-date "due date in N days" / "due in N weeks"
        var dueMatch = _dueDatePattern.Match(text);
        if (dueMatch.Success)
        {
            entities["dueDate"] = dueMatch.Groups[1].Value;
            text = _dueDatePattern.Replace(text, "due ${dueDate}");
        }

        // Step 0b-specific: specific-date due dates
        // Handles typo "due data", "due date coming 7th June", "due on June 7th", ISO dates.
        // Must run BEFORE PiiRules so the month name is not consumed as ${period}.
        if (!entities.ContainsKey("dueDate"))
        {
            var specDueMatch = _specificDueDatePattern.Match(text);
            if (specDueMatch.Success)
            {
                entities["dueDate"] = specDueMatch.Groups[1].Value.Trim();
                text = _specificDueDatePattern.Replace(text, "due ${dueDate}");
            }
        }

        // Step 0c: pre-process discount — "10% off", "$50 off"
        // Must run before the monetary-amount rule so the $ value isn't consumed first.
        var discountMatch = _discountPattern.Match(text);
        if (discountMatch.Success)
        {
            // Prefer the percentage group; fall back to the full $ match
            var discountVal = discountMatch.Groups[1].Success
                ? discountMatch.Groups[1].Value + "%"
                : discountMatch.Value.Split(' ')[0];   // "$50"
            entities["discount"] = discountVal;
            text = _discountPattern.Replace(text, "${discount} off");
        }

        // Step 1: apply surface normalisation (no entity extraction)
        foreach (var (pattern, replacement) in NormalisationRules)
            text = pattern.Replace(text, replacement);

        // Step 2: extract PII entities and replace with placeholders
        foreach (var (pattern, placeholder, entityKey) in PiiRules)
        {
            text = pattern.Replace(text, match =>
            {
                var value = match.Groups.Count > 1 && match.Groups[1].Success
                    ? match.Groups[1].Value
                    : match.Value;

                if (!entities.ContainsKey(entityKey))
                    entities[entityKey] = value.Trim();

                // For the customer rule, preserve the preposition prefix
                return entityKey == "customer" ? "for ${customer}" : placeholder;
            });
        }

        // Step 3: collapse multiple spaces
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();

        // Step 4: extract capabilities (simple keyword match)
        var capabilities = new List<string>();
        var lowered = text.ToLowerInvariant();
        if (lowered.Contains("profit and loss") || lowered.Contains("p&l") || lowered.Contains("pnl"))
            capabilities.Add("profit-loss");
        if (lowered.Contains("invoice") || lowered.Contains("billing"))
            capabilities.Add("invoice");
        if (lowered.Contains("accounts receivable") || lowered.Contains("a/r"))
            capabilities.Add("accounts-receivable");
        if (lowered.Contains("accounts payable") || lowered.Contains("a/p"))
            capabilities.Add("accounts-payable");
        if (lowered.Contains("reconcile"))
            capabilities.Add("reconciliation");
        if (lowered.Contains("report"))
            capabilities.Add("reporting");

        return new RewrittenIntent(rawText, text, entities, capabilities);
    }
}
