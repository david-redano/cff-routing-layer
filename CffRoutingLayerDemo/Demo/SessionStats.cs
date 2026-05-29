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

    // LLM call counters (incremented from RoutingEngine via the record methods)
    public int LlmRewriterCalls    { get; private set; }
    public int LlmClassifierCalls  { get; private set; }
    public int LlmRagCalls         { get; private set; }
    public int LlmStreamingCalls   { get; private set; }

    // ── Timing accumulators (ms) ──────────────────────────────────────────────

    public long TotalElapsedMs         { get; private set; }
    public long TotalCacheHitElapsedMs { get; private set; }
    public long TotalCacheMissElapsedMs { get; private set; }
    public long TotalRewriterMs        { get; private set; }
    public long TotalClassifierMs      { get; private set; }
    public long TotalExecutorMs        { get; private set; }

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

    public void RecordGuardrailRejection()    => GuardrailRejections++;
    public void RecordStreamingFallback()     => StreamingFallbacks++;
    public void RecordLlmRewriterCall(long ms)   { LlmRewriterCalls++;   TotalRewriterMs   += ms; }
    public void RecordLlmClassifierCall(long ms) { LlmClassifierCalls++; TotalClassifierMs += ms; }
    public void RecordLlmRagCall()               => LlmRagCalls++;
    public void RecordLlmStreamingCall()         => LlmStreamingCalls++;
    public void RecordExecutorMs(long ms)        => TotalExecutorMs += ms;

    // ── Derived ───────────────────────────────────────────────────────────────

    public double CacheHitRate     => TotalQueries == 0 ? 0 : (double)CacheHits / TotalQueries;
    public double AvgElapsedMs     => TotalQueries == 0 ? 0 : (double)TotalElapsedMs / TotalQueries;
    public double AvgCacheHitMs    => CacheHits    == 0 ? 0 : (double)TotalCacheHitElapsedMs  / CacheHits;
    public double AvgCacheMissMs   => CacheMisses  == 0 ? 0 : (double)TotalCacheMissElapsedMs / CacheMisses;
    public int    TotalLlmCalls    => LlmRewriterCalls + LlmClassifierCalls + LlmRagCalls + LlmStreamingCalls;

    public IReadOnlyDictionary<string, int> IntentCounts => _intentCounts;
}
