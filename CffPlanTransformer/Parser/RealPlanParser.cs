// CffPlanTransformer – Parser for real copilot-engine plan files
namespace CffPlanTransformer.Parser;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Parses a single <c>response_NNN.txt</c> file into a <see cref="RealPlan"/>.
///
/// Each file has two sections:
///   • <Reasoning> … </Reasoning>  — custom YAML-like metadata
///   • <Response>  … </Response>   — XML wrapper around JSON <Step> blocks
/// </summary>
internal static class RealPlanParser
{
    // ── Reasoning regex patterns ────────────────────────────────────────────

    private static readonly Regex RxCategory    = new(@"Request:\s*\{Cat:\s*(\w+)", RegexOptions.Compiled);
    private static readonly Regex RxUnderstand  = new(@"Understanding:\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex RxLayout      = new(@"Layout:\s*\{Type:\s*([^,}]+)", RegexOptions.Compiled);
    // Matches: Step1: {Name: some_tool_name, Id: "uuid"}
    private static readonly Regex RxTool        = new(@"(Step\d+):\s*\{Name:\s*([\w]+),\s*Id:\s*""([^""]+)""\}", RegexOptions.Compiled);
    // Summary: [Field1, Field2, ...]
    private static readonly Regex RxSummary     = new(@"Summary:\s*\[([^\]]+)\]", RegexOptions.Compiled);
    // RequiredParams block: grab everything until next top-level key
    private static readonly Regex RxReqParams   = new(@"RequiredParams:(.*?)(?=\n\w|\n  \w[^:]*:|\z)", RegexOptions.Compiled | RegexOptions.Singleline);
    // Individual step param lines: Step1: {key: "val", key2: "val2"}
    private static readonly Regex RxStepParams  = new(@"(Step\d+):\s*\{([^}]*)\}", RegexOptions.Compiled);
    // key: "value" pairs inside param braces
    private static readonly Regex RxKvPair      = new(@"([\w_]+):\s*""([^""]*?)""", RegexOptions.Compiled);

    // ── Response regex ──────────────────────────────────────────────────────
    private static readonly Regex RxReasoning   = new(@"<Reasoning>(.*?)</Reasoning>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex RxResponse    = new(@"<Response>(.*?)</Response>",   RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex RxStep        = new(@"<Step>(.*?)</Step>",           RegexOptions.Compiled | RegexOptions.Singleline);

    // ── Public entry point ──────────────────────────────────────────────────

    public static RealPlan Parse(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var plan    = new RealPlan();

        var reasoningMatch = RxReasoning.Match(content);
        if (reasoningMatch.Success)
            ParseReasoning(reasoningMatch.Groups[1].Value, plan);

        var responseMatch = RxResponse.Match(content);
        if (responseMatch.Success)
            ParseResponse(responseMatch.Groups[1].Value, plan);

        return plan;
    }

    // ── Reasoning parsing ───────────────────────────────────────────────────

    private static void ParseReasoning(string block, RealPlan plan)
    {
        var m = RxCategory.Match(block);
        if (m.Success) plan.Category = m.Groups[1].Value.Trim();

        m = RxUnderstand.Match(block);
        if (m.Success) plan.Understanding = m.Groups[1].Value.Trim();

        m = RxLayout.Match(block);
        if (m.Success) plan.Layout = m.Groups[1].Value.Trim();

        // Tools
        foreach (Match tm in RxTool.Matches(block))
            plan.Tools.Add(new ToolEntry
            {
                StepId = tm.Groups[1].Value,
                Name   = tm.Groups[2].Value,
                Id     = tm.Groups[3].Value
            });

        // Summary fields
        m = RxSummary.Match(block);
        if (m.Success)
        {
            foreach (var field in m.Groups[1].Value.Split(','))
            {
                var f = field.Trim();
                if (f.Length > 0) plan.SummaryFields.Add(f);
            }
        }

        // RequiredParams
        m = RxReqParams.Match(block);
        if (m.Success)
        {
            var reqBlock = m.Groups[1].Value;
            foreach (Match sm in RxStepParams.Matches(reqBlock))
            {
                var stepId  = sm.Groups[1].Value;
                var kvBlock = sm.Groups[2].Value;
                var dict    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match kv in RxKvPair.Matches(kvBlock))
                    dict[kv.Groups[1].Value] = kv.Groups[2].Value;
                plan.RequiredParams[stepId] = dict;
            }
        }
    }

    // ── Response / Step parsing ─────────────────────────────────────────────

    private static void ParseResponse(string block, RealPlan plan)
    {
        foreach (Match sm in RxStep.Matches(block))
        {
            var json = sm.Groups[1].Value.Trim();

            // Strip the embedded JS function body before parsing JSON –
            // it contains quote characters that break the parser.
            json = StripFunctionBody(json);

            try
            {
                using var doc  = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var step = new ResponseStep
                {
                    Id           = root.TryGetProperty("Id",           out var idEl)   ? idEl.GetInt32()             : 0,
                    Type         = root.TryGetProperty("Type",         out var tyEl)   ? tyEl.GetString() ?? ""      : "",
                    ToolId       = root.TryGetProperty("ToolId",       out var tiEl)   ? tiEl.GetString() ?? ""      : "",
                    ToolName     = root.TryGetProperty("ToolName",     out var tnEl)   ? tnEl.GetString() ?? ""      : "",
                    FunctionName = root.TryGetProperty("FunctionName", out var fnEl)   ? fnEl.GetString() ?? ""      : "",
                    StepReason   = root.TryGetProperty("StepReason",   out var srEl)   ? srEl.GetString() ?? ""      : "",
                    NextStep     = root.TryGetProperty("NextStep",     out var nsEl) &&
                                   nsEl.ValueKind == JsonValueKind.Number               ? nsEl.GetInt32()            : null,
                };

                if (root.TryGetProperty("Input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in inputEl.EnumerateArray())
                    {
                        var key = entry.TryGetProperty("Key",   out var kEl) ? kEl.GetString() ?? "" : "";
                        var val = entry.TryGetProperty("Value", out var vEl) ? vEl.GetString() ?? "" : "";
                        if (key.Length > 0) step.Input[key] = val;
                    }
                }

                plan.Steps.Add(step);
            }
            catch
            {
                // Skip malformed step blocks gracefully
            }
        }
    }

    /// <summary>
    /// Replaces the value of the "Function" JSON key with an empty string so
    /// the surrounding JSON remains parseable. The embedded JS is not needed
    /// for YAML generation.
    /// </summary>
    private static string StripFunctionBody(string json)
    {
        // Remove "Function": "...escaped JS..." — the value is a JSON string
        // that may contain nested escaped quotes.  We find the key, then skip
        // the entire string value by counting escape sequences.
        var funcKey = "\"Function\"";
        var keyIdx  = json.IndexOf(funcKey, StringComparison.Ordinal);
        if (keyIdx < 0) return json;

        // Advance past the key and colon
        var colonIdx = json.IndexOf(':', keyIdx + funcKey.Length);
        if (colonIdx < 0) return json;

        // Find the opening quote of the value
        var quoteStart = json.IndexOf('"', colonIdx + 1);
        if (quoteStart < 0) return json;

        // Walk the string to find the matching closing quote
        var i = quoteStart + 1;
        while (i < json.Length)
        {
            if (json[i] == '\\') { i += 2; continue; }   // skip escape sequence
            if (json[i] == '"')  { break; }
            i++;
        }
        // i is now at the closing quote
        return json[..quoteStart] + "\"\"" + json[(i + 1)..];
    }
}
