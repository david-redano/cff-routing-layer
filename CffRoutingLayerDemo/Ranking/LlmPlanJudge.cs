using System.Text;
using System.Text.Json;
using CffRoutingLayerDemo.Bedrock;
using CffRoutingLayerDemo.Matching;
using CffRoutingLayerDemo.Queries;

namespace CffRoutingLayerDemo.Ranking;

public sealed class LlmPlanJudge : IPlanRanker
{
    private readonly BedrockLlmHelper _bedrock;
    private readonly FeatureAlignmentRanker _deterministicRanker;

    private const string SystemPrompt = """
        You are a plan selection judge for a financial application.

        Given a user's structured intent and 2-5 candidate execution plans,
        select the plan that BEST satisfies the user's needs.

        Respond ONLY with JSON (no markdown):
        {
          "selectedPlanId": "plan-id-here",
          "confidence": 0.85,
          "reasoning": "one sentence"
        }

        If NO plan adequately satisfies the query: { "selectedPlanId": null, "confidence": 0, "reasoning": "..." }
        """;

    public LlmPlanJudge(BedrockLlmHelper bedrock)
    {
        _bedrock             = bedrock;
        _deterministicRanker = new FeatureAlignmentRanker();
    }

    public async Task<RankingResult> RankAsync(QueryIntent intent, IReadOnlyList<CandidateResult> candidates)
    {
        var deterministicResult = await _deterministicRanker.RankAsync(intent, candidates);

        if (!deterministicResult.IsAmbiguous)
            return deterministicResult;

        var prompt = BuildJudgePrompt(intent, candidates);
        string json;
        try
        {
            json = await _bedrock.ConverseAsync(SystemPrompt, prompt, maxTokens: 150, temperature: 0f);
        }
        catch
        {
            return deterministicResult; // Fallback on Bedrock failure
        }

        try
        {
            return ApplyJudgment(json, deterministicResult, candidates);
        }
        catch
        {
            return deterministicResult;
        }
    }

    private static RankingResult ApplyJudgment(string json, RankingResult fallback, IReadOnlyList<CandidateResult> candidates)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var selectedId = root.TryGetProperty("selectedPlanId", out var idProp) && idProp.ValueKind != JsonValueKind.Null
            ? idProp.GetString()
            : null;

        if (selectedId is null) return fallback with { TopConfidence = 0f };

        var selected = candidates.FirstOrDefault(c => c.Plan.PlanId == selectedId);
        if (selected is null) return fallback; // LLM hallucinated an ID

        var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetSingle() : 0.7f;
        var reasoning  = root.TryGetProperty("reasoning",  out var rProp)    ? rProp.GetString() ?? "" : "";

        var reranked = candidates
            .OrderByDescending(c => c.Plan.PlanId == selectedId ? 1f : 0f)
            .ThenByDescending(c => c.AlignmentScore)
            .Select(c => new RankedCandidate
            {
                Plan        = c.Plan,
                Score       = c.Plan.PlanId == selectedId ? confidence : c.AlignmentScore * 0.5f,
                Explanation = c.Plan.PlanId == selectedId ? reasoning : "Not selected by LLM judge"
            })
            .ToList();

        return new RankingResult
        {
            Candidates    = reranked,
            Reasoning     = $"LLM judge selected {selectedId}: {reasoning}",
            TopConfidence = confidence,
            IsAmbiguous   = false
        };
    }

    private static string BuildJudgePrompt(QueryIntent intent, IReadOnlyList<CandidateResult> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## User Intent");
        sb.AppendLine($"Action: {intent.PrimaryAction}");
        sb.AppendLine($"Domain: {intent.Domain}/{intent.SubDomain}");
        sb.AppendLine($"Temporal: {intent.TemporalScope?.Type} ({intent.TemporalScope?.RelativeExpression})");
        sb.AppendLine($"Slots: {string.Join(", ", intent.ExtractedSlots.Select(s => $"{s.Name}={s.Value}"))}");
        sb.AppendLine($"Expected output: {intent.ExpectedOutput}");
        sb.AppendLine();
        sb.AppendLine("## Candidate Plans");
        foreach (var (c, i) in candidates.Select((c, i) => (c, i + 1)))
        {
            sb.AppendLine($"### Plan {i}: {c.Plan.PlanId}");
            sb.AppendLine($"Steps: {string.Join(" → ", c.Plan.Steps.Select(s => string.IsNullOrEmpty(s.ToolName) ? s.Action : s.ToolName))}");
            sb.AppendLine($"Required inputs: {string.Join(", ", c.Plan.RequiredInputSlots)}");
            sb.AppendLine($"Produces: {string.Join(", ", c.Plan.ProducedOutputFields)}");
            sb.AppendLine($"Alignment: {c.AlignmentScore:P0}");
        }
        return sb.ToString();
    }
}
