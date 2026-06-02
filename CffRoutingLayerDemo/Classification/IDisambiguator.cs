// Classification/IDisambiguator.cs
namespace CffRoutingLayerDemo.Classification;

/// <summary>
/// Resolves ambiguous routing when the Stage 4b fallback matcher proposes a
/// candidate intent at low confidence.  The implementation asks an LLM to
/// compare the user query against each candidate's <c>Understanding</c>
/// description and return the best match — or <c>null</c> when none fit.
/// </summary>
public interface IDisambiguator
{
    /// <summary>
    /// Select the intent that best matches <paramref name="normalizedQuery"/>
    /// from the supplied candidates, or return <c>null</c> if none are a
    /// credible match.
    /// </summary>
    /// <param name="normalizedQuery">PII-free canonical query from Stage 2.</param>
    /// <param name="candidates">
    /// (IntentName, Understanding) pairs — Understanding is the human-readable
    /// description of what the plan actually does (from the YAML plan file).
    /// </param>
    Task<string?> SelectAsync(
        string normalizedQuery,
        IReadOnlyList<(string Intent, string Understanding)> candidates,
        CancellationToken ct = default);
}
