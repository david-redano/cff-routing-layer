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
    private readonly Dictionary<string, AgentManifest> _agents;

    public AgentRegistry(IEnumerable<AgentManifest> agents)
        => _agents = agents.ToDictionary(a => a.AgentId, StringComparer.Ordinal);

    /// <summary>Look up a registered agent by its AgentId.</summary>
    public AgentManifest Resolve(string agentId)
        => _agents.TryGetValue(agentId, out var manifest)
            ? manifest
            : throw new KeyNotFoundException($"Agent '{agentId}' is not registered.");

    public IReadOnlyCollection<AgentManifest> All => _agents.Values;

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
                AgentId      = plan.AgentId,
                DisplayName  = string.IsNullOrWhiteSpace(plan.DisplayName) ? plan.AgentId : plan.DisplayName,
                Description  = plan.Description,
                Intent       = plan.Intent,
                Capabilities = [.. plan.Capabilities],
                PlanFactory  = (intent, ctx) => BuildPlanFromYaml(plan, intent, ctx)
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

        var steps = plan.Steps.Select(s =>
        {
            // Merge: runtime base first, then YAML static params override
            var merged = new Dictionary<string, string>(runtime, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in s.Parameters)
                merged[kv.Key] = kv.Value;

            return new PlanStep(s.Id, s.Action, merged, [.. s.DependsOn]);
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

    // ── Capability-based lookup ─────────────────────────────────────────────

    /// <summary>
    /// Find the first agent whose capabilities contain a keyword present in
    /// <paramref name="normalizedText"/>.  Called when intent classification
    /// returns Unknown, as a secondary routing path before giving up.
    /// </summary>
    public AgentManifest? FindByCapability(string normalizedText)
    {
        var lower = normalizedText.ToLowerInvariant();
        return _agents.Values.FirstOrDefault(a =>
            a.Capabilities.Any(cap => CapabilityMatches(cap, lower)));
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
