// Understanding/LlmQueryParser.cs
namespace CffRoutingLayerDemo.Understanding;

using System.Text;
using System.Text.Json;
using CffRoutingLayerDemo.Bedrock;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// LLM-backed query parser using Amazon Bedrock (Claude).
/// This is the only place in Layer 1 where a network call occurs.
/// Extracts structured QueryIntent from free-text user input.
/// </summary>
public sealed class LlmQueryParser : IQueryParser
{
    private readonly BedrockLlmHelper _bedrock;
    private readonly TemporalResolver _temporalResolver;
    private readonly RuleBasedQueryParser _fallback;

    private const string SystemPrompt = """
        You are a query understanding system for a financial/accounting application.

        Given a user query, extract the following as JSON (respond ONLY with valid JSON, no markdown):

        {
          "primaryAction": "List|Create|Compute|Compare|Forecast|Audit|Categorize|Optimize",
          "domain": "sales|finance|inventory|hr|general",
          "subDomain": "cashflow|invoicing|reconciliation|tax|reporting|forecasting|orders|crm|stock|payroll|audit|unknown",
          "domainConfidence": 0.0,
          "entities": [
            { "name": "period", "value": "last month", "type": "temporal", "confidence": 0.9, "isExplicit": true }
          ],
          "temporal": {
            "type": "None|PointInTime|BoundedPeriod|RelativeWindow|OpenEnded",
            "start": null,
            "end": null,
            "relativeExpression": null
          },
          "constraints": [],
          "expectedOutput": "Summary|DetailedList|Comparison|Forecast|Narrative",
          "confidence": 0.0
        }

        Rules:
        - If the query is about multiple unrelated tasks, set confidence < 0.4 and note in entities.
        - Omit fields you cannot determine — do not guess.
        - For temporal: resolve relative dates against today's date provided in the prompt.
        - NEVER return markdown fences or explanatory text — JSON only.
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
            json = await _bedrock.ConverseAsync(SystemPrompt, prompt, maxTokens: 400, temperature: 0f);
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
        sb.AppendLine($"Today's date: {DateTime.UtcNow:yyyy-MM-dd}");

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

        var primaryAction  = Enum.TryParse<DomainAction>(root.GetStringOrDefault("primaryAction"), true, out var pa) ? pa : DomainAction.List;
        var domain         = root.GetStringOrDefault("domain") ?? "general";
        var subDomain      = root.GetStringOrDefault("subDomain") ?? "unknown";
        var domainConf     = root.GetFloatOrDefault("domainConfidence");
        var confidence     = root.GetFloatOrDefault("confidence");
        var expectedOutput = Enum.TryParse<OutputFormat>(root.GetStringOrDefault("expectedOutput"), true, out var of) ? of : OutputFormat.Summary;

        var slots = new List<QuerySlot>();
        if (root.TryGetProperty("entities", out var entitiesEl))
        {
            foreach (var e in entitiesEl.EnumerateArray())
            {
                slots.Add(new QuerySlot
                {
                    Name       = e.GetStringOrDefault("name") ?? "unknown",
                    Value      = e.GetStringOrDefault("value") ?? "",
                    Type       = e.GetStringOrDefault("type") ?? "string",
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
            Constraints       = constraints,
            ExpectedOutput    = expectedOutput,
            SubIntents        = [],
            OverallConfidence = confidence,
            ParsingNotes      = []
        };
    }

    private static DateOnly? ParseDate(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        return DateOnly.TryParse(v.GetString(), out var d) ? d : null;
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
