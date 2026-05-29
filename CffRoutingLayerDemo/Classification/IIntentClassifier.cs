// Classification/IIntentClassifier.cs
namespace CffRoutingLayerDemo.Classification;

using CffRoutingLayerDemo.Core;

public interface IIntentClassifier
{
    /// <summary>Returns the single best-matching intent (existing behaviour).</summary>
    IntentResult Classify(string userMessage);

    /// <summary>
    /// Returns ALL intents that match the message above the scoring threshold,
    /// ordered by descending confidence. Used by the routing engine for multi-intent
    /// detection. A single-intent query returns a list of length 1.
    /// </summary>
    IReadOnlyList<IntentResult> ClassifyAll(string userMessage);
}
