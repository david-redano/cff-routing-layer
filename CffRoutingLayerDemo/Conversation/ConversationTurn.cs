// Conversation/ConversationTurn.cs
namespace CffRoutingLayerDemo.Conversation;

/// <summary>
/// Immutable record of a single user→assistant turn in the routing session.
/// Stored in <see cref="ConversationHistory"/> for coreference resolution
/// and conversation compaction.
/// </summary>
public sealed record ConversationTurn(
    DateTime Timestamp,

    /// <summary>Verbatim user input.</summary>
    string UserMessage,

    /// <summary>PII-free canonical form produced by the intent rewriter.</summary>
    string NormalizedMessage,

    /// <summary>Named entities extracted during rewriting (customer, accountId, amount, …).</summary>
    IReadOnlyDictionary<string, string> ExtractedEntities,

    /// <summary>Classified intent label, "Unknown", or "Streamed" for streaming-fallback turns.</summary>
    string Intent,

    /// <summary>Agent that handled the turn (empty for Unknown/Streamed turns).</summary>
    string AgentId,

    /// <summary>The assistant reply (report, rejection message, or streamed answer).</summary>
    string AssistantResponse,

    /// <summary>True when the turn was handled by the streaming LLM fallback rather than the router.</summary>
    bool WasStreamed
);
