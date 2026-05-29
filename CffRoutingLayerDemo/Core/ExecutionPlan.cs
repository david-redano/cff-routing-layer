// Core/ExecutionPlan.cs
namespace CffRoutingLayerDemo.Core;

public record PlanStep(
    int StepId,
    string Action,
    Dictionary<string, string> Input,
    int[] DependsOn
);

public record ExecutionPlan(
    string PlanId,
    string Intent,
    List<PlanStep> Steps
);
