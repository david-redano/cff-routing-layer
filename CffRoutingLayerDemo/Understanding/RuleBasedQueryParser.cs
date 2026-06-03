// Understanding/RuleBasedQueryParser.cs
namespace CffRoutingLayerDemo.Understanding;

using CffRoutingLayerDemo.Queries;

/// <summary>
/// Deterministic parser for when LLM is unavailable or for testing.
/// Uses keyword dictionaries and regex patterns.
/// Returns low confidence when ambiguous rather than guessing.
/// </summary>
public sealed class RuleBasedQueryParser : IQueryParser
{
    private readonly ActionClassifier _actionClassifier;
    private readonly EntityExtractor _entityExtractor;
    private readonly TemporalResolver _temporalResolver;
    private readonly ConstraintParser _constraintParser;
    private readonly DomainDictionary _domainDictionary;

    public RuleBasedQueryParser()
    {
        _actionClassifier = new ActionClassifier();
        _entityExtractor  = new EntityExtractor();
        _temporalResolver = new TemporalResolver();
        _constraintParser = new ConstraintParser();
        _domainDictionary = new DomainDictionary();
    }

    public Task<QueryIntent> ParseAsync(string rawQuery, ConversationContext? context = null)
    {
        var normalized = rawQuery.Trim().ToLowerInvariant();

        var action      = _actionClassifier.Classify(normalized);
        var (domain, subDomain, domainConf) = _domainDictionary.Classify(normalized);
        var slots       = _entityExtractor.Extract(rawQuery);
        var temporal    = _temporalResolver.Extract(rawQuery);
        var constraints = _constraintParser.Extract(rawQuery);
        var outputFmt   = InferOutputFormat(action.Action, normalized);
        var confidence  = ComputeConfidence(action.Confidence, domainConf, slots.Count);

        // Carry over session slots from context as low-confidence implicit slots
        var allSlots = new List<QuerySlot>(slots);
        if (context is not null)
        {
            foreach (var (k, v) in context.SessionSlots)
            {
                if (!allSlots.Any(s => s.Name.Equals(k, StringComparison.OrdinalIgnoreCase)))
                {
                    allSlots.Add(new QuerySlot
                    {
                        Name       = k,
                        Value      = v,
                        Type       = "string",
                        Confidence = 0.6f,
                        IsExplicit = false
                    });
                }
            }
        }

        var intent = new QueryIntent
        {
            RawQuery          = rawQuery,
            NormalizedQuery   = normalized,
            PrimaryAction     = action.Action,
            ImpliedActions    = action.ImpliedActions,
            Domain            = domain,
            SubDomain         = subDomain,
            DomainConfidence  = domainConf,
            ExtractedSlots    = allSlots,
            TemporalScope     = temporal,
            Constraints       = constraints,
            ExpectedOutput    = outputFmt,
            SubIntents        = [],
            OverallConfidence = confidence,
            ActionConfidence  = action.Confidence,
            ParsingNotes      = []
        };

        return Task.FromResult(intent);
    }

    private static OutputFormat InferOutputFormat(DomainAction action, string text)
    {
        if (action == DomainAction.Forecast)    return OutputFormat.Forecast;
        if (action == DomainAction.Compare)     return OutputFormat.Comparison;
        if (action == DomainAction.Compute)     return OutputFormat.Summary;
        if (text.Contains("detail") || text.Contains("breakdown") || text.Contains("list"))
            return OutputFormat.DetailedList;
        return OutputFormat.Summary;
    }

    private static float ComputeConfidence(float actionConf, float domainConf, int slotCount)
    {
        var slotBonus = Math.Min(0.2f, slotCount * 0.05f);
        return Math.Clamp((actionConf + domainConf) / 2f + slotBonus, 0f, 1f);
    }
}
