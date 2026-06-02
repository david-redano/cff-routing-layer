// Bedrock/BedrockLlmHelper.cs
namespace CffRoutingLayerDemo.Bedrock;

using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Config;

/// <summary>
/// Shared helper for single-turn conversations with the configured LLM
/// (Claude Haiku via Amazon Bedrock Converse API).
///
/// All Bedrock text-generation callers (<see cref="BedrockLlmClassifier"/>,
/// <see cref="LlmIntentRewriter"/>, <see cref="BedrockDisambiguator"/>) share
/// one <see cref="AmazonBedrockRuntimeClient"/> instance via this class, avoiding
/// redundant credential resolution and connection-pool fragmentation.
/// </summary>
public sealed class BedrockLlmHelper : IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;

    public BedrockLlmHelper(AppConfig config)
    {
        _modelId = config.BedrockLlmModelId;
        _client  = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    // ── Single system prompt ──────────────────────────────────────────────

    /// <summary>
    /// Sends a single-turn conversation with one system prompt.
    /// Returns the LLM's response text, or an empty string on any error.
    /// </summary>
    public Task<string> ConverseAsync(
        string systemPrompt,
        string userMessage,
        int maxTokens   = 500,
        float temperature = 0f,
        CancellationToken ct = default)
        => ConverseAsync([systemPrompt], userMessage, maxTokens, temperature, ct);

    // ── Multiple system blocks (e.g. static prompt + runtime context) ─────

    /// <summary>
    /// Sends a single-turn conversation with multiple system content blocks
    /// (e.g. a static system prompt plus a runtime-context block).
    /// Returns the LLM's response text, or an empty string on any error.
    /// </summary>
    public async Task<string> ConverseAsync(
        IReadOnlyList<string> systemPrompts,
        string userMessage,
        int maxTokens   = 500,
        float temperature = 0f,
        CancellationToken ct = default)
    {
        // Sanitize non-ASCII characters (e.g. em dash — from generated plan texts)
        // before they reach the AWS SDK — the SDK can raise HttpRequestException
        // when non-ASCII bytes end up in request headers during SigV4 signing.
        userMessage = SanitizeForBedrock(userMessage);

        var request = new ConverseRequest
        {
            ModelId = _modelId,
            System  = systemPrompts
                          .Select(p => new SystemContentBlock { Text = p })
                          .ToList(),
            Messages =
            [
                new Message
                {
                    Role    = ConversationRole.User,
                    Content = [new ContentBlock { Text = userMessage }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = maxTokens,
                Temperature = temperature
            }
        };

        var response = await _client.ConverseAsync(request, ct);
        return response.Output?.Message?.Content?.FirstOrDefault()?.Text?.Trim() ?? "";
    }

    public void Dispose() => _client.Dispose();

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces common typographic non-ASCII characters with ASCII equivalents
    /// and strips any remaining non-ASCII bytes.
    /// </summary>
    private static string SanitizeForBedrock(string text)
    {
        text = text
            .Replace('\u2014', '-')   // em dash  —
            .Replace('\u2013', '-')   // en dash  –
            .Replace('\u2012', '-');  // figure dash ‒

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch <= '\x7F') sb.Append(ch);
        return sb.ToString();
    }
}
