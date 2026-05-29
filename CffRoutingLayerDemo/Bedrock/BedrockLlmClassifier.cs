// Bedrock/BedrockLlmClassifier.cs
namespace CffRoutingLayerDemo.Bedrock;

using System.Collections.Generic;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Config;
using CffRoutingLayerDemo.Core;

/// <summary>
/// LLM-backed intent classifier using Claude via Amazon Bedrock Converse API.
/// Falls back to "Unknown" on any failure so the pipeline can continue.
/// </summary>
public sealed class BedrockLlmClassifier : IIntentClassifier, IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;

    private static readonly string SystemPrompt =
        $$"""
    You are an intent classifier for a financial accounting assistant.
    Given a user message (already PII-anonymised), classify it into zero or more of:

    {{string.Join(", ", CanonicalPhrases.Intents)}}

    Rules:
    - Only classify messages that are clearly and unambiguously about a supported accounting or finance task.
    - If the message is vague, generic, ambiguous, or does not mention a concrete accounting/finance action, return an empty array (no intent).
    - Do NOT infer intent from generic commands (e.g., "run this", "do it now", "run the report", "run the balance sheet") or from requests that do not specify a clear accounting/finance task.
    - Return JSON only: [ { "intent": "...", "confidence": 0.0-1.0, "agentId": "..." } , ... ]
    - confidence: 1.0 = certain, 0.7 = likely, 0.5 = uncertain.
    - agentId: use the agent mapped to the intent (see canonical list); empty string for Unknown.
    - Do NOT include markdown or prose.

    Intent → AgentId mappings:
    {{string.Join("\n", CanonicalPhrases.IntentToAgent.Select(kv => $"  {kv.Key} → {kv.Value}"))}}
    """;

    public BedrockLlmClassifier(AppConfig config)
    {
        _modelId = config.BedrockLlmModelId;
        _client  = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    public IntentResult Classify(string normalizedText)
        => ClassifyAsync(normalizedText).GetAwaiter().GetResult();

    public async Task<IntentResult> ClassifyAsync(
        string normalizedText,
        CancellationToken ct = default)
    {
        var request = new ConverseRequest
        {
            ModelId = _modelId,
            System  = [new SystemContentBlock { Text = SystemPrompt }],
            Messages =
            [
                new Message
                {
                    Role    = ConversationRole.User,
                    Content = [new ContentBlock { Text = $"Classify: \"{normalizedText}\"" }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = 100,
                Temperature = 0f
            }
        };

        try
        {
            var response = await _client.ConverseAsync(request, ct);
            var json     = response.Output.Message.Content[0].Text.Trim();
            return ParseResponse(json, normalizedText);
        }
        catch
        {
            return new IntentResult("Unknown", 0.0,
                new Dictionary<string, string>(), "", false);
        }
    }

    private static IntentResult ParseResponse(string json, string input)
    {
        // Strip markdown fences if present
        var cleaned = json.TrimStart('`');
        if (cleaned.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[4..];
        cleaned = cleaned.TrimEnd('`').Trim();

        try
        {
            using var doc  = JsonDocument.Parse(cleaned);
            var root       = doc.RootElement;
            var intent     = root.GetProperty("intent").GetString() ?? "Unknown";
            var confidence = root.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5;
            var agentId    = root.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "";

            if (!CanonicalPhrases.Intents.Contains(intent)) intent = "Unknown";

            return new IntentResult(intent, confidence,
                new Dictionary<string, string>(), agentId, false);
        }
        catch
        {
            return new IntentResult("Unknown", 0.0,
                new Dictionary<string, string>(), "", false);
        }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// LLM prompt is single-intent by design. Returns the result as a singleton list.
    /// Multi-intent splitting from LLM output is a future enhancement.
    /// </summary>
    public IReadOnlyList<IntentResult> ClassifyAll(string normalizedText)
        => ClassifyAllAsync(normalizedText).GetAwaiter().GetResult();

    public async Task<IReadOnlyList<IntentResult>> ClassifyAllAsync(string normalizedText, CancellationToken ct = default)
    {
        var request = new ConverseRequest
        {
            ModelId = _modelId,
            System  = [new SystemContentBlock { Text = SystemPrompt }],
            Messages =
            [
                new Message
                {
                    Role    = ConversationRole.User,
                    Content = [new ContentBlock { Text = $"Classify: \"{normalizedText}\"" }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = 200,
                Temperature = 0f
            }
        };

        try
        {
            var response = await _client.ConverseAsync(request, ct);
            var json     = response.Output.Message.Content[0].Text.Trim();
            return ParseMultiResponse(json, normalizedText);
        }
        catch
        {
            return [new IntentResult("Unknown", 0.0, new Dictionary<string, string>(), "", false)];
        }
    }

    private static IReadOnlyList<IntentResult> ParseMultiResponse(string json, string input)
    {
        // Strip markdown fences if present
        var cleaned = json.TrimStart('`');
        if (cleaned.StartsWith("json", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[4..];
        cleaned = cleaned.TrimEnd('`').Trim();

        try
        {
            var results = new List<IntentResult>();
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    var intent     = elem.GetProperty("intent").GetString() ?? "Unknown";
                    var confidence = elem.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5;
                    var agentId    = elem.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "";
                    if (!CanonicalPhrases.Intents.Contains(intent)) intent = "Unknown";
                    results.Add(new IntentResult(intent, confidence, new Dictionary<string, string>(), agentId, false));
                }
            }
            else
            {
                // Fallback: single object
                var intent     = doc.RootElement.GetProperty("intent").GetString() ?? "Unknown";
                var confidence = doc.RootElement.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5;
                var agentId    = doc.RootElement.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "";
                if (!CanonicalPhrases.Intents.Contains(intent)) intent = "Unknown";
                results.Add(new IntentResult(intent, confidence, new Dictionary<string, string>(), agentId, false));
            }
            return results.Count > 0 ? results : [new IntentResult("Unknown", 0.0, new Dictionary<string, string>(), "", false)];
        }
        catch
        {
            return [new IntentResult("Unknown", 0.0, new Dictionary<string, string>(), "", false)];
        }
    }
}
