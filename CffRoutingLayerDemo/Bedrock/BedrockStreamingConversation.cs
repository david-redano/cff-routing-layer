// Bedrock/BedrockStreamingConversation.cs
namespace CffRoutingLayerDemo.Bedrock;

using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Config;
using CffRoutingLayerDemo.Conversation;

/// <summary>
/// Streaming LLM conversation using the Bedrock ConverseStream API.
/// Used as the fallback when the routing engine returns "Unknown" intent —
/// the user's message is answered directly by Claude.
///
/// Maintains its own internal message list (separate from ConversationHistory)
/// for the LLM's chat context. The final assistant reply is recorded in the
/// shared <see cref="ConversationHistory"/> by the caller (Program.cs).
/// </summary>
public sealed class BedrockStreamingConversation : IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;
    private readonly List<Message> _messages = [];

    private static readonly string SystemPrompt = """
        You are a helpful financial accounting assistant for small and medium businesses.
        Help with: cash flow, invoices, reconciliation, tax, P&L, bookkeeping, payroll, forecasting.
        Be concise. If the question is outside accounting/finance, politely redirect.
        """;

    public BedrockStreamingConversation(AppConfig config)
    {
        _modelId = config.BedrockStreamModelId;
        _client  = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    /// <summary>
    /// Send a user message and stream the assistant reply to stdout.
    /// Returns the full reply text when streaming completes.
    /// </summary>
    public async Task<string> ChatAsync(
        string userMessage,
        ConversationHistory? history = null,
        CancellationToken ct = default)
    {
        _messages.Add(new Message
        {
            Role    = ConversationRole.User,
            Content = [new ContentBlock { Text = userMessage }]
        });

        // Build system blocks: routing context summary + assistant persona
        var systemBlocks = new List<SystemContentBlock>
        {
            new() { Text = SystemPrompt }
        };

        // Inject conversation summary as additional system context
        var ctx = history?.BuildContext(recentCount: 3);
        if (!string.IsNullOrEmpty(ctx))
            systemBlocks.Add(new SystemContentBlock
            {
                Text = $"[Routing history context — use this to resolve references]\n{ctx}"
            });

        var request = new ConverseStreamRequest
        {
            ModelId  = _modelId,
            System   = systemBlocks,
            Messages = _messages,
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = 512,
                Temperature = 0.5f,
                TopP        = 0.9f
            }
        };

        var reply = new System.Text.StringBuilder();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("  [Streaming] ");

        try
        {
            var response = await _client.ConverseStreamAsync(request, ct);

            await foreach (var evt in response.Stream.WithCancellation(ct))
            {
                if (evt is ContentBlockDeltaEvent delta &&
                    delta.Delta?.Text is { } chunk)
                {
                    Console.Write(chunk);
                    reply.Append(chunk);
                }
            }
        }
        catch (OperationCanceledException) { /* user interrupted */ }
        finally
        {
            Console.ResetColor();
            Console.WriteLine();
        }

        var replyText = reply.ToString();

        // Keep message for next turn's context
        _messages.Add(new Message
        {
            Role    = ConversationRole.Assistant,
            Content = [new ContentBlock { Text = replyText }]
        });

        // Trim to avoid unbounded growth (keep last 20 messages)
        while (_messages.Count > 20)
            _messages.RemoveAt(0);

        return replyText;
    }

    /// <summary>Clear the internal LLM message history.</summary>
    public void Reset() => _messages.Clear();

    public void Dispose() => _client.Dispose();
}
