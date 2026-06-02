// CffPlanTransformer – Transforms a RealPlan into a routing-demo YAML string
namespace CffPlanTransformer.Transformer;

using System.Text;
using System.Text.RegularExpressions;
using CffPlanTransformer.Parser;

internal static class PlanTransformer
{
    // Words ignored when building the PascalCase intent name
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a","an","the","of","for","from","with","by","in","at","on","to","is","are",
        "all","across","but","who","whom","have","has","which","that","this","been",
        "where","how","what","when","them","their","via","then","and","or","not",
        "very","per","each","its","into","than","more","some","any","one","two",
        "both","also","about","user","wants","know","data","available",
        "least","most","highest","lowest","best","worst","few","many","high","low",
        "above","below","between","first","last","next","top","bottom","given",
        "across","number","amount","value","values","based","using","used"
    };

    /// <summary>Returns the intent name that would be generated for <paramref name="plan"/>.</summary>
    public static string GetIntent(RealPlan plan) => GenerateIntent(plan.Understanding);

    /// <summary>
    /// Returns a structural fingerprint for deduplication.
    /// Two plans are considered identical when they have the same Understanding and the same
    /// step structure (step type + tool/function name + input parameter <em>keys</em>).
    /// Parameter <em>values</em> (e.g. date ranges) are intentionally excluded so that plans
    /// whose only difference is a concrete date are treated as duplicates.
    /// </summary>
    public static string GetFingerprint(RealPlan plan)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(plan.Understanding.Trim().ToLowerInvariant());

        foreach (var step in plan.Steps.OrderBy(s => s.Id))
        {
            sb.Append('|');
            sb.Append(step.Type.ToLowerInvariant());
            sb.Append(':');
            sb.Append(step.Type.Equals("Tool", StringComparison.OrdinalIgnoreCase)
                ? step.ToolName
                : step.FunctionName);
            sb.Append(':');
            // Key names only — not values
            sb.Append(string.Join(",", step.Input.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Converts a parsed <see cref="RealPlan"/> into a routing-demo YAML string.
    /// </summary>
    /// <param name="plan">The parsed plan.</param>
    /// <param name="fileIndex">1-based number derived from the source filename (e.g. 1 for response_001.txt).</param>
    public static string Transform(RealPlan plan, int fileIndex)
    {
        var intent      = GenerateIntent(plan.Understanding);
        var agentId     = DeriveAgentId(plan.Tools);
        var displayName = DeriveDisplayName(agentId);
        var capabilities = DeriveCapabilities(plan.Tools);
        var planId      = $"real-{fileIndex:D3}";

        // Collect date range from first tool step that has date params
        var (dateFrom, dateTo) = ExtractDateRange(plan);

        // Compute DependsOn for each step from the NextStep chain
        var dependsOn = ComputeDependsOn(plan.Steps);

        var sb = new StringBuilder();

        sb.AppendLine($"planId: {planId}");
        sb.AppendLine($"intent: {intent}");
        sb.AppendLine($"agentId: {agentId}");
        sb.AppendLine($"displayName: {displayName}");
        sb.AppendLine($"description: \"{EscapeYaml(plan.Understanding)}\"");

        sb.AppendLine("capabilities:");
        foreach (var cap in capabilities)
            sb.AppendLine($"  - {cap}");

        sb.AppendLine($"understanding: \"{EscapeYaml(plan.Understanding)}\"");

        sb.AppendLine("expectedData:");
        sb.AppendLine("  summary:");
        if (plan.SummaryFields.Count > 0)
            foreach (var f in plan.SummaryFields)
                sb.AppendLine($"    - {f}");
        else
            sb.AppendLine("    - Result");

        sb.AppendLine("defaultEntities:");
        sb.AppendLine("  companyId: DEMO-001");
        if (!string.IsNullOrEmpty(dateFrom))
            sb.AppendLine($"  dateFrom: \"{dateFrom}\"");
        if (!string.IsNullOrEmpty(dateTo))
            sb.AppendLine($"  dateTo: \"{dateTo}\"");

        sb.AppendLine("sampleQueries:");
        sb.AppendLine($"  - \"{EscapeYaml(plan.Understanding)}\"");
        sb.AppendLine($"  - \"{EscapeYaml(GenerateAltQuery(plan.Understanding))}\"");

        sb.AppendLine("steps:");
        foreach (var step in plan.Steps)
        {
            sb.AppendLine($"  - id: {step.Id}");

            if (string.Equals(step.Type, "Tool", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"    action: {Slugify(step.ToolName)}");
                sb.AppendLine($"    stepType: Tool");
                sb.AppendLine($"    toolName: {step.ToolName}");
                if (!string.IsNullOrEmpty(step.ToolId))
                    sb.AppendLine($"    toolId: \"{step.ToolId}\"");
                if (!string.IsNullOrEmpty(step.StepReason))
                    sb.AppendLine($"    stepReason: \"{EscapeYaml(step.StepReason)}\"");
            }
            else
            {
                sb.AppendLine($"    action: compute-{Slugify(step.StepReason)}");
                sb.AppendLine($"    stepType: Code");
                if (!string.IsNullOrEmpty(step.FunctionName))
                    sb.AppendLine($"    functionName: {step.FunctionName}");
                if (!string.IsNullOrEmpty(step.StepReason))
                    sb.AppendLine($"    stepReason: \"{EscapeYaml(step.StepReason)}\"");
            }

            // DependsOn
            var deps = dependsOn.TryGetValue(step.Id, out var d) ? d : [];
            sb.AppendLine(deps.Count == 0
                ? "    dependsOn: []"
                : $"    dependsOn: [{string.Join(", ", deps)}]");

            // Input params (Tool steps)
            if (step.Input.Count > 0)
            {
                sb.AppendLine("    input:");
                foreach (var (k, v) in step.Input)
                {
                    sb.AppendLine($"      - key: {k}");
                    sb.AppendLine($"        value: \"{EscapeYaml(v)}\"");
                }
            }

            // NextStep (canonical chain hint)
            if (step.NextStep.HasValue)
                sb.AppendLine($"    nextStep: {step.NextStep}");
        }

        return sb.ToString();
    }

    // ── Intent generation ───────────────────────────────────────────────────

    private static string GenerateIntent(string understanding)
    {
        if (string.IsNullOrWhiteSpace(understanding)) return "ExecutePlan";

        var text = understanding;

        // Strip "User wants to [know]"
        text = Regex.Replace(text, @"^[Uu]ser\s+wants\s+to\s+(know\s+)?", "", RegexOptions.IgnoreCase);

        // Split on non-alphabetic characters
        var words = Regex.Split(text, @"[^a-zA-Z]+")
                         .Where(w => w.Length > 1 && !StopWords.Contains(w))
                         .Take(5)
                         .Select(w => char.ToUpper(w[0]) + w[1..].ToLower())
                         .ToArray();

        return words.Length > 0 ? string.Concat(words) : "ExecutePlan";
    }

    // Leading action verbs stripped before building the alt query
    private static readonly Regex RxLeadingVerb = new(
        @"^(Calculate|Identify|Compare|Retrieve|List|Fetch|Find|Show|Get|Determine|Count|Analyse|Analyze|Generate)\s+(the\s+)?(which\s+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string GenerateAltQuery(string understanding)
    {
        if (string.IsNullOrWhiteSpace(understanding)) return "Show me the report";

        // Strip leading verb, then build a natural-sounding query from key nouns
        var stripped = RxLeadingVerb.Replace(understanding, "").TrimStart();

        // Capitalise first letter
        var core = stripped.Length > 0
            ? char.ToUpper(stripped[0]) + stripped[1..]
            : understanding;

        // Trim to ~80 chars at a word boundary for brevity
        if (core.Length > 80)
        {
            var cut = core.LastIndexOf(' ', 80);
            core = cut > 0 ? core[..cut] + "?" : core[..80];
        }
        else if (!core.EndsWith('?') && !core.EndsWith('.'))
        {
            core += "?";
        }

        return core;
    }

    // ── AgentId / DisplayName ───────────────────────────────────────────────

    private static string DeriveAgentId(List<ToolEntry> tools)
    {
        var domain = DeriveToolDomain(tools);
        return domain switch
        {
            "sales"     => "SalesAgent",
            "purchases" => "PurchasesAgent",
            "accounting"=> "AccountingAgent",
            "analysis"  => "AnalysisAgent",
            "inventory" => "InventoryAgent",
            _           => "FinancialAgent"
        };
    }

    private static string DeriveDisplayName(string agentId) =>
        agentId switch
        {
            "SalesAgent"      => "Sales Agent",
            "PurchasesAgent"  => "Purchases Agent",
            "AccountingAgent" => "Accounting Agent",
            "AnalysisAgent"   => "Analysis Agent",
            "InventoryAgent"  => "Inventory Agent",
            _                 => "Financial Agent"
        };

    private static string DeriveToolDomain(List<ToolEntry> tools)
    {
        if (tools.Count == 0) return "financial";
        // Use the domain of the majority of tools
        var domains = tools.Select(t => t.Name.Split('_')[0]).ToList();
        return domains.GroupBy(d => d)
                      .OrderByDescending(g => g.Count())
                      .First().Key;
    }

    // ── Capabilities ────────────────────────────────────────────────────────

    private static List<string> DeriveCapabilities(List<ToolEntry> tools)
    {
        var caps = new LinkedHashSet<string>();
        foreach (var t in tools)
            caps.Add(ToolToCapability(t.Name));
        return [.. caps];
    }

    private static string ToolToCapability(string toolName)
    {
        var parts  = toolName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return toolName.Replace('_', '-');

        var domain = parts[0];
        // Filter out "list" and the domain word itself from the remaining parts
        var rest   = parts.Skip(1)
                          .Where(p => !p.Equals("list", StringComparison.OrdinalIgnoreCase) &&
                                      !p.Equals(domain, StringComparison.OrdinalIgnoreCase))
                          .ToArray();

        if (rest.Length == 0) return domain;

        // Special-case normalisation
        var entity = string.Join("-", rest);
        if (entity is "aggregate-entity" or "aggregate") entity = "aggregation";

        return $"{domain}-{entity}";
    }

    // ── Date range extraction ───────────────────────────────────────────────

    private static (string dateFrom, string dateTo) ExtractDateRange(RealPlan plan)
    {
        // Prefer the already-parsed Response step Input arrays (most reliable)
        foreach (var step in plan.Steps.Where(s =>
            string.Equals(s.Type, "Tool", StringComparison.OrdinalIgnoreCase)))
        {
            var from = step.Input.GetValueOrDefault("date_from")
                    ?? step.Input.GetValueOrDefault("date_range_from")
                    ?? "";
            var to   = step.Input.GetValueOrDefault("date_to")
                    ?? step.Input.GetValueOrDefault("date_range_to")
                    ?? "";
            if (!string.IsNullOrEmpty(from) || !string.IsNullOrEmpty(to))
                return (from, to);
        }

        // Fallback: RequiredParams from the Reasoning block
        foreach (var (_, d) in plan.RequiredParams)
        {
            var from = d.GetValueOrDefault("date_from")
                    ?? d.GetValueOrDefault("date_range_from")
                    ?? "";
            var to   = d.GetValueOrDefault("date_to")
                    ?? d.GetValueOrDefault("date_range_to")
                    ?? "";
            if (!string.IsNullOrEmpty(from) || !string.IsNullOrEmpty(to))
                return (from, to);
        }
        return ("", "");
    }

    // ── DependsOn from NextStep chain ───────────────────────────────────────

    /// <summary>
    /// Inverts the NextStep chain: for each step N, collects all step IDs
    /// whose NextStep == N and uses them as N's DependsOn list.
    /// </summary>
    private static Dictionary<int, List<int>> ComputeDependsOn(List<ResponseStep> steps)
    {
        var result = steps.ToDictionary(s => s.Id, _ => new List<int>());
        foreach (var step in steps)
        {
            if (step.NextStep.HasValue && result.ContainsKey(step.NextStep.Value))
                result[step.NextStep.Value].Add(step.Id);
        }
        return result;
    }

    // ── YAML helpers ────────────────────────────────────────────────────────

    private static string EscapeYaml(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Slugify(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "step";
        var slug = Regex.Replace(s.ToLower(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? slug : "step";
    }
}

/// <summary>Order-preserving hash set (insertion order, no duplicates).</summary>
file sealed class LinkedHashSet<T> : IEnumerable<T> where T : notnull
{
    private readonly Dictionary<T, LinkedListNode<T>> _map  = [];
    private readonly LinkedList<T>                    _list = new();

    public void Add(T item)
    {
        if (!_map.ContainsKey(item))
            _map[item] = _list.AddLast(item);
    }

    public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
