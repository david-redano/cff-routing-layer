// Bedrock/BedrockEmbeddingProvider.cs
namespace CffRoutingLayerDemo.Bedrock;

using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Config;

/// <summary>
/// Generates text embeddings via the Amazon Titan Embeddings V2 model.
/// Returns 1536-dimensional float vectors, L2-normalised (normalize=true).
///
/// Vectors are persisted to a local JSON file (embeddings_cache.json) so
/// repeated calls for the same text never hit Bedrock twice — even across
/// restarts.  The file is created on first use if it does not already exist.
/// </summary>
public sealed class BedrockEmbeddingProvider : IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;

    // ── Local embedding cache ─────────────────────────────────────────────
    private readonly string _cacheFilePath;
    private readonly Dictionary<string, float[]> _localCache;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false
    };

    public BedrockEmbeddingProvider(AppConfig config)
    {
        _modelId = config.EmbeddingModelId;
        _client  = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));

        _cacheFilePath = Path.Combine(AppContext.BaseDirectory, "embeddings_cache.json");
        _localCache    = LoadCacheFile(_cacheFilePath);
    }

    // ── Startup: load or create ───────────────────────────────────────────

    private static Dictionary<string, float[]> LoadCacheFile(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"  [EmbedCache] No cache file found — will create at: {path}");
            return new Dictionary<string, float[]>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<Dictionary<string, float[]>>(json)
                         ?? new Dictionary<string, float[]>(StringComparer.Ordinal);
            Console.WriteLine($"  [EmbedCache] Loaded {loaded.Count} cached vector(s) from: {path}");
            return new Dictionary<string, float[]>(loaded, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [EmbedCache] Warning — could not read cache file ({ex.Message}). Starting empty.");
            return new Dictionary<string, float[]>(StringComparer.Ordinal);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces common typographic non-ASCII characters with ASCII equivalents
    /// and strips any remaining non-ASCII bytes. This prevents the AWS SDK from
    /// raising <see cref="System.Net.Http.HttpRequestException"/> when non-ASCII
    /// characters end up in request headers during SigV4 signing.
    /// </summary>
    private static string SanitizeForBedrock(string text)
    {
        // Typographic dashes → ASCII hyphen-minus
        text = text
            .Replace('\u2014', '-')   // em dash  —
            .Replace('\u2013', '-')   // en dash  –
            .Replace('\u2012', '-');  // figure dash ‒

        // Strip any remaining non-ASCII characters
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
            if (ch <= '\x7F') sb.Append(ch);

        return sb.ToString();
    }

    /// <summary>
    /// Returns the embedding for <paramref name="text"/>.
    /// Checks the local file cache first; calls Bedrock only on a miss,
    /// then persists the new vector back to disk.
    /// </summary>
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        // Normalise to ASCII before using as cache key or sending to Bedrock.
        // Non-ASCII chars (e.g. em dash —) in plan understanding texts can
        // cause the AWS SDK to raise HttpRequestException from HTTP header validation.
        text = SanitizeForBedrock(text);
        if (_localCache.TryGetValue(text, out var cached))
            return cached;

        // Cache miss — call Bedrock
        var vector = await FetchFromBedrockAsync(text, ct);

        // Persist to local cache
        _localCache[text] = vector;
        await PersistCacheAsync(ct);

        return vector;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private async Task<float[]> FetchFromBedrockAsync(string text, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            inputText = text,
            normalize  = true
        });

        var request = new InvokeModelRequest
        {
            ModelId     = _modelId,
            ContentType = "application/json",
            Accept      = "application/json",
            Body        = new MemoryStream(Encoding.UTF8.GetBytes(body))
        };

        var response = await _client.InvokeModelAsync(request, ct);
        using var reader = new StreamReader(response.Body);
        var responseJson  = await reader.ReadToEndAsync(ct);

        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetSingle())
            .ToArray();
    }

    /// <summary>Serialise the in-memory cache to disk (one writer at a time).</summary>
    private async Task PersistCacheAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(_localCache, _jsonOpts);
            await File.WriteAllTextAsync(_cacheFilePath, json, ct);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _fileLock.Dispose();
    }
}
