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
    /// When true, use the LLM-based intent rewriter (Bedrock call per turn).
    /// When false, use the regex-based rewriter (no I/O, &lt; 1 ms).
    /// Default: false (safe for local runs without AWS credentials).
    /// </summary>
    public bool UseLlmRewriter { get; init; } = false;

    /// <summary>
    /// When true, use Bedrock embeddings for the semantic cache.
    /// When false, use the local keyword-vector simulator.
    /// Default: false (safe for local runs).
    /// </summary>
    public bool UseBedrockCache { get; init; } = false;

    /// <summary>
    /// When true, use the Bedrock LLM for intent classification.
    /// When false, use the deterministic rule-based classifier.
    /// Default: false (safe for local runs).
    /// </summary>
    public bool UseBedrockClassifier { get; init; } = false;

    /// <summary>    /// When true, an LLM disambiguation call (Stage 4c) runs after the Stage 4b
    /// fallback matcher to confirm or reject low-confidence candidate intents.
    /// Uses the same model as the classifier; max_tokens=20 (one-token answer).
    /// Default: false (safe for local runs without AWS credentials).
    /// </summary>
    public bool UseBedrockDisambiguator { get; init; } = false;

    /// <summary>    /// When true, final plan-report steps call Claude via <see cref="CffRoutingLayerDemo.Bedrock.RagSummarizer"/>
    /// to narrate the retrieved financial data (RAG loop).
    /// When false, a local formatter generates the report without a Bedrock call.
    /// Default: false (safe for local runs without AWS credentials).
    /// </summary>
    public bool UseBedrockRag { get; init; } = false;

    // ── Cache ────────────────────────────────────────────────────────────────

    /// <summary>Cosine similarity threshold for a semantic cache HIT.</summary>
    public double CacheSimilarityThreshold { get; init; } = 0.88;

    /// <summary>Embedding vector dimensions (Titan V2 default).</summary>
    public int EmbeddingDimensions { get; init; } = 1536;

    // ── Conversation history ──────────────────────────────────────────────────

    /// <summary>Number of recent turns provided to the LLM rewriter as context.</summary>
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
        UseLlmRewriter          = Bool("USE_LLM_REWRITER",         false),
        UseBedrockCache         = Bool("USE_BEDROCK_CACHE",         false),
        UseBedrockClassifier    = Bool("USE_BEDROCK_CLASSIFIER",    false),
        UseBedrockDisambiguator = Bool("USE_BEDROCK_DISAMBIGUATOR", false),
        UseBedrockRag           = Bool("USE_BEDROCK_RAG",             false),
        CacheSimilarityThreshold = Double("CACHE_SIMILARITY_THRESHOLD", 0.88),
        EmbeddingDimensions     = Int("EMBEDDING_DIMENSIONS",       1536),
        RewriterContextTurns    = Int("REWRITER_CONTEXT_TURNS",     4),
    };

    private static string  Env(string key, string def)  => Environment.GetEnvironmentVariable(key) ?? def;
    private static bool    Bool(string key, bool def)   => bool.TryParse(Env(key, def.ToString()), out var v) ? v : def;
    private static int     Int(string key, int def)     => int.TryParse(Env(key, def.ToString()), out var v) ? v : def;
    private static double  Double(string key, double def) => double.TryParse(Env(key, def.ToString()), out var v) ? v : def;
}
