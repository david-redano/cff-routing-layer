// Core/IntentResult.cs
namespace CffRoutingLayerDemo.Core;

public sealed record IntentResult(
    string Intent,
    double Confidence,
    Dictionary<string, string> Entities,
    string AgentId,
    bool RequiresConfirmation = false
);
