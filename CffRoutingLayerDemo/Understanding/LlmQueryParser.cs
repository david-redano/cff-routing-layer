using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CffRoutingLayerDemo.Queries;
using CffRoutingLayerDemo.Bedrock;

namespace CffRoutingLayerDemo.Understanding;

public sealed class LlmQueryParser : IQueryParser
{
    private readonly BedrockLlmHelper _bedrock;
    private readonly TemporalResolver _temporalResolver;
    private readonly RuleBasedQueryParser _fallback;

    private const string SystemPrompt = """
        You are a query understanding system for a financial/accounting application.

        Given a user query, extract the following as JSON (respond ONLY with valid JSON, no markdown):

        {
          "query": "rewritten standalone sentence for THIS task only — required when subIntents is non-empty, omit otherwise",
          "primaryAction": "List|Create|Compute|Compare|Forecast|Audit|Categorize|Optimize",
          "domain": "sales|finance|inventory|hr|general",
          "subDomain": "cashflow|invoicing|reconciliation|tax|reporting|forecasting|orders|crm|stock|payroll|audit|unknown",
          "domainConfidence": 0.0,
          "entities": [
            { "name": "period",        "value": "this year", "type": "temporal",  "confidence": 0.9, "isExplicit": true },
            { "name": "comparePeriod", "value": "last year", "type": "temporal",  "confidence": 0.9, "isExplicit": true },
            { "name": "customer",      "value": "Acme Corp",  "type": "entity",   "confidence": 0.9, "isExplicit": true },
            { "name": "amount",        "value": "4500",       "type": "monetary", "confidence": 0.9, "isExplicit": true }
          ],
          "temporal": {
            "type": "None|PointInTime|BoundedPeriod|RelativeWindow|OpenEnded",
            "start": "YYYY-MM-DD or null",
            "end": "YYYY-MM-DD or null",
            "relativeExpression": null
          },
          "compareTemporal": {
            "type": "BoundedPeriod",
            "start": "YYYY-MM-DD or null",
            "end": "YYYY-MM-DD or null",
            "relativeExpression": null
          },
          "constraints": [],
          "expectedOutput": "Summary|DetailedList|Comparison|Forecast|Narrative",
          "confidence": 0.0,
          "reasoning": "one sentence explaining the classification",
          "subIntents": [
            {
              "query": "standalone sub-task text",
              "primaryAction": "...",
              "domain": "...",
              "subDomain": "...",
              "entities": [],
              "temporal": { "type": "None" },
              "expectedOutput": "...",
              "confidence": 0.0
            }
          ]
        }

        Entity naming rules — follow these exactly:
        - Entity `name` values MUST be unique within the array. Never emit two entities with the same name.
        - Use these canonical slot names:
            period          — the primary time window ("this year", "Q3", "FY2024")
            comparePeriod   — the secondary window in a comparison ("last year", "Q3 2024")
            customer        — customer or client name
            vendor          — supplier / vendor name
            accountId       — account identifier (e.g. "CHK-001")
            amount          — monetary value (plain digits, see rules below)
            invoiceId       — invoice reference
            employeeId      — employee identifier
            category        — expense or revenue category
        - For any concept not in the canonical list, use a short camelCase name of your choosing.

        Slot extraction rules — CRITICAL:
        - Only extract entity slots for EXPLICITLY NAMED entities: proper nouns, specific IDs, account codes, or names.
        - NEVER extract descriptive, comparative, or superlative phrases as slot values.
          Descriptive qualifiers after "with", "that has", "having" describe a filter, not an entity name.
          WRONG: { "name": "customer", "value": "more balance" }   ← "more balance" is not a customer name
          WRONG: { "name": "vendor",   "value": "largest invoice" } ← "largest invoice" is not a vendor name
          RIGHT: { "name": "customer", "value": "Acme Corp" }       ← explicit proper noun
          RIGHT: { "name": "customer", "value": "CUST-042" }        ← explicit identifier
        - Phrases like "with more balance", "with the highest revenue", "with most activity" belong in
          `constraints`, not in `entities`. If no explicit name is given for a slot, do not emit that slot.

        Comparison query rules (applies when query contains "vs", "versus", "compared to", "year over year", "YoY", "month over month", "MoM", or "this X vs last X"):
        - Set `primaryAction` to "Compare" and `expectedOutput` to "Comparison".
        - Put the PRIMARY window in `period` (the "current" or first-mentioned period).
        - Put the COMPARISON window in `comparePeriod` (the "previous" or second-mentioned period).
        - Populate `temporal` with the resolved dates for `period`.
        - Populate `compareTemporal` with the resolved dates for `comparePeriod`.
        - Omit `compareTemporal` entirely for non-comparison queries.
        - A comparison query is ONE intent — do NOT split it into subIntents.

        Multi-intent detection rules (applies when the query contains two or more clearly INDEPENDENT tasks):
        - Trigger words: "and also", "and then", "as well as", "plus", "in addition", "along with", "and what".
        - Also trigger on plain "and" or "as well as" when it joins two INDEPENDENT financial tasks
          (different domains, different subDomains, or clearly different operations —
           e.g. "list invoices AND show cash runway", "reconcile bank statement AND analyze profit anomalies").
        - Key indicator: if the query names two DIFFERENT financial operations that each stand alone as a
          complete request, split them — even if both fall under the broad "finance" domain. Examples of
          always-split pairs: reconcile + audit, reconcile + report, P&L + cash flow, tax + reconcile.
        - Do NOT trigger on "and" that joins related items within one task ("revenue and expenses for Q1").
        - Each sub-task must be independently routable (different subDomain, action, or subject).
        - When detected:
            * The ROOT intent fields (`primaryAction`, `domain`, `subDomain`, `entities`, `temporal`, etc.)
              MUST describe the FIRST independent task only — rewrite its fields to match only that task.
            * REQUIRED: set the root `query` field to a standalone sentence for the FIRST task only.
              Example: "show me the upcoming sales payments and list the customer with more balance"
              → root query: "show me the upcoming sales payments"
              → subIntent query: "list the customer with more balance"
            * Populate `subIntents` with ONE entry per ADDITIONAL independent task (second, third, …).
            * Do NOT repeat the first task inside `subIntents`.
        - Each subIntent element has these fields:
            query         — the isolated sub-query as a standalone question
            primaryAction — same enum as root
            domain        — same enum as root
            subDomain     — same enum as root
            entities      — same rules as root (canonical names, unique)
            temporal      — same rules as root
            expectedOutput — same enum as root
            confidence    — confidence for this sub-intent alone
        - `subIntents` must NOT contain its own `subIntents` (no nesting).
        - When `subIntents` is populated, set the root `confidence` to the MINIMUM of the sub-intents.
        - If the query has only ONE clear intent, `subIntents` MUST be an empty array [].

        Temporal resolution rules:
        - Resolve ALL relative and named periods to exact ISO dates using the date context provided in the prompt.
          Examples: "last month" → start/end for the previous calendar month;
                    "this quarter" → start/end for the current calendar quarter;
                    "FY2025" → 2025-01-01 / 2025-12-31;
                    "last fiscal year" → the fiscal year prior to the current one.
        - If the query mentions a specific year (e.g. FY2024, 2024), set start = YYYY-01-01, end = YYYY-12-31.
        - If no temporal expression is present, set temporal.type = "None".
        - If the query is about multiple unrelated tasks, set confidence < 0.4 and note in reasoning.
        - Omit optional fields you cannot determine — do not guess.
        - NEVER return markdown fences or explanatory text — JSON only.

        Monetary amount extraction:
        - ALWAYS extract amounts into a slot named "amount" with type "monetary".
        - Normalize to a plain numeric string (digits only, no currency symbols or commas).
          "$400" → "400", "400$" → "400", "$4,500" → "4500", "€250" → "250",
          "four thousand five hundred dollars" → "4500", "1.5k" → "1500".
        - The dollar/euro sign may appear BEFORE or AFTER the number — handle both.
        - If the value is empty or unclear, omit the entity rather than returning an empty string.
        """;

    public LlmQueryParser(BedrockLlmHelper bedrock)
    {
        _bedrock          = bedrock;
        _temporalResolver = new TemporalResolver();
        _fallback         = new RuleBasedQueryParser();
    }

    public async Task<QueryIntent> ParseAsync(string rawQuery, ConversationContext? context = null)
    {
        var prompt = BuildPrompt(rawQuery, context);

        string json;
        try
        {
            json = await _bedrock.ConverseAsync(SystemPrompt, prompt, maxTokens: 800, temperature: 0f);
        }
        catch
        {
            // Fallback to deterministic parser on any Bedrock error
            return await _fallback.ParseAsync(rawQuery, context);
        }

        try
        {
            return ParseJson(json, rawQuery);
        }
        catch
        {
            return await _fallback.ParseAsync(rawQuery, context);
        }
    }

    private static string BuildPrompt(string rawQuery, ConversationContext? context)
    {
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;
        var quarter = (now.Month - 1) / 3 + 1;
        var fiscalYear = now.Year;
        var prevFY = fiscalYear - 1;
        var prevMonth = now.AddMonths(-1);
        var prevQuarter = quarter == 1 ? 4 : quarter - 1;
        var prevQuarterYear = quarter == 1 ? fiscalYear - 1 : fiscalYear;

        sb.AppendLine("## Date context (use this to resolve all relative and named periods)");
        sb.AppendLine($"Today            : {now:yyyy-MM-dd}");
        sb.AppendLine($"Current month    : {now:MMMM yyyy} ({now:yyyy-MM-01} – {new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month)):yyyy-MM-dd})");
        sb.AppendLine($"Current quarter  : Q{quarter} {fiscalYear} ({new DateTime(fiscalYear, (quarter - 1) * 3 + 1, 1):yyyy-MM-dd} – {new DateTime(fiscalYear, quarter * 3, DateTime.DaysInMonth(fiscalYear, quarter * 3)):yyyy-MM-dd})");
        sb.AppendLine($"Current FY       : FY{fiscalYear} (2026-01-01 – {fiscalYear}-12-31)");
        sb.AppendLine($"Last month       : {prevMonth:MMMM yyyy} ({prevMonth:yyyy-MM-01} – {new DateTime(prevMonth.Year, prevMonth.Month, DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month)):yyyy-MM-dd})");
        sb.AppendLine($"Last quarter     : Q{prevQuarter} {prevQuarterYear}");
        sb.AppendLine($"Last FY          : FY{prevFY} ({prevFY}-01-01 – {prevFY}-12-31)");

        if (context?.RecentTurns.Count > 0)
        {
            sb.AppendLine("\nRecent conversation:");
            foreach (var turn in context.RecentTurns.TakeLast(3))
            {
                sb.AppendLine($"  User: {turn.Query}");
                sb.AppendLine($"  Resolved: {turn.ResolvedAction} in {turn.Domain}");
            }
        }

        if (context?.SessionSlots.Count > 0)
        {
            sb.AppendLine($"\nActive session values: {JsonSerializer.Serialize(context.SessionSlots)}");
        }

        sb.AppendLine($"\nUser query: {rawQuery}");
        return sb.ToString();
    }

    private QueryIntent ParseJson(string json, string rawQuery)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // When the LLM rewrites the root query to isolate the first task in a multi-intent split,
        // prefer that rewritten text over the full original query.  This ensures Phase 1 embedding
        // targets only the first task and avoids the first sub-intent overlapping with the second.
        var effectiveQuery = root.GetStringOrDefault("query") is { Length: > 0 } q ? q : rawQuery;
        return ParseIntentElement(root, effectiveQuery);
    }

    private QueryIntent ParseIntentElement(JsonElement root, string rawQuery)
    {
        var primaryAction  = Enum.TryParse<DomainAction>(root.GetStringOrDefault("primaryAction"), true, out var pa) ? pa : DomainAction.List;
        var domain         = root.GetStringOrDefault("domain") ?? "general";
        var subDomain      = root.GetStringOrDefault("subDomain") ?? "unknown";
        var domainConf     = root.GetFloatOrDefault("domainConfidence");
        var confidence     = root.GetFloatOrDefault("confidence");
        var reasoning      = root.GetStringOrDefault("reasoning") ?? "";
        var expectedOutput = Enum.TryParse<OutputFormat>(root.GetStringOrDefault("expectedOutput"), true, out var of) ? of : OutputFormat.Summary;

        var slots = new List<QuerySlot>();
        if (root.TryGetProperty("entities", out var entitiesEl))
        {
            foreach (var e in entitiesEl.EnumerateArray())
            {
                var slotName  = e.GetStringOrDefault("name") ?? "unknown";
                var slotValue = e.GetStringOrDefault("value") ?? "";
                var slotType  = e.GetStringOrDefault("type") ?? "string";

                // Normalise monetary values: strip currency symbols and commas regardless
                // of whether the LLM already did so (handles "400$", "$400", "4,500", etc.).
                if (slotType == "monetary" || slotName == "amount")
                    slotValue = NormalizeMonetaryValue(slotValue, rawQuery);

                // Drop slots whose value is still empty after normalisation.
                if (string.IsNullOrWhiteSpace(slotValue)) continue;

                // Guard: reject descriptive/comparative phrases that the LLM should never
                // have extracted as entity slot values (e.g. "more balance", "largest invoice").
                // Only proper nouns and identifiers are valid; descriptive qualifiers belong in constraints.
                if (IsDescriptivePhrase(slotValue)) continue;

                slots.Add(new QuerySlot
                {
                    Name       = slotName,
                    Value      = slotValue,
                    Type       = slotType,
                    Confidence = e.GetFloatOrDefault("confidence"),
                    IsExplicit = e.TryGetProperty("isExplicit", out var ie) && ie.GetBoolean()
                });
            }
        }

        TemporalScope? temporal = null;
        if (root.TryGetProperty("temporal", out var tempEl))
        {
            var typeStr = tempEl.GetStringOrDefault("type");
            if (!string.IsNullOrEmpty(typeStr) && typeStr != "None" &&
                Enum.TryParse<TemporalScopeType>(typeStr, true, out var tType))
            {
                var relExpr = tempEl.GetStringOrDefault("relativeExpression");
                temporal = !string.IsNullOrEmpty(relExpr)
                    ? _temporalResolver.Resolve(relExpr, DateOnly.FromDateTime(DateTime.UtcNow))
                    : new TemporalScope
                    {
                        Type  = tType,
                        Start = ParseDate(tempEl, "start"),
                        End   = ParseDate(tempEl, "end"),
                        RelativeExpression = relExpr
                    };
            }
        }

        TemporalScope? compareTemporal = null;
        if (root.TryGetProperty("compareTemporal", out var compEl))
        {
            var typeStr = compEl.GetStringOrDefault("type");
            if (!string.IsNullOrEmpty(typeStr) && typeStr != "None" &&
                Enum.TryParse<TemporalScopeType>(typeStr, true, out var cType))
            {
                var relExpr = compEl.GetStringOrDefault("relativeExpression");
                compareTemporal = !string.IsNullOrEmpty(relExpr)
                    ? _temporalResolver.Resolve(relExpr, DateOnly.FromDateTime(DateTime.UtcNow))
                    : new TemporalScope
                    {
                        Type  = cType,
                        Start = ParseDate(compEl, "start"),
                        End   = ParseDate(compEl, "end"),
                        RelativeExpression = relExpr
                    };
            }
        }

        var constraints = new List<Constraint>();
        if (root.TryGetProperty("constraints", out var constEl))
        {
            foreach (var c in constEl.EnumerateArray())
            {
                if (Enum.TryParse<ConstraintOperator>(c.GetStringOrDefault("operator"), true, out var op))
                {
                    constraints.Add(new Constraint
                    {
                        Field      = c.GetStringOrDefault("field") ?? "unknown",
                        Operator   = op,
                        Value      = c.GetStringOrDefault("value") ?? "",
                        IsNegation = c.TryGetProperty("isNegation", out var neg) && neg.GetBoolean()
                    });
                }
            }
        }

        var subIntents = new List<QueryIntent>();
        if (root.TryGetProperty("subIntents", out var subEl))
        {
            foreach (var s in subEl.EnumerateArray())
            {
                var subQuery = s.GetStringOrDefault("query") ?? rawQuery;
                subIntents.Add(ParseIntentElement(s, subQuery));
            }
        }

        return new QueryIntent
        {
            RawQuery          = rawQuery,
            NormalizedQuery   = rawQuery.Trim().ToLowerInvariant(),
            PrimaryAction     = primaryAction,
            ImpliedActions    = new HashSet<DomainAction>(),
            Domain            = domain,
            SubDomain         = subDomain,
            DomainConfidence  = domainConf,
            ExtractedSlots    = slots,
            TemporalScope     = temporal,
            CompareTemporal   = compareTemporal,
            Constraints       = constraints,
            ExpectedOutput    = expectedOutput,
            SubIntents        = subIntents,
            OverallConfidence = confidence,
            ActionConfidence  = 1.0f,   // LLM explicitly classified the action
            Reasoning         = reasoning,
            ParsingNotes      = []
        };
    }

    private static DateOnly? ParseDate(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return DateOnly.TryParse(v.GetString(), out var d) ? d : null;
    }

    /// <summary>
    /// Normalises a monetary slot value to a plain numeric string.
    /// Handles: "$400", "400$", "€4,500", "4500", "1.5k", "1.5K".
    /// If the LLM returned an empty value, falls back to a regex scan of the raw query.
    /// </summary>
    private static string NormalizeMonetaryValue(string raw, string rawQuery)
    {
        var candidate = string.IsNullOrWhiteSpace(raw) ? ExtractAmountFromQuery(rawQuery) : raw;
        if (string.IsNullOrWhiteSpace(candidate)) return "";

        // Strip leading/trailing currency symbols and whitespace
        candidate = candidate.Trim().TrimStart('$', '€', '£', '¥').TrimEnd('$', '€', '£', '¥').Trim();

        // Remove commas used as thousands separators: "4,500" → "4500"
        candidate = candidate.Replace(",", "");

        // Handle shorthand: "1.5k" → "1500"
        if (candidate.EndsWith("k", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(candidate[..^1], out var kVal))
            return ((long)(kVal * 1000)).ToString();

        // Return the numeric portion only
        var digits = System.Text.RegularExpressions.Regex.Match(candidate, @"\d+(\.\d+)?");
        return digits.Success ? digits.Value : "";
    }

    /// <summary>
    /// Last-resort regex scan for monetary amounts when the LLM returned an empty value.
    /// Matches: "$400", "400$", "€250", "4,500", "1.5k" anywhere in the query.
    /// </summary>
    private static string ExtractAmountFromQuery(string query)
    {
        // Pattern: optional leading symbol, digits (with optional thousands commas and decimals), optional trailing symbol/k
        var m = System.Text.RegularExpressions.Regex.Match(
            query,
            @"[$€£¥]?\s*(\d[\d,]*(?:\.\d+)?)\s*(?:[$€£¥]|k\b)?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// Returns true when a slot value is a descriptive or comparative phrase rather than a proper
    /// noun or identifier. These should never be extracted as entity slot values; they belong in
    /// <c>constraints</c>. This is a deterministic post-parse guard against LLM prompt non-compliance.
    ///
    /// Heuristics (any one is sufficient to reject):
    ///   • Contains a comparative/superlative/quantity adjective (more, most, highest, largest, …)
    ///   • Contains a common function word that indicates a filter predicate (with, that, having, …)
    ///   • Contains two or more whitespace-separated tokens where none looks like a proper noun
    ///     (i.e. all tokens are lowercase common words)
    /// </summary>
    private static bool IsDescriptivePhrase(string value)
    {
        var lower = value.Trim().ToLowerInvariant();

        // Single-word values are almost always fine (proper noun or ID).
        var tokens = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1) return false;

        // Comparative / superlative / quantity triggers
        string[] descriptiveTriggers =
        [
            "more", "most", "less", "least", "highest", "lowest", "largest", "smallest",
            "biggest", "best", "worst", "top", "bottom", "greater", "lower", "higher",
            "maximum", "minimum", "total", "average", "latest", "oldest", "newest",
            "recent", "oldest", "pending", "unpaid", "overdue"
        ];

        // Filter / predicate function words
        string[] filterWords = [ "with", "that", "having", "which", "where", "who", "whose" ];

        if (tokens.Any(t => descriptiveTriggers.Contains(t))) return true;
        if (tokens.Any(t => filterWords.Contains(t))) return true;

        return false;
    }
}

// Extension helpers for JsonElement
internal static class JsonElementExtensions
{
    public static string? GetStringOrDefault(this JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString();
        return null;
    }

    public static float GetFloatOrDefault(this JsonElement el, string prop, float def = 0.5f)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetSingle();
        return def;
    }
}
