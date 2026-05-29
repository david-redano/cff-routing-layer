// Plans/PlanDefinition.cs
namespace CffRoutingLayerDemo.Plans;

/// <summary>
/// YAML-deserialisable plan definition.  Contains everything needed to
/// register an agent and build its execution plan at runtime — no C# code
/// change is required when adding a new agent/intent.
/// </summary>
public sealed class PlanDefinition
{
    public string PlanId         { get; set; } = "";
    public string Intent         { get; set; } = "";
    public string AgentId        { get; set; } = "";
    public string DisplayName    { get; set; } = "";
    public string Description    { get; set; } = "";
    public List<string> Capabilities { get; set; } = [];

    /// <summary>Default entity values injected into step parameters.</summary>
    public Dictionary<string, string> DefaultEntities { get; set; } = [];

    /// <summary>Sample queries that should resolve to this plan's intent.</summary>
    public List<string> SampleQueries { get; set; } = [];

    /// <summary>Ordered execution steps declared in YAML.</summary>
    public List<YamlPlanStep> Steps { get; set; } = [];
}

/// <summary>A single step as declared in a YAML plan file.</summary>
public sealed class YamlPlanStep
{
    /// <summary>1-based step identifier (must be unique within the plan).</summary>
    public int Id { get; set; }

    /// <summary>Action name passed to <see cref="PlanExecutor"/>.</summary>
    public string Action { get; set; } = "";

    /// <summary>Step IDs this step must wait for before executing.</summary>
    public List<int> DependsOn { get; set; } = [];

    /// <summary>Static parameters declared in YAML; runtime values are merged in at build time.</summary>
    public Dictionary<string, string> Parameters { get; set; } = [];
}
