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
/// Orchestrates the four-phase routing pipeline (redesigned):
///   Phase 0 — Intent Extraction    (free text → structured QueryIntent, LLM-first with fallback)
///   Phase 1 — Coarse Retrieval     (QueryIntent → top-10 candidates, hybrid embedding + metadata)
///   Phase 2 — Fine Ranking         (candidates → re-ranked shortlist, LLM batch scoring)
///   Phase 3 — Validation &amp; Decision (slot filling + calibrated confidence gating)
///
/// Produces a RoutingDecision with a full diagnostic trace for every query.
/// Execution of the selected plan is the caller's responsibility.
/// </summary>
public sealed class PlanRoutingPipeline
{
    private readonly IQueryParser        _queryParser;
    private readonly IPlanIndex          _planIndex;
    private readonly IPlanRanker         _ranker;
    private readonly IPlanValidator      _validator;
    private readonly CalibratedThresholds _thresholds;

    public PlanRoutingPipeline(
        IQueryParser   queryParser,
        IPlanIndex     planIndex,
        IPlanRanker    ranker,
        IPlanValidator validator,
        CalibratedThresholds? thresholds = null)
    {
        _queryParser = queryParser;
        _planIndex   = planIndex;
        _ranker      = ranker;
        _validator   = validator;
        _thresholds  = thresholds ?? CalibratedThresholds.Default;
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
        // Phase 0 — Intent Extraction
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
            intent.OverallConfidence,
            intent.Reasoning
        }));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ► Phase 0  Intent extraction...            ✓ {intent.PrimaryAction}/{intent.Domain} ({intent.OverallConfidence:P0})");
        if (!string.IsNullOrEmpty(intent.Reasoning))
            Console.WriteLine($"             reasoning: {intent.Reasoning}");
        if (intent.TemporalScope is { Type: not TemporalScopeType.None } ts)
        {
            var tsLabel = ts.RelativeExpression
                ?? (ts.Start.HasValue && ts.End.HasValue
                    ? $"{ts.Start:yyyy-MM-dd} → {ts.End:yyyy-MM-dd}"
                    : ts.Type.ToString());
            Console.WriteLine($"             temporal: {tsLabel}");
        }
        foreach (var slot in intent.ExtractedSlots)
            Console.WriteLine($"             slot: {slot.Name} = \"{slot.Value}\" ({slot.Confidence:P0})");
        Console.ResetColor();

        // Phase 0 confidence gate — reject unrecognisable / noise queries before
        // any retrieval or Bedrock ranking calls are made.
        if (intent.OverallConfidence < _thresholds.MinPhase0Confidence)
        {
            notes.Add($"Phase 0 confidence {intent.OverallConfidence:P0} below minimum ({_thresholds.MinPhase0Confidence:P0}) — query too ambiguous");
            return RoutingDecision.Rejected(
                "I couldn't understand your request. Please try rephrasing it with more detail.",
                BuildTrace());
        }

        // ═════════════════════════════════════════════════
        // Multi-intent fork — route each sub-intent independently
        // ═════════════════════════════════════════════════
        if (intent.SubIntents.Count > 0)
        {
            // The root intent describes the FIRST task; subIntents are additional tasks.
            // Route all of them: root first, then each sub-intent.
            var allIntents = new[] { intent }.Concat(intent.SubIntents).ToList();

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  ► Multi-intent: {allIntents.Count} tasks detected");
            Console.ResetColor();

            var plans  = new List<SelectedPlan>();
            var failed = new List<string>();

            foreach (var sub in allIntents)
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"  ┌ Sub-intent: {sub.RawQuery}");
                Console.ResetColor();

                var subDecision = await RouteIntentAsync(sub, layers, notes, BuildTrace, ct);
                if (subDecision.Status == RoutingStatus.Success && subDecision.SelectedPlan is not null)
                    plans.Add(subDecision.SelectedPlan);
                else
                    failed.Add($"\"{sub.RawQuery}\" → {subDecision.Message}");
            }

            if (plans.Count == 0)
                return RoutingDecision.NoPlanFound(
                    $"None of the {allIntents.Count} tasks could be routed: {string.Join("; ", failed)}",
                    BuildTrace());

            if (failed.Count > 0)
                notes.Add($"{failed.Count} task(s) failed routing: {string.Join("; ", failed)}");

            return RoutingDecision.MultiSuccess(plans, BuildTrace());
        }

        return await RouteIntentAsync(intent, layers, notes, BuildTrace, ct);
    }

    /// <summary>Runs Phases 1–3 for a single already-parsed intent.</summary>
    private async Task<RoutingDecision> RouteIntentAsync(
        QueryIntent              intent,
        List<LayerTrace>         layers,
        List<string>             notes,
        Func<RoutingTraceResult> buildTrace,
        CancellationToken        ct)
    {
        // ═════════════════════════════════════════════════
        // Phase 1 — Hybrid Retrieval
        // ═════════════════════════════════════════════════
        var l2Sw       = Stopwatch.StartNew();
        var candidates = await _planIndex.RetrieveAsync(intent, maxCandidates: 10, ct);
        l2Sw.Stop();

        layers.Add(new LayerTrace("Retrieval", l2Sw.Elapsed.TotalMilliseconds, new
        {
            CandidateCount = candidates.Count,
            TopPlan        = candidates.FirstOrDefault()?.Plan.PlanId,
            TopScore       = candidates.FirstOrDefault()?.AlignmentScore,
            Plans          = candidates.Select(c => $"{c.Plan.PlanId}({c.AlignmentScore:P0})")
        }));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ► Phase 1  Hybrid retrieval...             ✓ {candidates.Count} candidates");
        foreach (var c in candidates)
            Console.WriteLine($"             • {c.Plan.PlanId} ({c.AlignmentScore:P0})");
        Console.ResetColor();

        if (candidates.Count == 0)
        {
            notes.Add("No plans survived structural filters");
            return RoutingDecision.NoPlanFound($"No plans match domain={intent.Domain}, action={intent.PrimaryAction}", buildTrace());
        }

        // Phase 1 minimum score gate — if even the best candidate is too weak,
        // skip ranking and reject early (prevents Phase 2 from inflating low scores).
        var topPhase1Score = candidates.Max(c => c.AlignmentScore);
        if (topPhase1Score < _thresholds.MinPhase1Score)
        {
            notes.Add($"Phase 1 top score {topPhase1Score:P0} below minimum ({_thresholds.MinPhase1Score:P0}) — no confident candidate");
            return RoutingDecision.NoPlanFound(
                $"I couldn't find a matching plan (best match: {topPhase1Score:P0}). Please try rephrasing.",
                buildTrace());
        }

        // ═════════════════════════════════════════════════
        // Phase 2 — Fine Ranking
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
        Console.WriteLine($"  ► Phase 2  Fine ranking...                 ✓ {ranking.Candidates.Count} candidates" +
                          (ranking.IsAmbiguous ? " [AMBIGUOUS]" : ""));
        foreach (var c in ranking.Candidates)
            Console.WriteLine($"             • {c.Plan.PlanId} ({c.Score:P0})");
        Console.ResetColor();

        // ═════════════════════════════════════════════════
        // Phase 3 — Validation & Calibrated Decision
        // ═════════════════════════════════════════════════
        var topScore    = ranking.TopConfidence;
        var secondScore = ranking.Candidates.Count > 1 ? ranking.Candidates[1].Score : 0f;
        var decision    = _thresholds.Decide(topScore, secondScore);

        // Hard reject and clarify paths — no validation needed.
        if (decision == RoutingStatus.Rejected)
        {
            notes.Add($"Rejected: top score {topScore:P0} below reject threshold ({_thresholds.RejectThreshold:P0})");
            return RoutingDecision.Rejected(
                $"No plan found — best match scored {topScore:P0} which is too low to be confident",
                buildTrace());
        }

        if (decision == RoutingStatus.Clarify)
        {
            notes.Add($"Clarify: top score {topScore:P0} in clarification band");
            return RoutingDecision.Clarify(
                $"I found a possible match ({ranking.Candidates[0].Plan.DisplayName}, {topScore:P0} confidence) but I'm not sure. Could you rephrase your request?",
                buildTrace());
        }

        if (decision == RoutingStatus.Ambiguous)
        {
            notes.Add($"Ambiguous: scores {topScore:P0} vs {secondScore:P0} (gap {topScore - secondScore:P0} below {_thresholds.AmbiguityGap:P0})");
            var topTwo = ranking.Candidates.Take(2).Select(c =>
            {
                var b = BuildSlotBindings(intent, c.Plan);
                return new SelectedPlan
                {
                    Plan             = c.Plan,
                    Confidence       = c.Score,
                    SlotBindings     = b,
                    ValidationResult = _validator.Validate(c.Plan, intent, b),
                    Explanation      = c.Explanation
                };
            }).ToList();
            return RoutingDecision.Ambiguous(topTwo, buildTrace());
        }

        // Validate the top candidates. Try fallbacks on validation failure.
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
            Console.WriteLine($"  ► Phase 3  Validation ({candidate.Plan.PlanId})...         {(validation.Status == ValidationStatus.Fail ? "✗" : "✓")} {validation.Status}");
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

            // Return ConfirmAndExecute for plans in the confirmation band.
            if (decision == RoutingStatus.ConfirmAndExecute && attempt == 0)
                return RoutingDecision.ConfirmAndExecute(selected, buildTrace());

            return RoutingDecision.Success(selected, buildTrace());
        }

        // All candidates failed validation
        return RoutingDecision.ValidationFailed("All candidates failed slot coverage validation", buildTrace());
    }

    private static IReadOnlyList<SlotBinding> BuildSlotBindings(QueryIntent intent, PlanDefinition plan)
    {
        var bindings  = new List<SlotBinding>();
        var nameCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in intent.ExtractedSlots.Where(s => s.Confidence >= 0.5f))
        {
            nameCount.TryGetValue(slot.Name, out var seen);
            nameCount[slot.Name] = seen + 1;

            // Rename duplicates: "period" → "period", "period2", "period3", …
            // This preserves both periods in a comparison query without a key collision.
            var slotName = seen == 0 ? slot.Name : $"{slot.Name}{seen + 1}";

            bindings.Add(new SlotBinding
            {
                SlotName   = slotName,
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

        if (intent.CompareTemporal is not null)
        {
            if (intent.CompareTemporal.Start.HasValue)
                bindings.Add(new SlotBinding
                {
                    SlotName   = "compareStartDate",
                    Value      = intent.CompareTemporal.Start.Value.ToString("yyyy-MM-dd"),
                    Source     = BindingSource.TemporalResolution,
                    Confidence = 0.9f
                });

            if (intent.CompareTemporal.End.HasValue)
                bindings.Add(new SlotBinding
                {
                    SlotName   = "compareEndDate",
                    Value      = intent.CompareTemporal.End.Value.ToString("yyyy-MM-dd"),
                    Source     = BindingSource.TemporalResolution,
                    Confidence = 0.9f
                });

            if (intent.CompareTemporal.RelativeExpression is not null &&
                !bindings.Any(b => b.SlotName == "comparePeriod"))
            {
                bindings.Add(new SlotBinding
                {
                    SlotName   = "comparePeriod",
                    Value      = intent.CompareTemporal.RelativeExpression,
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
