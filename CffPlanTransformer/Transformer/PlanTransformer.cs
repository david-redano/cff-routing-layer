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
        sb.AppendLine($"domain: {DeriveYamlDomain(agentId)}");
        sb.AppendLine($"displayName: {displayName}");

        // Normalized understanding replaces concrete slot values with ${slot} placeholders
        // so that embeddings generalise across parameter variations (e.g. "last 30 days"
        // and "last 200 days" produce the same vector as "last ${period}").
        var normUnderstanding = NormalizeUnderstanding(plan.Understanding);

        // Approach: strip tool-name tokens and step-type phrases; keep only the
        // conceptual computation description for richer embedding signal.
        var normDescription = string.IsNullOrWhiteSpace(plan.Approach)
            ? ""
            : StripApproach(plan.Approach);

        sb.AppendLine("capabilities:");
        foreach (var cap in capabilities)
            sb.AppendLine($"  - {cap}");

        sb.AppendLine($"understanding: \"{EscapeYaml(normUnderstanding)}\"");
        if (!string.IsNullOrWhiteSpace(normDescription))
            sb.AppendLine($"description: \"{EscapeYaml(normDescription)}\"");

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
        sb.AppendLine($"  - \"{EscapeYaml(normUnderstanding)}\"");
        sb.AppendLine($"  - \"{EscapeYaml(GenerateAltQuery(normUnderstanding))}\"");

        sb.AppendLine("steps:");

        // Determine which step is the final output step (no nextStep pointer)
        var finalStepId = plan.Steps
            .Where(s => !s.NextStep.HasValue)
            .Select(s => (int?)s.Id)
            .FirstOrDefault() ?? (plan.Steps.Count > 0 ? plan.Steps.Max(s => s.Id) : -1);

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

            // outputFields — what this step produces for downstream steps
            var outputFields = InferStepOutputFields(step, step.Id == finalStepId ? plan.SummaryFields : []);
            sb.AppendLine($"    outputFields: [{string.Join(", ", outputFields)}]");

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

    // ── Understanding normalization ──────────────────────────────────────────
    // Replace concrete slot values with ${slot} placeholders so embeddings
    // generalise across parameter variations (e.g. "last 30 days" and
    // "last 200 days" both become "last ${period}").

    // Relative period phrases — matched before bare years to avoid partial matches
    private static readonly Regex RxPeriodInText = new(
        @"\b(last\s+\d+\s+(?:days?|months?|weeks?|years?)" +
        @"|this\s+month|last\s+month" +
        @"|year[\s\-]to[\s\-]date|ytd" +
        @"|last\s+quarter|this\s+quarter" +
        @"|last\s+year|this\s+year|next\s+year" +
        @"|last\s+fiscal\s+year|this\s+fiscal\s+year" +
        @"|Q[1-4]\s+\d{4}" +
        @"|current\s+(?:financial\s+)?year)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Named-month comparisons: "between March and April", "in March", "during April", "from March to June"
    private static readonly Regex RxMonthPeriod = new(
        @"\b(between\s+(?:MONTHS)\s+and\s+(?:MONTHS)|(?:during|in)\s+(?:MONTHS)|from\s+(?:MONTHS)\s+to\s+(?:MONTHS))\b"
            .Replace("MONTHS", @"(?:January|February|March|April|May|June|July|August|September|October|November|December)"),
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Natural-language dates: "January 15", "16 April", "April 16 2024", "15th January"
    private static readonly Regex RxNaturalDate = new(
        @"\b(?:(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2}(?:st|nd|rd|th)?(?:,\s*\d{4})?|\d{1,2}(?:st|nd|rd|th)?\s+(?:January|February|March|April|May|June|July|August|September|October|November|December)(?:\s+\d{4})?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Specific ISO dates (YYYY-MM-DD)
    private static readonly Regex RxDateInText = new(
        @"\b\d{4}-\d{2}-\d{2}\b",
        RegexOptions.Compiled);

    // Bare fiscal/calendar years not inside a period phrase (e.g. "FY2024", "2024")
    private static readonly Regex RxYearInText = new(
        @"\b(?:FY)?(20\d{2})\b",
        RegexOptions.Compiled);

    // Customer names after contextual keywords ("for customer X", "belonging to X", "for X")
    // Matches a title-cased name (2–5 capitalised words, possibly with "&" or "and")
    private static readonly Regex RxCustomerName = new(
        @"(?<=\b(?:for\s+customer|customer\s+named?|belonging\s+to|for)\s+)" +
        @"([A-Z][A-Za-z]+(?:\s+(?:[A-Z][A-Za-z]+|&|and))+)",
        RegexOptions.Compiled);

    // Order / invoice / quote reference numbers.
    // The captured token MUST look like a reference code (pure digits, or uppercase+digits
    // like INV-001) so common English words ("date", "details", "value") are never matched.
    private static readonly Regex RxRefNumber = new(
        @"\b(?:order|invoice|quote|ref(?:erence)?)\s+(?:number\s+)?(\d+[A-Z0-9\-]*|[A-Z]{2,}\d+[\w\-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Product / item names after "ordered" followed by a frequency/ranking qualifier.
    // Allows leading lowercase so "iPhones", "iPads" etc. are caught.
    private static readonly Regex RxProductName = new(
        @"(?<=\bordered\s+)([A-Za-z][A-Za-z0-9\+®™]{3,20})(?=\s+(?:most|frequently|more|fewer|less|often))",
        RegexOptions.Compiled);

    // Monetary amounts — $, €, £, ¥ prefixes and optional M/K suffix
    private static readonly Regex RxAmountInText = new(
        @"[\$€£¥][\d,]+(?:\.\d+)?(?:[MmKk])?",
        RegexOptions.Compiled);

    // ── Approach stripping ───────────────────────────────────────────────────
    // Removes implementation noise from the Approach field while keeping
    // the conceptual computation description useful for semantic search.

    // snake_case tool names after "via" (e.g. "via sales_list_customers")
    private static readonly Regex RxViaTool = new(
        @"\bvia\s+[a-z][a-z0-9]*(?:_[a-z0-9]+)+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "in a Code step", "in a Code step", "in Code step", "a Code step"
    private static readonly Regex RxCodeStep = new(
        @"\bin\s+a?\s*Code\s+step\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Bare snake_case identifiers that look like tool names (≥2 underscores)
    // e.g. accounting_get_trial_balance but NOT "sort_descending" (kept for context)
    private static readonly Regex RxToolName = new(
        @"\b[a-z][a-z0-9]*(?:_[a-z0-9]+){2,}\b",
        RegexOptions.Compiled);

    private static string StripApproach(string approach)
    {
        var text = approach;
        text = RxViaTool.Replace(text, "");
        text = RxCodeStep.Replace(text, "");
        text = RxToolName.Replace(text, "");
        // Collapse multiple spaces and clean up punctuation artefacts like ", ," or "( )"
        text = Regex.Replace(text, @"\(\s*\)", "");
        text = Regex.Replace(text, @",\s*,", ",");
        text = Regex.Replace(text, @"\s{2,}", " ");
        return NormalizeUnderstanding(text.Trim(' ', ',', '.'));
    }

    private static string NormalizeUnderstanding(string understanding)
    {
        var text = understanding;

        // Period phrases (relative windows first, then named-month comparisons)
        text = RxPeriodInText.Replace(text, "${period}");
        text = RxMonthPeriod.Replace(text, "${period}");

        // Dates (natural language before ISO so "January 15" is caught first)
        text = RxNaturalDate.Replace(text, "${date}");
        text = RxDateInText.Replace(text, "${date}");

        // Bare years (after period/date replacements to avoid double-matching)
        text = RxYearInText.Replace(text, "${year}");

        // Entity slot values
        text = RxAmountInText.Replace(text, "${amount}");
        text = RxCustomerName.Replace(text, "${customer}");
        text = RxRefNumber.Replace(text, m =>
            m.Value.StartsWith("order", StringComparison.OrdinalIgnoreCase)  ? "order ${orderId}" :
            m.Value.StartsWith("invoice", StringComparison.OrdinalIgnoreCase) ? "invoice ${invoiceId}" :
            m.Value.StartsWith("quote", StringComparison.OrdinalIgnoreCase)   ? "quote ${quoteId}" :
            "ref ${refId}");
        text = RxProductName.Replace(text, "${itemDescription}");

        return text;
    }

    // Leading action verbs stripped before building the alt query
    private static readonly Regex RxLeadingVerb = new(        @"^(Calculate|Identify|Compare|Retrieve|List|Fetch|Find|Show|Get|Determine|Count|Analyse|Analyze|Generate)\s+(the\s+)?(which\s+)?",
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

    // ── Domain mapping ──────────────────────────────────────────────────────

    /// <summary>
    /// Maps the derived agentId back to the canonical domain used by PlanFeatureExtractor.
    /// </summary>
    private static string DeriveYamlDomain(string agentId) =>
        agentId switch
        {
            "SalesAgent"      => "sales",
            "PurchasesAgent"  => "sales",
            "InventoryAgent"  => "inventory",
            "AccountingAgent" => "finance",
            "AnalysisAgent"   => "finance",
            _                 => "finance"   // FinancialAgent and any future agents
        };

    // ── Step output field inference ─────────────────────────────────────────

    /// <summary>
    /// Infers the output field names a step produces.
    /// <list type="bullet">
    ///   <item>Final output step → summary fields from ExpectedData (camelCased).</item>
    ///   <item>Tool step        → entity name derived from the tool name (camelCased).</item>
    ///   <item>Code step (intermediate) → generic sentinel <c>step{id}Result</c>.</item>
    /// </list>
    /// </summary>
    private static List<string> InferStepOutputFields(ResponseStep step, List<string> summaryFields)
    {
        // Final output step: emit summary field names (camelCased)
        if (summaryFields.Count > 0)
            return summaryFields.Select(ToCamelCase).ToList();

        // Tool step: infer entity from tool name (e.g. sales_list_sales_orders → salesOrders)
        if (string.Equals(step.Type, "Tool", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(step.ToolName))
        {
            return [InferToolOutputEntity(step.ToolName, step.Id)];
        }

        // Code (intermediate) or unknown
        return [$"step{step.Id}Result"];
    }

    /// <summary>
    /// Derives the primary entity name a tool step produces.
    /// Rule: strip domain prefix and action verb, join remaining parts in camelCase.
    /// Examples:
    ///   sales_list_sales_orders → salesOrders
    ///   analysis_aggregate_entity → aggregateResult
    ///   accounting_list_invoices → invoices
    /// </summary>
    private static string InferToolOutputEntity(string toolName, int stepId)
    {
        var parts = toolName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return $"step{stepId}Data";

        // Drop the domain prefix (first part)
        var afterDomain = parts.Skip(1).ToArray();

        // Drop the action verb (list, get, fetch, create, update, delete, aggregate, etc.)
        var verbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "list", "get", "fetch", "create", "update", "delete", "aggregate", "compute", "find", "search" };

        var entityParts = afterDomain.SkipWhile(p => verbs.Contains(p)).ToArray();

        if (entityParts.Length == 0)
        {
            // Verb-only tool (e.g. analysis_aggregate) → generic result
            return $"step{stepId}Data";
        }

        // Deduplicate repeated domain prefix in entity parts
        // (e.g. sales_list_sales_orders: domain=sales, entity parts=["sales","orders"] → drop leading repeat)
        var domainPart = parts[0];
        if (entityParts[0].Equals(domainPart, StringComparison.OrdinalIgnoreCase) && entityParts.Length > 1)
            entityParts = entityParts.Skip(1).ToArray();

        // Build camelCase: first part lowercase, subsequent parts title-cased
        var camel = new StringBuilder(entityParts[0].ToLowerInvariant());
        foreach (var p in entityParts.Skip(1))
            camel.Append(char.ToUpperInvariant(p[0])).Append(p[1..].ToLowerInvariant());
        return camel.ToString();
    }

    private static string ToCamelCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return char.ToLowerInvariant(s[0]) + s[1..];
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
