// CffPlanTransformer – Model types for parsed real copilot plans
namespace CffPlanTransformer.Parser;

/// <summary>Parsed representation of a real copilot-engine plan response_NNN.txt file.</summary>
internal sealed class RealPlan
{
    /// <summary>Category from Reasoning block (Aggregation, Calculation, Comparison, Retrieval, …).</summary>
    public string Category { get; set; } = "";

    /// <summary>Free-text description of what the plan does.</summary>
    public string Understanding { get; set; } = "";

    /// <summary>
    /// High-level implementation approach from the Reasoning block.
    /// Describes the computation performed (e.g. "aggregate by customer, compute approval rate").
    /// Tool names and step-type phrases are stripped before use.
    /// </summary>
    public string Approach { get; set; } = "";

    /// <summary>Output layout declared in the plan (Text, BarChart+Table, etc.).</summary>
    public string Layout { get; set; } = "";

    /// <summary>Tool entries keyed by StepId ("Step1", "Step2", …).</summary>
    public List<ToolEntry> Tools { get; set; } = [];

    /// <summary>Input params per tool step, keyed by StepId.</summary>
    public Dictionary<string, Dictionary<string, string>> RequiredParams { get; set; } = [];

    /// <summary>Ordered summary field names from ExpectedData.</summary>
    public List<string> SummaryFields { get; set; } = [];

    /// <summary>Parsed <Step> blocks from the Response section.</summary>
    public List<ResponseStep> Steps { get; set; } = [];
}

internal sealed class ToolEntry
{
    public string StepId  { get; set; } = "";
    public string Name    { get; set; } = "";
    public string Id      { get; set; } = "";
}

internal sealed class ResponseStep
{
    public int     Id           { get; set; }
    public string  Type         { get; set; } = "";   // "Tool" | "Code"
    public string  ToolId       { get; set; } = "";
    public string  ToolName     { get; set; } = "";
    public string  FunctionName { get; set; } = "";
    public string  StepReason   { get; set; } = "";
    public Dictionary<string, string> Input { get; set; } = [];
    public int?    NextStep      { get; set; }
}
