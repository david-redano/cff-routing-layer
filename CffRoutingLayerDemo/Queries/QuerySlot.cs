// Queries/QuerySlot.cs
namespace CffRoutingLayerDemo.Queries;

public sealed record QuerySlot
{
    public required string Name { get; init; }          // "customer", "accountId", "amount"
    public required string Value { get; init; }         // "Acme Corp", "CHK-001", "4500"
    public required string Type { get; init; }          // "string", "currency", "identifier"
    public required float Confidence { get; init; }
    public required bool IsExplicit { get; init; }      // User stated it vs inferred from context
}
