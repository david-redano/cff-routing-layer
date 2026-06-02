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
/// </summary>
public sealed class PlanRoutingPipeline
{
    private readonly IQueryParser   _queryParser;
    private readonly IPlanIndex     _planIndex;
    private readonly IPlanRanker    _ranker;
    private readonly IPlanValidator _validator;
    private readonly PlanExecutor   _executor;

    public PlanRoutingPipeline(
        IQueryParser   queryParser,
        IPlanIndex     planIndex,
        IPlanRanker    ranker,
        IPlanValidator validator,
        PlanExecutor   executor)
    {
        _queryParser = queryParser;
        _planIndex   = planIndex;
        _ranker      = ranker;
        _validator   = validator;
        _executor    = executor;
    }

    /// <summary>
    /// Route a user query to the best matching plan, execute it, and return the result.
    /// </summary>
    public async Task<(string Result, RoutingDecision Decision)> HandleAsync(
        string rawQuery,
        string companyId       = "DEMO-001",
        ConversationContext?   context = null,
        CancellationToken      ct = default)
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
            var trace = BuildTrace();
            return ("I can only assist with accounting and financial tasks. " +
                    "Try asking about cash flow, invoices, reconciliation, taxes, or P&L.",
                    RoutingDecision.Rejected("Confidence below threshold", trace));
        }

        // ═════════════════════════════════════════════════
        // Layer 2 — Plan Retrieval
        // ═════════════════════════════════════════════════
        var l2Sw      = Stopwatch.StartNew();
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
        if (candidates.Count > 0)
            Console.WriteLine($"             Top: {candidates[0].Plan.PlanId} ({candidates[0].AlignmentScore:P0})");
        Console.ResetColor();

        if (candidates.Count == 0)
        {
            notes.Add("No plans survived structural filters");
            var trace = BuildTrace();
            return ("I could not find a plan that matches your request. Please rephrase your accounting question.",
                    RoutingDecision.NoPlanFound($"No plans match domain={intent.Domain}, action={intent.PrimaryAction}", trace));
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
        Console.WriteLine($"  ► Layer 3  Plan ranking...                 ✓ {ranking.Candidates.FirstOrDefault()?.Plan.PlanId} ({ranking.TopConfidence:P0})" +
                          (ranking.IsAmbiguous ? " [AMBIGUOUS]" : ""));
        Console.ResetColor();

        if (ranking.TopConfidence < 0.35f)
        {
            notes.Add($"Low confidence: top score {ranking.TopConfidence:P0}");
            var trace = BuildTrace();
            return ("I found possible matches but am not confident enough to proceed. " +
                    "Please be more specific about what you need.",
                    RoutingDecision.LowConfidence(
                        $"Best candidate scored {ranking.TopConfidence:P0} — below threshold",
                        ranking.Candidates.Select(c => c.Plan.PlanId).ToList(),
                        trace));
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
                continue; // Try next candidate
            }

            // ═════════════════════════════════════════════
            // Execute selected plan
            // ═════════════════════════════════════════════
            var selected = new SelectedPlan
            {
                Plan             = candidate.Plan,
                Confidence       = attempt == 0 ? candidate.Score : candidate.Score * 0.9f,
                SlotBindings     = bindings,
                ValidationResult = validation,
                Explanation      = attempt == 0 ? candidate.Explanation : $"Fallback #{attempt + 1}: {candidate.Explanation}"
            };

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  ► Execute  Running plan {selected.Plan.PlanId}...");
            Console.ResetColor();

            // Build RoutingContext for executor compatibility
            var routingCtx = new RoutingContext(
                RequestId:   Guid.NewGuid().ToString(),
                UserMessage: rawQuery,
                CompanyId:   companyId,
                Timestamp:   DateTime.UtcNow,
                Entities:    bindings.ToDictionary(b => b.SlotName, b => b.Value));

            var executionPlan = BuildExecutionPlan(selected, routingCtx);
            var result        = await _executor.ExecuteAsync(executionPlan, routingCtx, ct);

            var decision = RoutingDecision.Success(selected, BuildTrace());
            return (result, decision);
        }

        // All candidates failed validation
        {
            var trace = BuildTrace();
            return ("I found matching plans but could not satisfy all required parameters. " +
                    "Please provide more details.",
                    RoutingDecision.ValidationFailed(
                        "All candidates failed slot coverage validation", trace));
        }
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

    private static ExecutionPlan BuildExecutionPlan(SelectedPlan selected, RoutingContext ctx)
    {
        var entities = selected.SlotBindings.ToDictionary(
            b => b.SlotName, b => b.Value, StringComparer.OrdinalIgnoreCase);

        var steps = selected.Plan.Steps.Select(s => new PlanStep(
            StepId:    s.Id,
            Action:    string.IsNullOrEmpty(s.ToolName) ? s.Action : s.ToolName,
            Input:     MergeParameters(s.Parameters, entities),
            DependsOn: s.DependsOn.ToArray()
        )).ToList();

        return new ExecutionPlan(
            PlanId: $"{selected.Plan.PlanId}-{ctx.RequestId[..8]}",
            Intent: selected.Plan.Intent,
            Steps:  steps);
    }

    private static Dictionary<string, string> MergeParameters(
        Dictionary<string, string> stepParams,
        Dictionary<string, string> entities)
    {
        var merged = new Dictionary<string, string>(stepParams, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in entities)
        {
            if (!merged.ContainsKey(k))
                merged[k] = v;
        }
        return merged;
    }

    private sealed record LayerTrace(string Name, double ElapsedMs, object? Result);
}
