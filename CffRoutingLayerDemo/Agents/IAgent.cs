// Agents/IAgent.cs
namespace CffRoutingLayerDemo.Agents;

using CffRoutingLayerDemo.Core;

public interface IAgent
{
    string AgentId { get; }
    ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context);
}
