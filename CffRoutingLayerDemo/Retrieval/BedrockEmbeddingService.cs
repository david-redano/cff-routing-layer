// Retrieval/BedrockEmbeddingService.cs
namespace CffRoutingLayerDemo.Retrieval;

using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Config;

/// <summary>
/// Embeds text using Amazon Titan Embeddings V2 via Bedrock InvokeModel.
/// Latency: ~80-120ms per call (excluded for pre-computed plan vectors).
/// Model: amazon.titan-embed-text-v2:0 (1024-dim output).
/// </summary>
public sealed class BedrockEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;

    public BedrockEmbeddingService(AppConfig config)
    {
        _modelId = config.EmbeddingModelId;
        _client  = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { inputText = text });

        var request = new InvokeModelRequest
        {
            ModelId     = _modelId,
            ContentType = "application/json",
            Accept      = "application/json",
            Body        = new MemoryStream(Encoding.UTF8.GetBytes(payload))
        };

        var response = await _client.InvokeModelAsync(request, ct);

        using var doc = JsonDocument.Parse(response.Body);
        var embeddingEl = doc.RootElement.GetProperty("embedding");
        return embeddingEl.EnumerateArray()
                          .Select(e => e.GetSingle())
                          .ToArray();
    }

    public void Dispose() => _client.Dispose();
}
