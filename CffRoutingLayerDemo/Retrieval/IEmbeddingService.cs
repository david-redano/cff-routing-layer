// Retrieval/IEmbeddingService.cs
namespace CffRoutingLayerDemo.Retrieval;

/// <summary>
/// Computes dense vector embeddings for text.
/// Used by <see cref="PlanEmbeddingIndex"/> and <see cref="HybridPlanRetriever"/>.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Embed a text string into a dense float vector.
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
