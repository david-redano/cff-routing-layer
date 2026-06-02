// Index/PlanIndex.cs
namespace CffRoutingLayerDemo.Index;

using CffRoutingLayerDemo.Index.Filters;
using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;
using CffRoutingLayerDemo.Ranking;

/// <summary>
/// In-memory multi-dimensional plan index.
/// Retrieval is a series of progressive filters that narrow the candidate set.
/// 
/// Design principle: ELIMINATE wrong plans quickly, don't try to FIND the right one.
/// The correct plan is whatever survives all filters.
/// </summary>
public sealed class PlanIndex : IPlanIndex
{
    private readonly IReadOnlyList<PlanDefinition> _allPlans;
    private readonly ScoringWeights _weights;
    private readonly IReadOnlyList<IRetrievalFilter> _filters;
    private readonly IndexStats _stats;

    public PlanIndex(IReadOnlyList<PlanDefinition> plans, ScoringWeights? weights = null)
    {
        _allPlans = plans;
        _weights  = weights ?? ScoringWeights.Default;

        // Filters applied cheapest/most-discriminating first
        _filters =
        [
            new DomainFilter(),
            new ActionFilter(),
            new TemporalFilter(),
            new EntityFilter(),
        ];

        _stats = new IndexStats
        {
            PlanCount    = plans.Count,
            ClusterCount = new ClusterBuilder().Build(plans).Count
        };
    }

    public IReadOnlyList<CandidateResult> Retrieve(QueryIntent intent, int maxCandidates = 5)
    {
        var candidates = _allPlans.AsEnumerable();

        foreach (var filter in _filters)
        {
            var narrowed = filter.Apply(candidates, intent).ToList();
            if (narrowed.Count > 0)
                candidates = narrowed;
            // If a filter would eliminate everything, skip it
        }

        return candidates
            .Select(plan => ScorePlan(plan, intent))
            .OrderByDescending(c => c.AlignmentScore)
            .Take(maxCandidates)
            .ToList();
    }

    public IndexStats GetStats() => _stats;

    private CandidateResult ScorePlan(PlanDefinition plan, QueryIntent intent)
    {
        var matched   = new List<string>();
        var mismatched = new List<string>();
        var unknown   = new List<string>();
        var score     = 0f;
        var totalWeight = 0f;

        var features = plan.Features;

        if (features is null)
        {
            // No feature vector — score based on capabilities/intent text
            var capScore = ScoreByCapabilities(plan, intent);
            return new CandidateResult
            {
                Plan              = plan,
                AlignmentScore    = capScore,
                MatchedFeatures   = capScore > 0 ? ["capability match"] : [],
                MismatchedFeatures = [],
                UnknownFeatures   = ["no feature vector — scored by text"]
            };
        }

        // Domain
        totalWeight += _weights.Domain;
        if (features.Domain == intent.Domain)
        {
            score += _weights.Domain;
            matched.Add($"domain:{features.Domain}");
        }
        else if (features.Domain == "general")
        {
            score += _weights.Domain * 0.5f;
            unknown.Add($"domain:general (plan is domain-agnostic)");
        }
        else
        {
            mismatched.Add($"domain: plan={features.Domain}, query={intent.Domain}");
        }

        // SubDomain
        totalWeight += _weights.SubDomain;
        if (features.SubDomain == intent.SubDomain)
        {
            score += _weights.SubDomain;
            matched.Add($"subDomain:{features.SubDomain}");
        }
        else if (intent.SubDomain == "unknown")
        {
            score += _weights.SubDomain * 0.5f;
            unknown.Add($"subDomain: query unspecified, plan={features.SubDomain}");
        }
        else
        {
            mismatched.Add($"subDomain: plan={features.SubDomain}, query={intent.SubDomain}");
        }

        // Action
        totalWeight += _weights.Action;
        if (features.PrimaryAction == intent.PrimaryAction)
        {
            score += _weights.Action;
            matched.Add($"action:{features.PrimaryAction}");
        }
        else if (intent.ImpliedActions.Contains(features.PrimaryAction))
        {
            score += _weights.Action * 0.7f;
            matched.Add($"action:{features.PrimaryAction} (implied)");
        }
        else
        {
            mismatched.Add($"action: plan={features.PrimaryAction}, query={intent.PrimaryAction}");
        }

        // Temporal
        totalWeight += _weights.Temporal;
        if (IsTemporallyCompatible(features, intent.TemporalScope))
        {
            score += _weights.Temporal;
            matched.Add($"temporal:{features.TemporalScope}");
        }
        else
        {
            mismatched.Add($"temporal: plan supports {features.TemporalScope}, query needs {intent.TemporalScope?.Type}");
        }

        // Entity coverage
        totalWeight += _weights.EntityCoverage;
        if (features.RequiredEntityTypes.Count == 0)
        {
            score += _weights.EntityCoverage;
            matched.Add("entities: none required");
        }
        else
        {
            var provided = intent.ExtractedSlots.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var defaulted = plan.DefaultEntities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var covered = features.RequiredEntityTypes.Where(e => provided.Contains(e) || defaulted.Contains(e)).Count();
            var coverage = (float)covered / features.RequiredEntityTypes.Count;
            score += _weights.EntityCoverage * coverage;
            if (coverage >= 1f)
                matched.Add("entities: all required slots covered");
            else
                mismatched.Add($"entities: missing [{string.Join(", ", features.RequiredEntityTypes.Except(provided, StringComparer.OrdinalIgnoreCase))}]");
        }

        // Output format
        totalWeight += _weights.OutputFormat;
        if (features.OutputFormat == intent.ExpectedOutput)
        {
            score += _weights.OutputFormat;
            matched.Add($"output:{features.OutputFormat}");
        }

        // Discriminators
        foreach (var (key, value) in features.Discriminators)
        {
            totalWeight += _weights.Discriminator;
            if (MatchDiscriminator(key, value, intent))
            {
                score += _weights.Discriminator;
                matched.Add($"discriminator:{key}={value}");
            }
        }

        return new CandidateResult
        {
            Plan               = plan,
            AlignmentScore     = totalWeight > 0 ? score / totalWeight : 0f,
            MatchedFeatures    = matched,
            MismatchedFeatures = mismatched,
            UnknownFeatures    = unknown
        };
    }

    private static bool IsTemporallyCompatible(PlanFeatureVector features, TemporalScope? queryScope)
    {
        if (queryScope is null) return true;
        return queryScope.Type switch
        {
            TemporalScopeType.BoundedPeriod  => features.SupportsDateRange,
            TemporalScopeType.PointInTime    => features.SupportsPointInTime,
            TemporalScopeType.RelativeWindow => features.SupportsDateRange,
            TemporalScopeType.OpenEnded      => true,
            _                                => true
        };
    }

    private static bool MatchDiscriminator(string key, string value, QueryIntent intent)
    {
        return key switch
        {
            "cross_reference"   => intent.PrimaryAction == DomainAction.Compare,
            "includes_forecast" => intent.ImpliedActions.Contains(DomainAction.Forecast) ||
                                   intent.PrimaryAction == DomainAction.Forecast,
            _                   => false
        };
    }

    private static float ScoreByCapabilities(PlanDefinition plan, QueryIntent intent)
    {
        // Fallback scoring when feature vector is not available
        var text = (intent.Domain + " " + intent.SubDomain + " " + intent.PrimaryAction.ToString()).ToLowerInvariant();
        var matchCount = plan.Capabilities.Count(c => text.Contains(c.ToLowerInvariant()));
        return matchCount > 0 ? Math.Min(0.6f, matchCount * 0.2f) : 0.1f;
    }
}
