// Core/RoutingContext.cs
namespace CffRoutingLayerDemo.Core;

public record RoutingContext(
    string RequestId,
    string UserMessage,
    string CompanyId,
    DateTime Timestamp,
    IReadOnlyDictionary<string, string>? Entities = null  // populated by IntentRewriter
);
