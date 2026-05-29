// Core/IntentResult.cs
namespace CffRoutingLayerDemo.Core;

public record IntentResult(
    string Intent,
    double Confidence,
    Dictionary<string, string> Entities,
    string AgentId,
    bool RequiresConfirmation,
    bool FromCache = false
);
