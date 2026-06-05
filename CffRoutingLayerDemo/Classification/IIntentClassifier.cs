// Classification/IIntentClassifier.cs
namespace CffRoutingLayerDemo.Classification;

using CffRoutingLayerDemo.Core;

public interface IIntentClassifier
{
    IntentResult Classify(string normalizedText);

    /// <summary>
    /// Returns ALL intents that match the query above the keyword threshold.
    /// Used by multi-intent detection and compound-query routing.
    /// </summary>
    IReadOnlyList<IntentResult> ClassifyAll(string normalizedText);
}
