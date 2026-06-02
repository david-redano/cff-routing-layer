// Understanding/EntityExtractor.cs
namespace CffRoutingLayerDemo.Understanding;

using System.Text.RegularExpressions;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Deterministic entity/slot extractor using regex patterns.
/// Extracts named entities that become SlotBindings during plan execution.
/// </summary>
public sealed class EntityExtractor
{
    private static readonly (string Name, string Type, Regex Pattern)[] Rules =
    [
        ("companyId",   "identifier", new Regex(@"\b(?<v>[A-Z]{2,}-\d{3,})\b")),
        ("amount",      "currency",   new Regex(@"\$(?<v>[\d,]+(?:\.\d{2})?)")),
        ("accountId",   "identifier", new Regex(@"\b(?<v>ACC-\d{4,})\b", RegexOptions.IgnoreCase)),
        ("invoiceId",   "identifier", new Regex(@"\b(?<v>INV-\d{4,})\b", RegexOptions.IgnoreCase)),
        ("companyName", "string",     new Regex(@"for\s+(?<v>[A-Z][A-Za-z\s]{2,20}(?:Corp|Inc|LLC|Ltd)?)", RegexOptions.IgnoreCase)),
        ("period",      "temporal",   new Regex(@"\b(?<v>(?:last|this)\s+(?:month|quarter|year)|last\s+\d+\s+days?|ytd|year.to.date)\b", RegexOptions.IgnoreCase)),
    ];

    public IReadOnlyList<QuerySlot> Extract(string text)
    {
        var slots = new List<QuerySlot>();

        foreach (var (name, type, pattern) in Rules)
        {
            var match = pattern.Match(text);
            if (!match.Success) continue;

            var value = match.Groups["v"].Success
                ? match.Groups["v"].Value.Trim()
                : match.Value.Trim();

            slots.Add(new QuerySlot
            {
                Name       = name,
                Value      = value,
                Type       = type,
                Confidence = 0.85f,
                IsExplicit = true
            });
        }

        return slots;
    }
}
