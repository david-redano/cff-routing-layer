// Plans/PlanLoader.cs
namespace CffRoutingLayerDemo.Plans;

using CffRoutingLayerDemo.Index;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>Loads <see cref="PlanDefinition"/> instances from YAML files.</summary>
public static class PlanLoader
{
    private static readonly IDeserializer Deserializer =
        new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

    private static readonly PlanFeatureExtractor Extractor = new();

    /// <summary>Load a single plan definition from a YAML file.</summary>
    public static PlanDefinition LoadFromFile(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        var plan = Deserializer.Deserialize<PlanDefinition>(yaml);
        plan.Features = Extractor.Extract(plan);
        return plan;
    }

    /// <summary>Load all .yaml plan files from a directory.</summary>
    public static IReadOnlyList<PlanDefinition> LoadFromDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return [];

        return Directory
            .GetFiles(directoryPath, "*.yaml", SearchOption.TopDirectoryOnly)
            .Select(LoadFromFile)
            .ToList();
    }
}
