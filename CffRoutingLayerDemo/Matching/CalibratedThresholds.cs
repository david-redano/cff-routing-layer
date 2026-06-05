// Matching/CalibratedThresholds.cs
namespace CffRoutingLayerDemo.Matching;

/// <summary>
/// Calibrated confidence thresholds for routing decisions.
/// Values should be tuned empirically against a labelled test set
/// (see designs/redesign.md — "Confidence Calibration" section).
///
/// Decision logic (applied after Phase 2 ranking):
///   topScore ≥ Execute   AND gap ≥ Ambiguity  → Execute (clear winner)
///   topScore ≥ Confirm   AND gap ≥ Ambiguity  → ConfirmAndExecute
///   topScore ≥ Confirm   AND gap  < Ambiguity  → Ambiguous (top-2 too close)
///   topScore ≥ Reject                          → Clarify
///   topScore  < Reject                          → Reject
/// </summary>
public sealed class CalibratedThresholds
{
    /// <summary>P(correct) > 95%: execute without confirmation.</summary>
    public float ExecuteThreshold { get; set; } = 0.75f;

    /// <summary>P(correct) > 75%: ask "Did you mean X?" before executing.</summary>
    public float ConfirmThreshold { get; set; } = 0.60f;

    /// <summary>P(correct) < 30%: cannot identify plan, ask user to rephrase.</summary>
    public float RejectThreshold { get; set; } = 0.35f;

    /// <summary>Minimum gap between top-1 and top-2 scores to avoid ambiguity prompt.</summary>
    public float AmbiguityGap { get; set; } = 0.08f;

    /// <summary>
    /// Minimum Phase 0 (intent extraction) confidence to proceed to retrieval.
    /// Queries below this threshold are rejected immediately as unrecognisable noise
    /// before any Bedrock embedding or ranking calls are made.
    /// </summary>
    public float MinPhase0Confidence { get; set; } = 0.28f;

    /// <summary>
    /// Minimum Phase 1 (hybrid retrieval) top candidate score to proceed to Phase 2.
    /// When all candidates score below this, no plan is confident enough to rank.
    /// </summary>
    public float MinPhase1Score { get; set; } = 0.45f;

    public static CalibratedThresholds Default { get; } = new();

    /// <summary>
    /// Determine the routing decision based on the top-ranked plan score and runner-up score.
    /// </summary>
    public RoutingStatus Decide(float topScore, float secondScore)
    {
        float gap = topScore - secondScore;

        if (topScore >= ExecuteThreshold && gap >= AmbiguityGap)
            return RoutingStatus.Success;

        if (topScore >= ConfirmThreshold && gap >= AmbiguityGap)
            return RoutingStatus.ConfirmAndExecute;

        if (gap < AmbiguityGap && topScore >= ConfirmThreshold)
            return RoutingStatus.Ambiguous;

        if (topScore >= RejectThreshold)
            return RoutingStatus.Clarify;

        return RoutingStatus.Rejected;
    }
}
