// Registry/AgentRegistry.cs
namespace CffRoutingLayerDemo.Registry;

using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;

/// <summary>
/// In-process agent registry — a flat dictionary of AgentId → AgentManifest.
///
/// Prefer <see cref="LoadFromPlans"/> which derives every agent from the YAML
/// plan files so no C# change is needed when adding a new intent/agent.
/// <see cref="BuildDefault"/> is kept for unit tests that run without a plans
/// directory.
/// </summary>

public sealed class AgentRegistry
{
    // Now supports multiple plans/intents per agent
    private readonly Dictionary<string, List<AgentManifest>> _agents;

    public AgentRegistry(IEnumerable<AgentManifest> agents)
    {
        _agents = new Dictionary<string, List<AgentManifest>>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            if (!_agents.TryGetValue(agent.AgentId, out var list))
            {
                list = new List<AgentManifest>();
                _agents[agent.AgentId] = list;
            }
            list.Add(agent);
        }
    }


    /// <summary>Look up all registered manifests for an AgentId.</summary>
    public IReadOnlyList<AgentManifest> Resolve(string agentId)
        => _agents.TryGetValue(agentId, out var manifests)
            ? manifests
            : throw new KeyNotFoundException($"Agent '{agentId}' is not registered.");

    public IReadOnlyCollection<AgentManifest> All => _agents.Values.SelectMany(x => x).ToList();

    // ── Dynamic factory (preferred) ───────────────────────────────────────────

    /// <summary>
    /// Build a registry by reading all *.yaml plan files from
    /// <paramref name="plansDirectory"/>.  Each plan contributes one
    /// <see cref="AgentManifest"/>; duplicate agentId values keep the last
    /// definition (alphabetical file order).
    /// </summary>
    public static AgentRegistry LoadFromPlans(string plansDirectory)
    {
        var plans = PlanLoader.LoadFromDirectory(plansDirectory);

        var manifests = plans
            .Where(p => !string.IsNullOrWhiteSpace(p.AgentId))
            .Select(plan => new AgentManifest
            {
                AgentId               = plan.AgentId,
                DisplayName           = string.IsNullOrWhiteSpace(plan.DisplayName) ? plan.AgentId : plan.DisplayName,
                Description           = plan.Description,
                Intent                = plan.Intent,
                Capabilities          = [.. plan.Capabilities],
                Understanding         = plan.Understanding,
                ExpectedSummaryFields = [.. plan.ExpectedData.Summary],
                PlanFactory           = (intent, ctx) => BuildPlanFromYaml(plan, intent, ctx)
            });

        var registry = new AgentRegistry(manifests);
        Console.WriteLine(
            $"  [AgentRegistry] Loaded {registry._agents.Count} agent(s) from plans: " +
            string.Join(", ", registry._agents.Keys));
        return registry;
    }

    /// <summary>
    /// Build an <see cref="ExecutionPlan"/> from the steps declared in a
    /// <see cref="PlanDefinition"/>.
    ///
    /// Parameter precedence (highest → lowest):
    ///   YAML step parameters > intent entities > defaultEntities > companyId
    /// </summary>
    private static ExecutionPlan BuildPlanFromYaml(
        PlanDefinition plan,
        IntentResult intent,
        RoutingContext ctx)
    {
        // Base runtime parameters available to every step
        var runtime = new Dictionary<string, string>(plan.DefaultEntities, StringComparer.OrdinalIgnoreCase)
        {
            ["companyId"] = ctx.CompanyId
        };
        foreach (var kv in intent.Entities)
            runtime[kv.Key] = kv.Value;

        // If no steps declared, fall back to a generic 3-step plan
        if (plan.Steps.Count == 0)
            return FallbackPlan(plan, intent, ctx, runtime);

        // Pre-compute DependsOn from NextStep for canonical-format plans that omit DependsOn.
        // For each step that declares NextStep = x, step x depends on this step.
        var nextStepLookup = plan.Steps
            .Where(s => s.NextStep.HasValue)
            .GroupBy(s => s.NextStep!.Value)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Id).ToArray());

        var steps = plan.Steps.Select(s =>
        {
            // Merge: runtime base first, then YAML static params override
            var merged = new Dictionary<string, string>(runtime, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in s.Parameters)
                merged[kv.Key] = kv.Value;

            // Merge canonical Tool step Input key-value list into parameters
            foreach (var kv in s.Input)
                merged[kv.Key] = kv.Value;

            // Resolve action name: prefer explicit Action, then ToolName, then FunctionName
            string action = !string.IsNullOrEmpty(s.Action)   ? s.Action
                          : !string.IsNullOrEmpty(s.ToolName)  ? s.ToolName
                          : !string.IsNullOrEmpty(s.FunctionName) ? s.FunctionName
                          : $"step-{s.Id}";

            // Resolve dependencies: prefer explicit DependsOn, then infer from NextStep graph
            int[] dependsOn = s.DependsOn.Count > 0
                ? [.. s.DependsOn]
                : nextStepLookup.TryGetValue(s.Id, out var inferred) ? inferred
                : [];

            return new PlanStep(s.Id, action, merged, dependsOn);
        }).ToList();

        return new ExecutionPlan(
            PlanId: $"{plan.PlanId[..Math.Min(plan.PlanId.Length, 8)]}-{ctx.RequestId[..8]}",
            Intent: intent.Intent,
            Steps:  steps);
    }

    private static ExecutionPlan FallbackPlan(
        PlanDefinition plan,
        IntentResult intent,
        RoutingContext ctx,
        Dictionary<string, string> runtime) => new(
        PlanId: $"{plan.AgentId.ToLowerInvariant()[..Math.Min(plan.AgentId.Length, 8)]}-{ctx.RequestId[..8]}",
        Intent: intent.Intent,
        Steps:
        [
            new PlanStep(1, "authenticate",   new Dictionary<string, string>(runtime), []),
            new PlanStep(2, "execute",        new Dictionary<string, string>(runtime), [1]),
            new PlanStep(3, "generate-report",new Dictionary<string, string>(runtime), [2])
        ]);

    // ── Understanding-based lookup ──────────────────────────────────────────

    /// <summary>
    /// Score each registered agent by how well its <see cref="AgentManifest.Understanding"/>
    /// description matches the normalised user query, with a bonus for expected summary
    /// field names that appear in the query.  Returns the highest-scoring agent whose
    /// score meets the minimum threshold, or <c>null</c> if none qualifies.
    /// </summary>
    public AgentManifest? FindByUnderstanding(string normalizedText)
    {
        var lower = normalizedText.ToLowerInvariant();
        return _agents.Values
            .SelectMany(list => list)
            .Select(a => (Agent: a, Score: ScoreByUnderstanding(a, lower)))
            .Where(x => x.Score >= 0.25)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Agent)
            .FirstOrDefault();
    }

    private static double ScoreByUnderstanding(AgentManifest agent, string lowerText)
    {
        double score = 0;

        if (!string.IsNullOrEmpty(agent.Understanding))
        {
            var words = TokenizeText(agent.Understanding)
                .Where(w => w.Length > 3)
                .ToArray();
            if (words.Length > 0)
                score += (double)words.Count(w => lowerText.Contains(w)) / words.Length;
        }

        // Bonus: ExpectedData summary field names found in the query
        foreach (var field in agent.ExpectedSummaryFields)
        {
            var parts = SplitPascalCase(field).Where(w => w.Length > 3);
            if (parts.Any(w => lowerText.Contains(w)))
                score += 0.05;
        }

        return score;
    }

    private static string[] TokenizeText(string text)
        => text.ToLowerInvariant()
               .Split([' ', ',', '.', '-', '(', ')', '\'', '"', ':'],
                      StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> SplitPascalCase(string text)
    {
        var buf = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            if (char.IsUpper(c) && buf.Length > 0)
            {
                yield return buf.ToString().ToLowerInvariant();
                buf.Clear();
            }
            buf.Append(c);
        }
        if (buf.Length > 0) yield return buf.ToString().ToLowerInvariant();
    }

    // ── Capability-based lookup ─────────────────────────────────────────────

    /// <summary>
    /// Find the first agent whose capabilities overlap with the provided list (preferred),
    /// or fall back to matching keywords in normalizedText if capabilities is null/empty.
    /// </summary>
    public AgentManifest? FindByCapability(string normalizedText, IReadOnlyList<string>? extractedCapabilities = null)
    {
        if (extractedCapabilities != null && extractedCapabilities.Count > 0)
        {
            var lowerCaps = extractedCapabilities.Select(c => c.ToLowerInvariant()).ToList();
            return _agents.Values
                .SelectMany(list => list)
                .FirstOrDefault(a =>
                {
                    if (a.Capabilities == null)
                        return false;
                    var planCaps = a.Capabilities.Select(c => c.ToLowerInvariant()).ToList();
                    if (planCaps.Count == 0)
                        return false;
                    int matchCount = planCaps.Count(cap => lowerCaps.Contains(cap));
                    double ratio = (double)matchCount / planCaps.Count;
                    return ratio >= 0.7;
                });
        }
        // Fallback: use normalizedText keyword matching
        var lower = normalizedText.ToLowerInvariant();
        return _agents.Values
            .SelectMany(list => list)
            .FirstOrDefault(a =>
            {
                if (a.Capabilities == null)
                    return false;
                var caps = a.Capabilities.ToList();
                if (caps.Count == 0)
                    return false;
                int matchCount = caps.Count(cap => CapabilityMatches(cap, lower));
                double ratio = (double)matchCount / caps.Count;
                return ratio >= 0.7;
            });
    }

    /// <summary>
    /// A capability like "cash-flow" matches text that contains either the
    /// full phrase (with hyphen replaced by space) or any individual word in
    /// that phrase that is longer than 3 characters.
    /// </summary>
    private static bool CapabilityMatches(string capability, string lowerText)
    {
        var phrase = capability.ToLowerInvariant().Replace('-', ' ');
        if (lowerText.Contains(phrase)) return true;
        return phrase.Split(' ').Any(w => w.Length > 3 && lowerText.Contains(w));
    }

    // ── Static fallback for unit tests ────────────────────────────────────────

    /// <summary>
    /// Hardcoded registry used by unit tests that run without a plans directory.
    /// For production use <see cref="LoadFromPlans"/> instead.
    /// </summary>
    public static AgentRegistry BuildDefault() => new(
    [
        new AgentManifest { AgentId = "BookkeepingAgent",      DisplayName = "Bookkeeping Agent" },
        new AgentManifest { AgentId = "InvoiceAgent",          DisplayName = "Invoice Agent" },
        new AgentManifest { AgentId = "ReconciliationAgent",   DisplayName = "Reconciliation Agent" },
        new AgentManifest { AgentId = "TaxAgent",              DisplayName = "Tax Agent" },
        new AgentManifest { AgentId = "ReportingAgent",        DisplayName = "Reporting Agent" },
        new AgentManifest { AgentId = "ProfitAuditAgent",      DisplayName = "Profit Audit Agent" },
        new AgentManifest { AgentId = "TaxOptimizationAgent",  DisplayName = "Tax Optimization Agent" },
        new AgentManifest { AgentId = "CashRunwayAgent",       DisplayName = "Cash Runway Agent" },
    ]);
}
