// Classification/IIntentClassifier.cs
namespace CffRoutingLayerDemo.Classification;

using CffRoutingLayerDemo.Core;

public interface IIntentClassifier
{
    IntentResult Classify(string userMessage);
}
