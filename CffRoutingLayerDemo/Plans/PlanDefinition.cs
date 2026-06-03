// Plans/PlanDefinition.cs
namespace CffRoutingLayerDemo.Plans;

using CffRoutingLayerDemo.Queries;

/// <summary>
/// YAML-deserialisable plan definition.  Contains everything needed to
/// register an agent and build its execution plan at runtime — no C# code
/// change is required when adding a new agent/intent.
///
/// The <c>Understanding</c> and <c>ExpectedData.Summary</c> fields mirror the
/// &lt;Reasoning&gt; block of the canonical plan format and are used for intent
/// matching when keyword classification and capability matching both fail.
/// </summary>
public sealed class PlanDefinition
{
    public string PlanId         { get; set; } = "";
    public string Intent         { get; set; } = "";
    public string AgentId        { get; set; } = "";
    public string DisplayName    { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];

    /// <summary>
    /// Explicit domain classification for this plan (e.g. "sales", "finance", "inventory", "hr").
    /// Populated from YAML. If empty, <see cref="Index.PlanFeatureExtractor"/> infers it from
    /// tool-naming convention and capability text.
    /// </summary>
    public string Domain { get; set; } = "";

    // ── Reasoning fields (from <Reasoning> block) ─────────────────────────

    /// <summary>Natural-language description of what the plan does.
    /// Maps to &lt;Reasoning&gt; → Understanding.</summary>
    public string Understanding { get; set; } = "";

    /// <summary>Describes the output data shape of the plan.
    /// Maps to &lt;Reasoning&gt; → ExpectedData.</summary>
    public ExpectedDataDefinition ExpectedData { get; set; } = new();

    // ── Existing runtime fields ────────────────────────────────────────────

    /// <summary>Default entity values injected into step parameters.</summary>
    public Dictionary<string, string> DefaultEntities { get; set; } = [];

    /// <summary>Sample queries that should resolve to this plan's intent.</summary>
    public List<string> SampleQueries { get; set; } = [];

    /// <summary>Ordered execution steps declared in YAML.</summary>
    public List<YamlPlanStep> Steps { get; set; } = [];

    /// <summary>
    /// Structural feature vector computed at load time by <see cref="Index.PlanFeatureExtractor"/>.
    /// Not populated from YAML — set after deserialization.
    /// </summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public PlanFeatureVector? Features { get; set; }

    /// <summary>All required input slot names derived from step parameters and default entities.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public IReadOnlyList<string> RequiredInputSlots =>
        Steps
            .SelectMany(s => s.Parameters.Keys)
            .Concat(DefaultEntities.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Output field names inferred from ExpectedData.</summary>
    [YamlDotNet.Serialization.YamlIgnore]
    public IReadOnlyList<string> ProducedOutputFields => ExpectedData.Summary;
}

/// <summary>
/// Describes the expected output shape of a plan.
/// Maps to &lt;Reasoning&gt; → ExpectedData.
/// </summary>
public sealed class ExpectedDataDefinition
{
    /// <summary>Top-level summary field names (e.g. TotalExpenses, Currency).</summary>
    public List<string> Summary { get; set; } = [];
}

/// <summary>A single step as declared in a YAML plan file.
/// Supports both the legacy action-based format and the canonical
/// Tool / Code step format from &lt;Response&gt; &lt;Step&gt; blocks.</summary>
public sealed class YamlPlanStep
{
    /// <summary>1-based step identifier (must be unique within the plan).</summary>
    public int Id { get; set; }

    // ── Legacy format ──────────────────────────────────────────────────────

    /// <summary>Action name passed to <see cref="PlanExecutor"/> (legacy format).</summary>
    public string Action { get; set; } = "";

    /// <summary>Step IDs this step must wait for before executing (legacy format).</summary>
    public List<int> DependsOn { get; set; } = [];

    /// <summary>Static parameters declared in YAML; runtime values are merged in at build time.</summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    // ── Canonical format (Tool / Code steps) ──────────────────────────────

    /// <summary>"Tool" or "Code". Empty means legacy action-based step.</summary>
    public string StepType { get; set; } = "";

    /// <summary>Tool function name for Tool steps (maps to Action).</summary>
    public string ToolName { get; set; } = "";

    /// <summary>Key-value input pairs for Tool steps (alternative to Parameters).</summary>
    public List<KeyValueEntry> Input { get; set; } = [];

    /// <summary>Entry-point function name for Code steps (maps to Action).</summary>
    public string FunctionName { get; set; } = "";

    /// <summary>ID of the next step in a linear chain (canonical format).
    /// Used to derive DependsOn when DependsOn is empty.</summary>
    public int? NextStep { get; set; }

    /// <summary>
    /// Field names this step produces as output.
    /// Consumed by <see cref="Validation.SchemaCompatibilityValidator"/> to verify
    /// that downstream steps can receive the inputs they need.
    /// </summary>
    public List<string> OutputFields { get; set; } = [];
}

/// <summary>A key/value input parameter for a canonical Tool step.</summary>
public sealed class KeyValueEntry
{
    public string Key   { get; set; } = "";
    public string Value { get; set; } = "";
}
