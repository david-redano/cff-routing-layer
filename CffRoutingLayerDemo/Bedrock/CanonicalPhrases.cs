// Bedrock/CanonicalPhrases.cs
namespace CffRoutingLayerDemo.Bedrock;

using CffRoutingLayerDemo.Plans;

/// <summary>
/// Canonical vocabulary derived at startup from the YAML plan files in the
/// plans/ directory.  Adding a new plan YAML automatically extends the intent
/// list — no manual update to this file is needed.
///
/// <see cref="Normalizations"/> remains hardcoded because it encodes linguistic
/// surface-form rules, not plan metadata.
/// </summary>
public static class CanonicalPhrases
{
    // ── Derived from plans/ at startup ────────────────────────────────────

    /// <summary>All valid intent labels (plus "Unknown"), loaded from plan YAML files.</summary>
    public static string[] Intents { get; private set; } = ["Unknown"];

    /// <summary>intent → agentId mapping, loaded from plan YAML files.</summary>
    public static Dictionary<string, string> IntentToAgent { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// All valid capability labels, loaded from plan YAML files.
    /// </summary>
    public static string[] Capabilities { get; private set; } = [];

    /// <summary>
    /// Bootstraps <see cref="Intents"/>, <see cref="IntentToAgent"/>, and <see cref="Capabilities"/> from the
    /// given plans directory.  Call once at application startup before constructing
    /// any LLM classifier or rewriter.
    /// </summary>
    public static void LoadFromPlans(string plansDirectory)
    {
        var plans = PlanLoader.LoadFromDirectory(plansDirectory);

        IntentToAgent = plans
            .Where(p => !string.IsNullOrWhiteSpace(p.Intent) && !string.IsNullOrWhiteSpace(p.AgentId))
            .ToDictionary(p => p.Intent, p => p.AgentId, StringComparer.Ordinal);

        // Keep "Unknown" last — it's a sentinel, not a real plan intent
        Intents = [.. IntentToAgent.Keys, "Unknown"];

        // Collect all unique capabilities from all plans
        Capabilities = plans
            .Where(p => p.Capabilities != null)
            .SelectMany(p => p.Capabilities)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToArray();

        Console.WriteLine(
            $"  [CanonicalPhrases] Loaded {IntentToAgent.Count} intent(s) and {Capabilities.Length} capability(ies) from plans: " +
            string.Join(", ", IntentToAgent.Keys));
    }

    // ── Hardcoded: linguistic surface-form rules (not plan metadata) ──────

    /// <summary>Surface form → canonical form mappings injected into LLM prompts.</summary>
    public static readonly Dictionary<string, string> Normalizations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["P&L"]                 = "profit and loss",
        ["pnl"]                 = "profit and loss",
        ["profit & loss"]       = "profit and loss",
        ["A/R"]                 = "accounts receivable",
        ["accounts receivable"] = "accounts receivable",
        ["A/P"]                 = "accounts payable",
        ["accounts payable"]    = "accounts payable",
        ["recon"]               = "reconcile",
        ["reconciliation"]      = "reconcile",
        ["bill"]                = "invoice",
        ["billing"]             = "invoice",
    };
}
