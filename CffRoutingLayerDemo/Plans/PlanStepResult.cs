// Plans/PlanStepResult.cs
namespace CffRoutingLayerDemo.Plans;

/// <summary>Result produced by the execution of a single plan step.</summary>
public sealed record PlanStepResult(
    int    StepId,
    string Action,
    string Output,
    bool   Success,
    long   ElapsedMs
);
