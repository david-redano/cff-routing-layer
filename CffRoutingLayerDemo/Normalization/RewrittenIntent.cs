// Normalization/RewrittenIntent.cs
namespace CffRoutingLayerDemo.Normalization;

/// <summary>
/// Output of <see cref="IntentRewriter"/>. Carries the PII-free canonical
/// text used for caching/classification, plus the extracted real values
/// that are later injected into execution plan steps.
/// </summary>
public sealed record RewrittenIntent(
    string OriginalText,
    string NormalizedText,
    IReadOnlyDictionary<string, string> ExtractedEntities
)
{
    /// <summary>Merge with entities extracted by the classifier (classifier wins on conflict).</summary>
    public Dictionary<string, string> MergeEntities(Dictionary<string, string> classifierEntities)
    {
        var merged = new Dictionary<string, string>(ExtractedEntities, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in classifierEntities)
            merged[k] = v;   // classifier is more authoritative
        return merged;
    }
}
