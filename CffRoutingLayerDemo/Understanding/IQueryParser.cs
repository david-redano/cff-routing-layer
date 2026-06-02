// Understanding/IQueryParser.cs
namespace CffRoutingLayerDemo.Understanding;

using CffRoutingLayerDemo.Queries;

public interface IQueryParser
{
    /// <summary>
    /// Parses a raw user query into a structured QueryIntent.
    /// This is the only place where natural language interpretation happens.
    /// Everything downstream operates on structured data.
    /// </summary>
    Task<QueryIntent> ParseAsync(string rawQuery, ConversationContext? context = null);
}
