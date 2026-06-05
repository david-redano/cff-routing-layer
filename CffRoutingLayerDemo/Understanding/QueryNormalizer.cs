// Understanding/QueryNormalizer.cs
namespace CffRoutingLayerDemo.Understanding;

using System.Text.RegularExpressions;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Normalizes query text by replacing TEMPORAL tokens with typed placeholders
/// BEFORE embedding. Applied symmetrically to:
///   • Plan sample queries at index build time (NormalizeSampleQuery)
///   • Incoming queries at retrieval time      (NormalizeWithSlots)
///
/// Only temporal tokens (fiscal years, quarters, months, bare years, ISO dates)
/// are normalized. Named entity values (customer names, invoice IDs, etc.) are
/// intentionally left as-is: normalizing them collapses distinct semantic meaning
/// and produces worse embedding similarity when the plan text retains the original
/// natural-language phrasing.
/// </summary>
public static class QueryNormalizer
{
    // ── Temporal-only patterns applied to both plan queries and incoming queries ──
    // Entity names (customers, vendors, invoice IDs, etc.) are NOT normalized here;
    // doing so hurts embedding similarity by collapsing semantically distinct text.
    private static readonly (Regex Pattern, string Placeholder)[] StructuralPatterns =
    [
        // Fiscal year:  FY2024, FY2025
        (new Regex(@"\bFY\s*\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "${fiscalYear}"),

        // Month name:  January, February … December
        (new Regex(
            @"\b(january|february|march|april|may|june|july|august|september|october|november|december)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "${month}"),

        // Calendar quarter:  Q1, Q2, Q3, Q4
        (new Regex(@"\bQ[1-4]\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "${quarter}"),

        // 4-digit year (after FY already consumed):  2024, 2025, 2026
        (new Regex(@"\b20\d{2}\b", RegexOptions.Compiled), "${year}"),

        // ISO / partial dates:  2024-01-01, 01/2024, 2024/01
        (new Regex(@"\b\d{4}[-/]\d{2}(?:[-/]\d{2})?\b|\b\d{2}[-/]\d{4}\b", RegexOptions.Compiled), "${date}"),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalize a plan's sample query, replacing temporal tokens with typed
    /// placeholders. Used when building plan embedding text so temporal variants
    /// (e.g. "March", "April", "FY2025") map to the same embedding.
    /// </summary>
    public static string NormalizeSampleQuery(string query)
    {
        var result = query;
        foreach (var (pattern, placeholder) in StructuralPatterns)
            result = pattern.Replace(result, placeholder);
        return result;
    }

    /// <summary>
    /// Normalize an incoming query by replacing temporal tokens with typed placeholders.
    /// Entity slot values (customer names, IDs, etc.) are intentionally NOT replaced —
    /// keeping them in the query text produces better embedding similarity against
    /// plans whose understanding/sample-query text also retains the natural phrasing.
    /// The <paramref name="slots"/> parameter is accepted but unused; it remains in the
    /// signature so call sites do not need to change.
    /// </summary>
    public static string NormalizeWithSlots(string query, IEnumerable<QuerySlot> slots)
    {
        var result = query;

        // Apply temporal-only structural patterns.
        foreach (var (pattern, placeholder) in StructuralPatterns)
            result = pattern.Replace(result, placeholder);

        return result;
    }
}
