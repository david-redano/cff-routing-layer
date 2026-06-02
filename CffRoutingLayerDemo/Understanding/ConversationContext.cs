// Understanding/ConversationContext.cs
namespace CffRoutingLayerDemo.Understanding;

public sealed record PreviousTurn
{
    public required string Query { get; init; }
    public required string ResolvedAction { get; init; }
    public required string Domain { get; init; }
}

public sealed record ConversationContext
{
    public required IReadOnlyList<PreviousTurn> RecentTurns { get; init; }
    public required string? ActiveDomain { get; init; }
    public required IReadOnlyDictionary<string, string> SessionSlots { get; init; }
}
