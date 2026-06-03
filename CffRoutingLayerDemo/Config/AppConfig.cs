// Config/AppConfig.cs
namespace CffRoutingLayerDemo.Config;

/// <summary>
/// All configuration read from environment variables / .env file at startup.
/// Defaults are safe for local demo (no AWS credentials required).
/// </summary>
public sealed class AppConfig
{
    // ── AWS ──────────────────────────────────────────────────────────────────

    public string AwsRegion { get; init; } = "us-east-1";

    // ── Bedrock model IDs ─────────────────────────────────────────────────────

    /// <summary>Model used for intent classification (LLM classifier).</summary>
    public string BedrockLlmModelId { get; init; } =
        "anthropic.claude-3-haiku-20240307-v1:0";

    /// <summary>Model used for streaming fallback conversation.</summary>
    public string BedrockStreamModelId { get; init; } =
        "anthropic.claude-3-haiku-20240307-v1:0";

    /// <summary>Model used for compaction and LLM intent rewriting.</summary>
    public string BedrockRewriterModelId { get; init; } =
        "anthropic.claude-3-haiku-20240307-v1:0";

    /// <summary>Amazon Titan Embeddings V2 model for semantic cache.</summary>
    public string EmbeddingModelId { get; init; } =
        "amazon.titan-embed-text-v2:0";

    // ── Feature flags ─────────────────────────────────────────────────────────

    /// <summary>
    /// When true, use the LLM-based query parser (Bedrock call per turn).
    /// When false, use the deterministic rule-based query parser (no I/O, &lt; 1 ms).
    /// Replaces: UseLlmRewriter + UseBedrockClassifier
    /// Default: false (safe for local runs without AWS credentials).
    /// </summary>
    public bool UseLlmQueryParser { get; init; } = false;

    /// <summary>
    /// When true, use the LLM plan judge when ranking is ambiguous (top-2 gap &lt; 0.10).
    /// When false, always use the deterministic feature-alignment ranker.
    /// Replaces: UseBedrockDisambiguator
    /// Default: false (safe for local runs without AWS credentials).
    /// </summary>
    public bool UseLlmRanker { get; init; } = false;

    /// <summary>
    /// When true, use hybrid embedding + metadata retrieval (Phase 1).
    /// Requires Bedrock credentials; plan embeddings are pre-computed at startup (~10-15s).
    /// When false, uses the structural filter-based index (no network calls).
    /// Default: false (safe for local runs without AWS credentials).
    /// </summary>
    public bool UseHybridRetrieval { get; init; } = false;

    /// <summary>
    /// When true, final plan-report steps call Claude via <see cref="CffRoutingLayerDemo.Bedrock.RagSummarizer"/>
    /// to narrate the retrieved financial data (RAG loop).
    /// When false, a local formatter generates the report without a Bedrock call.
    /// Default: false (safe for local runs without AWS credentials).
    /// </summary>
    public bool UseBedrockRag { get; init; } = false;

    // ── Conversation history ──────────────────────────────────────────────────

    /// <summary>Number of recent turns provided to the LLM parser as context.</summary>
    public int RewriterContextTurns { get; init; } = 4;

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>Build from environment variables. DotNetEnv must already be loaded.</summary>
    public static AppConfig FromEnvironment() => new()
    {
        AwsRegion               = Env("AWS_REGION",                "us-east-1"),
        BedrockLlmModelId       = Env("BEDROCK_LLM_MODEL_ID",      "anthropic.claude-3-haiku-20240307-v1:0"),
        BedrockStreamModelId    = Env("BEDROCK_STREAM_MODEL_ID",    "anthropic.claude-3-haiku-20240307-v1:0"),
        BedrockRewriterModelId  = Env("BEDROCK_REWRITER_MODEL_ID", "anthropic.claude-3-haiku-20240307-v1:0"),
        EmbeddingModelId        = Env("EMBEDDING_MODEL_ID",        "amazon.titan-embed-text-v2:0"),
        UseLlmQueryParser       = Bool("USE_LLM_QUERY_PARSER",     false),
        UseLlmRanker            = Bool("USE_LLM_RANKER",           false),
        UseHybridRetrieval      = Bool("USE_HYBRID_RETRIEVAL",     false),
        UseBedrockRag           = Bool("USE_BEDROCK_RAG",          false),
        RewriterContextTurns    = Int("REWRITER_CONTEXT_TURNS",    4),
    };

    private static string  Env(string key, string def)  => Environment.GetEnvironmentVariable(key) ?? def;
    private static bool    Bool(string key, bool def)   => bool.TryParse(Env(key, def.ToString()), out var v) ? v : def;
    private static int     Int(string key, int def)     => int.TryParse(Env(key, def.ToString()), out var v) ? v : def;
}
