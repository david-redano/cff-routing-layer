// Normalization/RegexIntentRewriter.cs
namespace CffRoutingLayerDemo.Normalization;

using System.Text.RegularExpressions;

/// <summary>
/// Normalises a raw user query by:
///   1. Replacing PII / entity-specific values with named placeholders.
///   2. Expanding surface-form abbreviations to canonical tokens.
/// The resulting <see cref="RewriteResult.NormalizedText"/> is safe to use as
/// a semantic-cache key without leaking customer data.
/// </summary>
public sealed partial class RegexIntentRewriter
{
    // ── PII / entity patterns ──────────────────────────────────────────────

    // Account IDs:  CHK-001, SAV-9901, ACC-42, etc.
    [GeneratedRegex(@"\b[A-Z]{2,4}-\d+\b")]
    private static partial Regex AccountIdPattern();

    // Dollar amounts: $4,500 / $4500 / 4,500$ / 1.5k
    [GeneratedRegex(@"\$[\d,]+(\.\d+)?|\b[\d,]+(\.\d+)?\s*\$|\b\d+(\.\d+)?k\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MoneyPattern();

    // Calendar months (full names only — avoids false-positives on short words)
    [GeneratedRegex(
        @"\b(january|february|march|april|may|june|july|august|september|october|november|december)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MonthPattern();

    // Fiscal/calendar years: FY2024, FY24, 2024 (standalone 4-digit)
    [GeneratedRegex(@"\bFY?\d{2,4}\b|\b(19|20)\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex YearPattern();

    // Company names: one or more capitalised words followed by a legal suffix.
    // Excludes single-letter abbreviations like "S-Corp".
    [GeneratedRegex(
        @"\b([A-Z][a-z]+(?:\s+[A-Z][a-z]+)*)\s+(?:LLC|Inc\.?|Corp\.?|Ltd\.?|Co\.?|Group|Ventures|Technologies)\b")]
    private static partial Regex CompanyNamePattern();

    // CamelCase / PascalCase compound words used as company names (e.g. TechVentures, AcmeCorp).
    // Requires at least one embedded capital letter after lowercase chars.
    [GeneratedRegex(@"\b[A-Z][a-z]+[A-Z][a-zA-Z]+\b")]
    private static partial Regex CamelCaseCompanyPattern();

    // ── Surface-form normalisations ───────────────────────────────────────

    // P&L / P & L / pnl  →  profit and loss
    [GeneratedRegex(@"\bP\s*&\s*L\b|\bpnl\b", RegexOptions.IgnoreCase)]
    private static partial Regex PnlPattern();

    // recon  →  reconcile
    [GeneratedRegex(@"\brecon\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReconPattern();

    // "bill [name] for" → "invoice [name] for"  (verb usage of "bill")
    [GeneratedRegex(@"\bbill\b", RegexOptions.IgnoreCase)]
    private static partial Regex BillVerbPattern();

    // Whitespace collapse
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseWhitespace();

    // ── Public API ─────────────────────────────────────────────────────────

    public Task<RewriteResult> RewriteAsync(string rawQuery)
    {
        var text = rawQuery;

        // 1. Surface-form normalisations (before PII stripping so patterns stay intact)
        text = PnlPattern().Replace(text, "profit and loss");
        text = ReconPattern().Replace(text, "reconcile");
        text = BillVerbPattern().Replace(text, "invoice");

        // 2. Replace company names with ${customer}
        text = CamelCaseCompanyPattern().Replace(text, "${customer}");
        text = CompanyNamePattern().Replace(text, "${customer}");

        // 3. Replace account IDs with ${accountId}
        text = AccountIdPattern().Replace(text, "${accountId}");

        // 4. Replace monetary values with ${amount}
        text = MoneyPattern().Replace(text, "${amount}");

        // 5. Replace calendar months with ${period}
        text = MonthPattern().Replace(text, "${period}");

        // 6. Replace years with ${year}
        text = YearPattern().Replace(text, "${year}");

        // 7. Lower-case and collapse whitespace, then restore placeholder casing
        text = CollapseWhitespace().Replace(text.Trim(), " ").ToLowerInvariant();
        text = text.Replace("${accountid}", "${accountId}")
                   .Replace("${customer}",  "${customer}")   // already lowercase
                   .Replace("${amount}",    "${amount}")
                   .Replace("${period}",    "${period}")
                   .Replace("${year}",      "${year}");

        return Task.FromResult(new RewriteResult(rawQuery, text));
    }
}

public sealed record RewriteResult(string OriginalText, string NormalizedText);

