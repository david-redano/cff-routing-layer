// Bedrock/BedrockDisambiguator.cs
namespace CffRoutingLayerDemo.Bedrock;

using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Config;

/// <summary>
/// LLM-backed disambiguator (Claude Haiku, max_tokens=20).
/// Presents the user query and each candidate's <c>Understanding</c> text to
/// the model and expects back the exact intent name or the word "none".
/// </summary>
public sealed class BedrockDisambiguator : IDisambiguator
{
    private readonly string                   _modelId;
    private readonly AmazonBedrockRuntimeClient _client;

    private const string SystemPrompt = """
        You are an intent selector for a financial accounting assistant.
        Given a user message and a numbered list of candidate intents (each with a description
        of what that intent does), decide which candidate the user is requesting.

        Rules:
        1. Select a candidate ONLY when the user message CLEARLY asks for that exact action.
        2. If the message is vague, about something not in the list, or only loosely related
           (e.g. "create a customer" when the candidate is "create an invoice"), respond: none
        3. Respond with ONLY the exact intent name from the list, or the single word: none
        4. No explanation, no punctuation, no prose — one token answer only.
        """;

    public BedrockDisambiguator(AppConfig config)
    {
        _modelId = config.BedrockLlmModelId;
        _client  = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    /// <inheritdoc/>
    public async Task<string?> SelectAsync(
        string normalizedQuery,
        IReadOnlyList<(string Intent, string Understanding)> candidates,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return null;

        // Build candidate list: "1. CreateInvoice — Create and send an invoice …"
        var candidateLines = string.Join("\n",
            candidates.Select((c, i) => $"{i + 1}. {c.Intent} — {c.Understanding}"));

        var userContent = $"""
            User message: "{normalizedQuery}"

            Candidate intents:
            {candidateLines}

            Which intent matches? Respond with the intent name or none:
            """;

        var request = new ConverseRequest
        {
            ModelId = _modelId,
            System  = [new SystemContentBlock { Text = SystemPrompt }],
            Messages =
            [
                new Message
                {
                    Role    = ConversationRole.User,
                    Content = [new ContentBlock { Text = userContent }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = 20,
                Temperature = 0f
            }
        };

        try
        {
            var response = await _client.ConverseAsync(request, ct);
            var raw      = response.Output?.Message?.Content?.FirstOrDefault()?.Text?.Trim() ?? "";

            // Accept only an exact candidate name (case-insensitive) or "none"
            if (raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;

            var match = candidates.FirstOrDefault(
                c => c.Intent.Equals(raw, StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrEmpty(match.Intent) ? null : match.Intent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [Disambiguator] LLM call failed ({ex.GetType().Name}) — skipping.");
            Console.ResetColor();
            // Reject on failure: a false-positive match (e.g. "create a customer" → CreateInvoice)
            // is more harmful than discarding the candidate and falling through to streaming.
            return null;
        }
    }
}
