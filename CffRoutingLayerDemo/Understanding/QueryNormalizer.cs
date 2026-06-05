// Understanding/QueryNormalizer.cs
namespace CffRoutingLayerDemo.Understanding;

using System.Text.RegularExpressions;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Normalizes query text by replacing entity values with typed slot placeholders
/// BEFORE embedding. Applied symmetrically to:
///   • Plan sample queries at index build time (NormalizeSampleQuery)
///   • Incoming queries at retrieval time      (NormalizeWithSlots)
///
/// This ensures entity-variant queries ("cashflow for Pepe" vs "cashflow for Acme")
/// collapse to the same semantic vector and match the plan regardless of the
/// specific entity value the user provided.
///
/// Inspired by the ensemble entity-extraction approach described in GenPlanX [21]:
/// company ids, customer names, dates, fiscal years, periods, and other
/// domain-specific identifiers are lifted out before semantic comparison.
/// </summary>
public static class QueryNormalizer
{
    // ── Structural patterns applied to both plan queries and incoming queries ──
    // Ordered: longer/more-specific patterns first to prevent partial replacements.
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

        // "for company Pepe", "for customer Marc", "for client Acme" (single word)
        (new Regex(
            @"\bfor\s+(?:company|customer|client|firm|supplier)\s+[A-Za-z][A-Za-z0-9\-]*\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
         "for ${entity}"),

        // "for Acme Corp", "for Pepe Industries" (title-case multi-word)
        (new Regex(@"\bfor\s+[A-Z][a-z]+(?:\s+[A-Z][a-z]+)+\b", RegexOptions.Compiled), "for ${entity}"),

        // ISO / partial dates:  2024-01-01, 01/2024, 2024/01
        (new Regex(@"\b\d{4}[-/]\d{2}(?:[-/]\d{2})?\b|\b\d{2}[-/]\d{4}\b", RegexOptions.Compiled), "${date}"),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalize a plan's sample query, replacing known entity patterns with typed
    /// placeholders. Used when building plan embedding text so all entity variants
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
    /// Normalize an incoming query using slots already extracted by Phase 0.
    /// First replaces slot values extracted by the LLM (highest fidelity), then
    /// applies the same structural patterns as <see cref="NormalizeSampleQuery"/>.
    /// </summary>
    public static string NormalizeWithSlots(string query, IEnumerable<QuerySlot> slots)
    {
        var result = query;

        // 1. Replace extracted slot values with ${slotName} — longest values first
        //    to avoid partial token replacements (e.g. "Pepe Industries" before "Pepe").
        foreach (var slot in slots
            .Where(s => s.Confidence >= 0.5f && !string.IsNullOrWhiteSpace(s.Value))
            .OrderByDescending(s => s.Value.Length))
        {
            result = ReplaceIgnoreCase(result, slot.Value, $"${{{slot.Name}}}");
        }

        // 2. Apply structural patterns for anything the LLM didn't explicitly extract.
        foreach (var (pattern, placeholder) in StructuralPatterns)
            result = pattern.Replace(result, placeholder);

        return result;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ReplaceIgnoreCase(string source, string search, string replacement) =>
        Regex.Replace(source, Regex.Escape(search), Regex.Escape(replacement).Replace(@"\$", "$"),
            RegexOptions.IgnoreCase);
}
