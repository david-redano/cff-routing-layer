// Demo/SessionStats.cs
namespace CffRoutingLayerDemo.Demo;

/// <summary>
/// Lightweight in-process telemetry for a single REPL session.
/// All writes are from the main async loop — no lock needed.
/// </summary>
public sealed class SessionStats
{
    // ── Counters ──────────────────────────────────────────────────────────────

    public int TotalQueries       { get; private set; }
    public int CacheHits          { get; private set; }
    public int CacheMisses        { get; private set; }
    public int StreamingFallbacks { get; private set; }
    public int GuardrailRejections { get; private set; }

    // LLM call counters — aligned to the pipeline phases that call Bedrock
    public int LlmPhase0Calls      { get; private set; }  // Phase 0: intent extraction (LlmQueryParser)
    public int LlmPhase2Calls      { get; private set; }  // Phase 2: plan ranking (LlmPlanJudge)
    public int LlmRagCalls         { get; private set; }
    public int LlmStreamingCalls   { get; private set; }

    // Token usage (accumulated from BedrockLlmHelper cumulative counters)
    public long TotalInputTokens   { get; private set; }
    public long TotalOutputTokens  { get; private set; }

    // ── Timing accumulators (ms) ──────────────────────────────────────────────

    public long TotalElapsedMs          { get; private set; }
    public long TotalCacheHitElapsedMs  { get; private set; }
    public long TotalCacheMissElapsedMs { get; private set; }
    public long TotalPhase0Ms           { get; private set; }
    public long TotalPhase2Ms           { get; private set; }
    public long TotalExecutorMs         { get; private set; }

    // intent distribution
    private readonly Dictionary<string, int> _intentCounts = new(StringComparer.Ordinal);

    // ── Record helpers ─────────────────────────────────────────────────────────

    public void RecordQuery(bool fromCache, long elapsedMs, string intent)
    {
        TotalQueries++;
        TotalElapsedMs += elapsedMs;

        if (fromCache)
        {
            CacheHits++;
            TotalCacheHitElapsedMs += elapsedMs;
        }
        else
        {
            CacheMisses++;
            TotalCacheMissElapsedMs += elapsedMs;
        }

        if (!string.IsNullOrEmpty(intent))
            _intentCounts[intent] = _intentCounts.GetValueOrDefault(intent) + 1;
    }

    public void RecordGuardrailRejection()       => GuardrailRejections++;
    public void RecordStreamingFallback()        => StreamingFallbacks++;
    public void RecordLlmPhase0Call(long ms)     { LlmPhase0Calls++;  TotalPhase0Ms  += ms; }
    public void RecordLlmPhase2Call(long ms)     { LlmPhase2Calls++;  TotalPhase2Ms  += ms; }
    public void RecordLlmRagCall()               => LlmRagCalls++;
    public void RecordLlmStreamingCall()         => LlmStreamingCalls++;
    public void RecordExecutorMs(long ms)        => TotalExecutorMs += ms;
    public void RecordLlmTokens(long input, long output)
    {
        TotalInputTokens  += input;
        TotalOutputTokens += output;
    }

    // ── Derived ───────────────────────────────────────────────────────────────

    public double CacheHitRate     => TotalQueries == 0 ? 0 : (double)CacheHits / TotalQueries;
    public double AvgElapsedMs     => TotalQueries == 0 ? 0 : (double)TotalElapsedMs / TotalQueries;
    public double AvgCacheHitMs    => CacheHits    == 0 ? 0 : (double)TotalCacheHitElapsedMs  / CacheHits;
    public double AvgCacheMissMs   => CacheMisses  == 0 ? 0 : (double)TotalCacheMissElapsedMs / CacheMisses;
    public int    TotalLlmCalls    => LlmPhase0Calls + LlmPhase2Calls + LlmRagCalls + LlmStreamingCalls;

    // Cost orientation — Claude Haiku on Bedrock: $0.25/1M input, $1.25/1M output
    private const double InputCostPer1M  = 0.25;
    private const double OutputCostPer1M = 1.25;
    public double EstimatedInputCostUsd  => TotalInputTokens  / 1_000_000.0 * InputCostPer1M;
    public double EstimatedOutputCostUsd => TotalOutputTokens / 1_000_000.0 * OutputCostPer1M;
    public double EstimatedTotalCostUsd  => EstimatedInputCostUsd + EstimatedOutputCostUsd;

    public IReadOnlyDictionary<string, int> IntentCounts => _intentCounts;
}
