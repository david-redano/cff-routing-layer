// Core/PlanRoutingPipeline.cs
namespace CffRoutingLayerDemo.Core;

using System.Diagnostics;
using CffRoutingLayerDemo.Index;
using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;
using CffRoutingLayerDemo.Ranking;
using CffRoutingLayerDemo.Understanding;
using CffRoutingLayerDemo.Validation;

/// <summary>
/// Orchestrates the four-layer routing pipeline:
///   Layer 1 — Query Understanding   (free text → structured QueryIntent)
///   Layer 2 — Plan Retrieval        (QueryIntent → 1-5 candidate plans via structural index)
///   Layer 3 — Plan Ranking          (candidates → ranked shortlist, LLM only if ambiguous)
///   Layer 4 — Plan Validation       (verify selected plan can execute with available slots)
///
/// Produces a RoutingDecision with a full diagnostic trace for every query.
/// Execution of the selected plan is the caller's responsibility.
/// </summary>
public sealed class PlanRoutingPipeline
{
    private readonly IQueryParser   _queryParser;
    private readonly IPlanIndex     _planIndex;
    private readonly IPlanRanker    _ranker;
    private readonly IPlanValidator _validator;

    public PlanRoutingPipeline(
        IQueryParser   queryParser,
        IPlanIndex     planIndex,
        IPlanRanker    ranker,
        IPlanValidator validator)
    {
        _queryParser = queryParser;
        _planIndex   = planIndex;
        _ranker      = ranker;
        _validator   = validator;
    }

    /// <summary>
    /// Route a user query through the 4-layer pipeline and return a routing decision.
    /// Execution of the selected plan is the caller's responsibility.
    /// </summary>
    public async Task<RoutingDecision> RouteAsync(
        string               rawQuery,
        ConversationContext?  context = null,
        CancellationToken    ct      = default)
    {
        var totalSw = Stopwatch.StartNew();
        var layers  = new List<LayerTrace>();
        var notes   = new List<string>();

        RoutingTraceResult BuildTrace() => new()
        {
            RawQuery       = rawQuery,
            TotalElapsedMs = totalSw.Elapsed.TotalMilliseconds,
            Layers         = layers.Select(l => new RoutingTraceLayerResult
            {
                LayerName = l.Name,
                ElapsedMs = l.ElapsedMs,
                Result    = l.Result
            }).ToList(),
            Notes = notes
        };

        // ═════════════════════════════════════════════════
        // Layer 1 — Query Understanding
        // ═════════════════════════════════════════════════
        var l1Sw   = Stopwatch.StartNew();
        var intent = await _queryParser.ParseAsync(rawQuery, context);
        l1Sw.Stop();

        layers.Add(new LayerTrace("Understanding", l1Sw.Elapsed.TotalMilliseconds, new
        {
            intent.PrimaryAction,
            intent.Domain,
            intent.SubDomain,
            intent.DomainConfidence,
            Slots            = intent.ExtractedSlots.Select(s => $"{s.Name}={s.Value}"),
            TemporalType     = intent.TemporalScope?.Type.ToString(),
            intent.OverallConfidence
        }));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ► Layer 1  Query understanding...          ✓ {intent.PrimaryAction}/{intent.Domain} ({intent.OverallConfidence:P0})");
        Console.ResetColor();

        if (intent.OverallConfidence < 0.25f)
        {
            notes.Add($"Rejected: confidence {intent.OverallConfidence:P0} below threshold");
            return RoutingDecision.Rejected("Confidence below threshold", BuildTrace());
        }

        // ═════════════════════════════════════════════════
        // Layer 2 — Plan Retrieval
        // ═════════════════════════════════════════════════
        var l2Sw       = Stopwatch.StartNew();
        var candidates = _planIndex.Retrieve(intent, maxCandidates: 5);
        l2Sw.Stop();

        layers.Add(new LayerTrace("Retrieval", l2Sw.Elapsed.TotalMilliseconds, new
        {
            CandidateCount = candidates.Count,
            TopPlan        = candidates.FirstOrDefault()?.Plan.PlanId,
            TopScore       = candidates.FirstOrDefault()?.AlignmentScore,
            Plans          = candidates.Select(c => $"{c.Plan.PlanId}({c.AlignmentScore:P0})")
        }));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ► Layer 2  Plan retrieval...               ✓ {candidates.Count} candidates");
        foreach (var c in candidates)
            Console.WriteLine($"             • {c.Plan.PlanId} ({c.AlignmentScore:P0})");
        Console.ResetColor();

        if (candidates.Count == 0)
        {
            notes.Add("No plans survived structural filters");
            return RoutingDecision.NoPlanFound($"No plans match domain={intent.Domain}, action={intent.PrimaryAction}", BuildTrace());
        }

        // ═════════════════════════════════════════════════
        // Layer 3 — Plan Ranking
        // ═════════════════════════════════════════════════
        var l3Sw    = Stopwatch.StartNew();
        var ranking = await _ranker.RankAsync(intent, candidates);
        l3Sw.Stop();

        layers.Add(new LayerTrace("Ranking", l3Sw.Elapsed.TotalMilliseconds, new
        {
            ranking.TopConfidence,
            ranking.IsAmbiguous,
            ranking.Reasoning,
            Winner = ranking.Candidates.FirstOrDefault()?.Plan.PlanId
        }));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ► Layer 3  Plan ranking...                 ✓ {ranking.Candidates.Count} candidates" +
                          (ranking.IsAmbiguous ? " [AMBIGUOUS]" : ""));
        foreach (var c in ranking.Candidates)
            Console.WriteLine($"             • {c.Plan.PlanId} ({c.Score:P0})");
        Console.ResetColor();

        if (ranking.TopConfidence < 0.35f)
        {
            notes.Add($"Low confidence: top score {ranking.TopConfidence:P0}");
            return RoutingDecision.NoPlanFound(
                $"Best candidate scored {ranking.TopConfidence:P0} \u2014 no confident match found",
                BuildTrace());
        }

        // Reject when all top candidates are statistically tied and none scores high enough
        // to be trusted as the correct plan for this query.
        if (ranking.IsAmbiguous && ranking.TopConfidence < 0.65f)
        {
            notes.Add($"Ambiguous: top score {ranking.TopConfidence:P0}, candidates tied within 0.10");
            return RoutingDecision.NoPlanFound(
                $"No confident match found \u2014 top {ranking.Candidates.Count} candidates all score around {ranking.TopConfidence:P0}",
                BuildTrace());
        }

        // ═════════════════════════════════════════════════
        // Layer 4 — Plan Validation
        // ═════════════════════════════════════════════════
        for (int attempt = 0; attempt < ranking.Candidates.Count; attempt++)
        {
            var candidate = ranking.Candidates[attempt];
            var bindings  = BuildSlotBindings(intent, candidate.Plan);

            var l4Sw       = Stopwatch.StartNew();
            var validation = _validator.Validate(candidate.Plan, intent, bindings);
            l4Sw.Stop();

            layers.Add(new LayerTrace($"Validation[{attempt}]", l4Sw.Elapsed.TotalMilliseconds, new
            {
                Plan             = candidate.Plan.PlanId,
                validation.Status,
                validation.Errors,
                validation.Warnings,
                validation.MissingSlots
            }));

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  ► Layer 4  Validation ({candidate.Plan.PlanId})...         {(validation.Status == ValidationStatus.Fail ? "✗" : "✓")} {validation.Status}");
            Console.ResetColor();

            if (validation.Status == ValidationStatus.Fail)
            {
                notes.Add($"Candidate {candidate.Plan.PlanId} failed validation: {string.Join("; ", validation.Errors)}");
                continue;
            }

            var selected = new SelectedPlan
            {
                Plan             = candidate.Plan,
                Confidence       = attempt == 0 ? candidate.Score : candidate.Score * 0.9f,
                SlotBindings     = bindings,
                ValidationResult = validation,
                Explanation      = attempt == 0 ? candidate.Explanation : $"Fallback #{attempt + 1}: {candidate.Explanation}"
            };

            return RoutingDecision.Success(selected, BuildTrace());
        }

        // All candidates failed validation
        return RoutingDecision.ValidationFailed("All candidates failed slot coverage validation", BuildTrace());
    }

    private static IReadOnlyList<SlotBinding> BuildSlotBindings(QueryIntent intent, PlanDefinition plan)
    {
        var bindings = new List<SlotBinding>();

        foreach (var slot in intent.ExtractedSlots.Where(s => s.Confidence >= 0.5f))
        {
            bindings.Add(new SlotBinding
            {
                SlotName   = slot.Name,
                Value      = slot.Value,
                Source     = BindingSource.QueryExtraction,
                Confidence = slot.Confidence
            });
        }

        if (intent.TemporalScope is not null)
        {
            if (intent.TemporalScope.Start.HasValue)
                bindings.Add(new SlotBinding
                {
                    SlotName   = "startDate",
                    Value      = intent.TemporalScope.Start.Value.ToString("yyyy-MM-dd"),
                    Source     = BindingSource.TemporalResolution,
                    Confidence = 0.9f
                });

            if (intent.TemporalScope.End.HasValue)
                bindings.Add(new SlotBinding
                {
                    SlotName   = "endDate",
                    Value      = intent.TemporalScope.End.Value.ToString("yyyy-MM-dd"),
                    Source     = BindingSource.TemporalResolution,
                    Confidence = 0.9f
                });

            if (intent.TemporalScope.RelativeExpression is not null &&
                !bindings.Any(b => b.SlotName == "period"))
            {
                bindings.Add(new SlotBinding
                {
                    SlotName   = "period",
                    Value      = intent.TemporalScope.RelativeExpression,
                    Source     = BindingSource.TemporalResolution,
                    Confidence = 0.85f
                });
            }
        }

        foreach (var constraint in intent.Constraints)
        {
            bindings.Add(new SlotBinding
            {
                SlotName   = $"filter_{constraint.Field}",
                Value      = $"{constraint.Operator}:{constraint.Value}",
                Source     = BindingSource.ConstraintExtraction,
                Confidence = 0.8f
            });
        }

        // Fill in plan defaults for any missing required slots
        foreach (var (key, value) in plan.DefaultEntities)
        {
            if (!bindings.Any(b => b.SlotName.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                bindings.Add(new SlotBinding
                {
                    SlotName   = key,
                    Value      = value,
                    Source     = BindingSource.PlanDefault,
                    Confidence = 0.7f
                });
            }
        }

        return bindings;
    }

    private sealed record LayerTrace(string Name, double ElapsedMs, object? Result);
}
