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
    // ── PII extraction rules (order: most-specific first) ─────────────────

    private static readonly (Regex Pattern, string Placeholder, string EntityKey)[] PiiRules =
    [
        // Account IDs  e.g. CHK-001, SAV-9901, ACC-12345
        (new Regex(@"\b([A-Z]{2,4}-\d{3,6})\b", RegexOptions.Compiled),
            "${accountId}", "accountId"),

        // Monetary amounts  e.g. $1,234.56  $500
        (new Regex(@"\$[\d,]+(?:\.\d{2})?", RegexOptions.Compiled),
            "${amount}", "amount"),

        // Quarter + year  e.g. Q1 2024, Q3 2025
        (new Regex(@"\bQ([1-4])\s*(20\d{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            "${period}", "period"),

        // Relative periods
        (new Regex(
            @"\b(last\s+\d+\s+days?|this\s+month|last\s+month|year[\s\-]to[\s\-]date|ytd|last\s+quarter|this\s+quarter)\b",
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
