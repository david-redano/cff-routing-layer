


The user wants me to design a proper plan-matching routing layer for a .NET application based on my critique. Let me create a comprehensive architecture that properly separates intent classification, plan retrieval (structural matching), and plan validation.

Key principles from my critique:
1. Separate intent classification from plan selection from plan instantiation
2. Index plans by structural features, not natural language
3. Use LLM for plan selection reasoning, not embedding similarity
4. Implement plan clustering with explicit discriminators
5. Validate with execution trace comparison
6. Make it debuggable and testable

Let me design this properly as a principal architect would — with clear domain models, separation of concerns, and a pipeline that doesn't compound errors.
</thinking>

# Plan-Matching Routing Layer — Architecture Design

## Design Philosophy

The system answers one question: **"Given a user query and its extracted context, which pre-saved execution plan — when instantiated with runtime parameters — will produce the result the user needs?"**

This is a **structured retrieval problem**, not a semantic similarity problem. The design decomposes it into four independent, testable layers that do not compound errors.

---

## Architecture Overview

```
User query
     │
     ▼
┌────────────────────────────────────────────────────────────────────────┐
│                         PlanRoutingPipeline                            │
│                                                                        │
│  Layer 1 ─ Query Understanding                                         │
│            Parse the query into a structured QueryIntent                │
│            (what, who, when, how, constraints)                          │
│                                                                        │
│  Layer 2 ─ Plan Retrieval                                              │
│            Narrow 105 plans → 3–5 candidates using structural index    │
│            (deterministic, sub-millisecond, no LLM)                     │
│                                                                        │
│  Layer 3 ─ Plan Ranking                                                │
│            Score candidates by feature alignment + optional LLM judge   │
│            (produces ranked shortlist with explanations)                 │
│                                                                        │
│  Layer 4 ─ Plan Validation                                             │
│            Verify selected plan can execute with available parameters   │
│            (slot coverage check + dry-run first step)                   │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
     │
     ▼
SelectedPlan { Plan, Confidence, SlotBindings, ValidationResult }
     │
     ▼
┌────────────────────────────────────────────────────────────────────────┐
│  Plan Executor                                                         │
│  Instantiate plan with bound slots → execute steps → return result     │
└────────────────────────────────────────────────────────────────────────┘
```

---

## Solution Structure

```
CffPlanRouter/
├── CffPlanRouter.sln
│
├── src/
│   ├── CffPlanRouter.Domain/                    # Pure domain models — no dependencies
│   │   ├── Plans/
│   │   │   ├── PlanDefinition.cs                # Immutable plan record
│   │   │   ├── PlanStep.cs                      # Single step in a plan
│   │   │   ├── ToolDescriptor.cs                # Tool name + input/output schema
│   │   │   ├── PlanFeatureVector.cs             # Structural feature index for a plan
│   │   │   └── PlanCluster.cs                   # Group of plans sharing an intent + discriminators
│   │   │
│   │   ├── Queries/
│   │   │   ├── QueryIntent.cs                   # Structured parse of user query
│   │   │   ├── QuerySlot.cs                     # Named entity with type + value + confidence
│   │   │   ├── TemporalScope.cs                 # Period representation (bounded, relative, open)
│   │   │   ├── DomainAction.cs                  # Enum: List, Create, Compute, Compare, Forecast, Audit
│   │   │   └── Constraint.cs                    # Filter/condition extracted from query
│   │   │
│   │   ├── Matching/
│   │   │   ├── CandidateResult.cs               # Plan + score + match explanation
│   │   │   ├── RankingResult.cs                 # Ordered candidates with reasoning
│   │   │   ├── ValidationResult.cs             # Pass/fail + missing slots + warnings
│   │   │   └── SelectedPlan.cs                  # Final output of the pipeline
│   │   │
│   │   └── Execution/
│   │       ├── SlotBinding.cs                   # Slot name → resolved value
│   │       ├── ExecutionContext.cs              # Runtime context for plan execution
│   │       └── StepResult.cs                    # Output of a single executed step
│   │
│   ├── CffPlanRouter.Index/                     # Plan indexing and structural retrieval
│   │   ├── IPlanIndex.cs                        # Query interface for plan retrieval
│   │   ├── PlanIndex.cs                         # In-memory multi-dimensional index
│   │   ├── PlanFeatureExtractor.cs              # Extracts PlanFeatureVector from PlanDefinition
│   │   ├── ClusterBuilder.cs                    # Groups plans into clusters at startup
│   │   ├── DiscriminatorRegistry.cs            # Defines discriminating features per cluster
│   │   └── Filters/
│   │       ├── DomainFilter.cs                  # Filter by domain (sales, finance, inventory)
│   │       ├── ToolFilter.cs                    # Filter by required tools
│   │       ├── ActionFilter.cs                  # Filter by action type
│   │       ├── TemporalFilter.cs               # Filter by temporal scope compatibility
│   │       └── EntityFilter.cs                  # Filter by required entity types
│   │
│   ├── CffPlanRouter.Understanding/             # Layer 1: Query → QueryIntent
│   │   ├── IQueryParser.cs                      # Interface for query understanding
│   │   ├── LlmQueryParser.cs                    # Claude-based structured extraction
│   │   ├── RuleBasedQueryParser.cs              # Deterministic fallback parser
│   │   ├── EntityExtractor.cs                   # Slot extraction (regex + LLM hybrid)
│   │   ├── ActionClassifier.cs                  # Maps verbs/phrases → DomainAction enum
│   │   ├── TemporalResolver.cs                  # Resolves relative dates to TemporalScope
│   │   └── ConstraintParser.cs                  # Extracts filters, negations, conditions
│   │
│   ├── CffPlanRouter.Ranking/                   # Layer 3: Candidates → Ranked shortlist
│   │   ├── IPlanRanker.cs                       # Interface for ranking
│   │   ├── FeatureAlignmentRanker.cs            # Deterministic scoring by feature overlap
│   │   ├── LlmPlanJudge.cs                      # LLM selects best plan given query + candidates
│   │   ├── ScoringWeights.cs                    # Configurable weights per feature dimension
│   │   └── RankingExplanation.cs                # Human-readable match reasoning
│   │
│   ├── CffPlanRouter.Validation/                # Layer 4: Verify plan is executable
│   │   ├── IPlanValidator.cs                    # Interface for validation
│   │   ├── SlotCoverageValidator.cs             # Checks all required slots are bound
│   │   ├── ToolAvailabilityValidator.cs         # Checks all tools in plan are registered
│   │   ├── SchemaCompatibilityValidator.cs      # Checks step output → next step input compatibility
│   │   └── DryRunValidator.cs                   # Executes first step, validates output schema
│   │
│   ├── CffPlanRouter.Execution/                 # Plan instantiation and execution
│   │   ├── IPlanExecutor.cs
│   │   ├── PlanExecutor.cs                      # Topological step execution
│   │   ├── SlotBinder.cs                        # Resolves SlotBindings into step parameters
│   │   ├── ToolRegistry.cs                      # Available tools and their schemas
│   │   └── StepRunner.cs                        # Executes individual steps
│   │
│   ├── CffPlanRouter.Infrastructure/            # AWS Bedrock, persistence, configuration
│   │   ├── Bedrock/
│   │   │   ├── BedrockClient.cs                 # Shared Converse API wrapper
│   │   │   ├── BedrockQueryParser.cs            # Implements IQueryParser via Claude
│   │   │   └── BedrockPlanJudge.cs              # Implements IPlanRanker via Claude
│   │   ├── Storage/
│   │   │   ├── PlanRepository.cs                # Loads plans from YAML/JSON files
│   │   │   └── PlanIndexPersistence.cs          # Serializes/deserializes the built index
│   │   └── Configuration/
│   │       └── RouterConfiguration.cs           # Typed configuration from appsettings
│   │
│   └── CffPlanRouter.Host/                      # Application entry point
│       ├── Program.cs
│       ├── PlanRoutingPipeline.cs               # Orchestrates Layers 1–4
│       ├── appsettings.json
│       └── Diagnostics/
│           ├── RoutingTrace.cs                  # Full diagnostic trace of a routing decision
│           └── RoutingMetrics.cs                # Latency, hit rates, rejection rates
│
├── tests/
│   ├── CffPlanRouter.Domain.Tests/
│   ├── CffPlanRouter.Index.Tests/
│   ├── CffPlanRouter.Understanding.Tests/
│   ├── CffPlanRouter.Ranking.Tests/
│   ├── CffPlanRouter.Validation.Tests/
│   └── CffPlanRouter.Integration.Tests/         # End-to-end with Bedrock mocks
│
├── tools/
│   └── CffPlanRouter.Indexer/                   # CLI tool: ingests plan files → builds index
│       ├── Program.cs
│       └── PlanIngestionPipeline.cs
│
└── plans/                                        # Source plan definitions (YAML)
    ├── sales/
    ├── finance/
    ├── inventory/
    └── _index.json                               # Pre-built index (generated by Indexer tool)
```

---

## Domain Models

### PlanDefinition — What a plan IS

```csharp
namespace CffPlanRouter.Domain.Plans;

/// <summary>
/// Immutable definition of a pre-saved execution plan.
/// This is the unit of retrieval — the thing we're trying to match.
/// </summary>
public sealed record PlanDefinition
{
    public required string PlanId { get; init; }
    public required string Domain { get; init; }           // "sales", "finance", "inventory", "hr"
    public required string Description { get; init; }      // Human-readable (for diagnostics only, never for matching)
    public required IReadOnlyList<PlanStep> Steps { get; init; }
    public required IReadOnlyList<ToolDescriptor> RequiredTools { get; init; }
    public required IReadOnlyList<string> RequiredInputSlots { get; init; }   // What the plan NEEDS to execute
    public required IReadOnlyList<string> ProducedOutputFields { get; init; } // What the plan PRODUCES
    public required PlanFeatureVector Features { get; init; }                 // Structural index (computed at ingestion)
}

/// <summary>
/// A single step in a plan's execution graph.
/// </summary>
public sealed record PlanStep
{
    public required int StepId { get; init; }
    public required string ToolName { get; init; }
    public required string Action { get; init; }           // What this step does (verb phrase)
    public required IReadOnlyDictionary<string, string> InputSchema { get; init; }   // param name → type
    public required IReadOnlyList<string> OutputFields { get; init; }
    public required IReadOnlyList<int> DependsOn { get; init; }
}

/// <summary>
/// Describes a tool that a plan step invokes.
/// </summary>
public sealed record ToolDescriptor
{
    public required string ToolName { get; init; }
    public required string Domain { get; init; }
    public required IReadOnlyList<string> RequiredInputs { get; init; }
    public required IReadOnlyList<string> ProducedOutputs { get; init; }
}
```

### PlanFeatureVector — How plans are INDEXED

This is the critical design element. Plans are indexed by **structural features**, not by natural language descriptions.

```csharp
namespace CffPlanRouter.Domain.Plans;

/// <summary>
/// Structural feature vector for a plan. Used for deterministic retrieval.
/// Every dimension is discrete and enumerable — no embeddings, no similarity thresholds.
/// </summary>
public sealed record PlanFeatureVector
{
    // ─── Domain classification ───
    public required string Domain { get; init; }                    // "sales", "finance", "inventory"
    public required string SubDomain { get; init; }                 // "orders", "invoicing", "reconciliation"

    // ─── Action type ───
    public required DomainAction PrimaryAction { get; init; }       // List, Create, Compute, Compare, Forecast, Audit
    public required IReadOnlySet<DomainAction> SecondaryActions { get; init; }

    // ─── Tool requirements ───
    public required IReadOnlySet<string> ToolNames { get; init; }   // Exact tool names used
    public required int StepCount { get; init; }
    public required bool RequiresCrossReference { get; init; }      // Multiple data sources joined

    // ─── Entity requirements ───
    public required IReadOnlySet<string> RequiredEntityTypes { get; init; }  // "customer", "date_range", "account_id"
    public required IReadOnlySet<string> OptionalEntityTypes { get; init; }

    // ─── Temporal characteristics ───
    public required TemporalScopeType TemporalScope { get; init; } // Bounded, Relative, OpenEnded, None
    public required bool SupportsDateRange { get; init; }
    public required bool SupportsPointInTime { get; init; }

    // ─── Output characteristics ───
    public required OutputFormat OutputFormat { get; init; }         // Summary, DetailedList, Comparison, Forecast
    public required IReadOnlySet<string> OutputFieldNames { get; init; }

    // ─── Discriminators (what makes this plan different from others in the same cluster) ───
    public required IReadOnlyDictionary<string, string> Discriminators { get; init; }
}

public enum DomainAction
{
    List,           // Retrieve and display records
    Create,         // Create a new entity
    Compute,        // Calculate a derived value
    Compare,        // Cross-reference two data sets
    Forecast,       // Project future values
    Audit,          // Detect anomalies or validate
    Categorize,     // Classify/group records
    Optimize        // Recommend improvements
}

public enum TemporalScopeType
{
    None,           // No time dimension
    PointInTime,    // "as of March 15"
    BoundedPeriod,  // "from Jan to Mar"
    RelativeWindow, // "last 30 days"
    OpenEnded       // "all time", "since inception"
}

public enum OutputFormat
{
    Summary,        // Aggregated metrics (totals, averages)
    DetailedList,   // Row-level records
    Comparison,     // Side-by-side or delta
    Forecast,       // Projected values with confidence
    Narrative       // Text report
}
```

### QueryIntent — What the user WANTS

```csharp
namespace CffPlanRouter.Domain.Queries;

/// <summary>
/// Structured representation of what the user is asking for.
/// This is the output of Layer 1 (Query Understanding).
/// It is NEVER compared by text similarity — only by structural alignment.
/// </summary>
public sealed record QueryIntent
{
    public required string RawQuery { get; init; }
    public required string NormalizedQuery { get; init; }

    // ─── What action? ───
    public required DomainAction PrimaryAction { get; init; }
    public required IReadOnlySet<DomainAction> ImpliedActions { get; init; }

    // ─── What domain? ───
    public required string Domain { get; init; }
    public required string SubDomain { get; init; }
    public required float DomainConfidence { get; init; }

    // ─── What entities are provided? ───
    public required IReadOnlyList<QuerySlot> ExtractedSlots { get; init; }

    // ─── What time frame? ───
    public required TemporalScope? TemporalScope { get; init; }

    // ─── What constraints/filters? ───
    public required IReadOnlyList<Constraint> Constraints { get; init; }

    // ─── What output is expected? ───
    public required OutputFormat ExpectedOutput { get; init; }

    // ─── Multi-intent? ───
    public required IReadOnlyList<QueryIntent> SubIntents { get; init; }  // Empty if single intent

    // ─── Confidence and diagnostics ───
    public required float OverallConfidence { get; init; }
    public required IReadOnlyList<string> ParsingNotes { get; init; }     // Diagnostic breadcrumbs
}

public sealed record QuerySlot
{
    public required string Name { get; init; }          // "customer", "accountId", "amount"
    public required string Value { get; init; }         // "Acme Corp", "CHK-001", "4500"
    public required string Type { get; init; }          // "string", "currency", "identifier"
    public required float Confidence { get; init; }     // How sure are we this extraction is correct
    public required bool IsExplicit { get; init; }      // User stated it vs inferred from context
}

public sealed record TemporalScope
{
    public required TemporalScopeType Type { get; init; }
    public DateOnly? Start { get; init; }
    public DateOnly? End { get; init; }
    public string? RelativeExpression { get; init; }    // "last 30 days" (for diagnostics)
}

public sealed record Constraint
{
    public required string Field { get; init; }         // What field is constrained
    public required ConstraintOperator Operator { get; init; }
    public required string Value { get; init; }
    public required bool IsNegation { get; init; }      // "everything EXCEPT payroll"
}

public enum ConstraintOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    LessThan,
    Contains,
    In,
    Between
}
```

---

## Layer 1: Query Understanding

### Interface

```csharp
namespace CffPlanRouter.Understanding;

public interface IQueryParser
{
    /// <summary>
    /// Parses a raw user query into a structured QueryIntent.
    /// This is the ONLY place where natural language interpretation happens.
    /// Everything downstream operates on structured data.
    /// </summary>
    Task<QueryIntent> ParseAsync(string rawQuery, ConversationContext? context = null);
}

public sealed record ConversationContext
{
    public required IReadOnlyList<PreviousTurn> RecentTurns { get; init; }
    public required string? ActiveDomain { get; init; }
    public required IReadOnlyDictionary<string, string> SessionSlots { get; init; }  // Carried-over entities
}
```

### LLM Implementation

```csharp
namespace CffPlanRouter.Understanding;

public sealed class LlmQueryParser : IQueryParser
{
    private readonly BedrockClient _bedrock;
    private readonly ActionClassifier _actionClassifier;
    private readonly TemporalResolver _temporalResolver;
    private readonly EntityExtractor _entityExtractor;

    private const string SystemPrompt = """
        You are a query understanding system for a financial/accounting application.
        
        Given a user query, extract the following structured information as JSON:
        
        {
          "primaryAction": "List|Create|Compute|Compare|Forecast|Audit|Categorize|Optimize",
          "domain": "sales|finance|inventory|hr|general",
          "subDomain": "orders|invoicing|reconciliation|tax|reporting|forecasting|...",
          "domainConfidence": 0.0-1.0,
          "entities": [
            { "name": "...", "value": "...", "type": "...", "confidence": 0.0-1.0, "isExplicit": true|false }
          ],
          "temporal": {
            "type": "None|PointInTime|BoundedPeriod|RelativeWindow|OpenEnded",
            "start": "YYYY-MM-DD or null",
            "end": "YYYY-MM-DD or null",
            "relativeExpression": "original text or null"
          },
          "constraints": [
            { "field": "...", "operator": "Equals|NotEquals|GreaterThan|...", "value": "...", "isNegation": false }
          ],
          "expectedOutput": "Summary|DetailedList|Comparison|Forecast|Narrative",
          "isMultiIntent": false,
          "subIntents": [],
          "confidence": 0.0-1.0,
          "notes": ["any ambiguities or assumptions"]
        }
        
        Rules:
        - If the query contains multiple distinct requests (e.g. "show cash flow AND estimate tax"),
          set isMultiIntent=true and populate subIntents with separate structured objects.
        - For temporal references, resolve relative dates against today: {{TODAY}}.
        - If an entity is inferred from context rather than explicitly stated, set isExplicit=false.
        - If you cannot determine a field with confidence > 0.5, omit it.
        - NEVER guess domain or action — if unclear, set confidence low and add a note.
        """;

    public async Task<QueryIntent> ParseAsync(string rawQuery, ConversationContext? context = null)
    {
        var prompt = BuildPrompt(rawQuery, context);
        var response = await _bedrock.ConverseAsync(SystemPrompt, prompt);
        var parsed = JsonSerializer.Deserialize<QueryIntentDto>(response);

        // Validate and enrich with deterministic post-processing
        var intent = MapToQueryIntent(parsed, rawQuery);

        // Deterministic corrections: the LLM might get temporal resolution wrong
        if (intent.TemporalScope is not null)
        {
            intent = intent with
            {
                TemporalScope = _temporalResolver.Resolve(
                    intent.TemporalScope.RelativeExpression,
                    DateOnly.FromDateTime(DateTime.UtcNow))
            };
        }

        return intent;
    }

    private string BuildPrompt(string rawQuery, ConversationContext? context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Today's date: {DateTime.UtcNow:yyyy-MM-dd}");

        if (context?.RecentTurns.Count > 0)
        {
            sb.AppendLine("\nConversation context:");
            foreach (var turn in context.RecentTurns.TakeLast(3))
            {
                sb.AppendLine($"  User: {turn.Query}");
                sb.AppendLine($"  → Resolved as: {turn.ResolvedAction} in {turn.Domain}");
            }

            if (context.SessionSlots.Count > 0)
            {
                sb.AppendLine($"\nActive session entities: {JsonSerializer.Serialize(context.SessionSlots)}");
            }
        }

        sb.AppendLine($"\nUser query: {rawQuery}");
        return sb.ToString();
    }
}
```

### Rule-Based Fallback (no LLM)

```csharp
namespace CffPlanRouter.Understanding;

/// <summary>
/// Deterministic parser for when LLM is unavailable or for testing.
/// Uses regex patterns and keyword dictionaries.
/// Will not handle ambiguity or coreference — returns low confidence when unsure.
/// </summary>
public sealed class RuleBasedQueryParser : IQueryParser
{
    private readonly ActionClassifier _actionClassifier;
    private readonly EntityExtractor _entityExtractor;
    private readonly TemporalResolver _temporalResolver;
    private readonly ConstraintParser _constraintParser;
    private readonly DomainDictionary _domainDictionary;

    public Task<QueryIntent> ParseAsync(string rawQuery, ConversationContext? context = null)
    {
        var normalized = Normalize(rawQuery);
        var action = _actionClassifier.Classify(normalized);
        var (domain, subDomain, confidence) = _domainDictionary.Classify(normalized);
        var slots = _entityExtractor.Extract(normalized);
        var temporal = _temporalResolver.Extract(normalized);
        var constraints = _constraintParser.Extract(normalized);
        var outputFormat = InferOutputFormat(action, normalized);

        var intent = new QueryIntent
        {
            RawQuery = rawQuery,
            NormalizedQuery = normalized,
            PrimaryAction = action.Action,
            ImpliedActions = action.ImpliedActions,
            Domain = domain,
            SubDomain = subDomain,
            DomainConfidence = confidence,
            ExtractedSlots = slots,
            TemporalScope = temporal,
            Constraints = constraints,
            ExpectedOutput = outputFormat,
            SubIntents = DetectMultiIntent(normalized),
            OverallConfidence = ComputeConfidence(action, domain, slots),
            ParsingNotes = []
        };

        return Task.FromResult(intent);
    }
}
```

### ActionClassifier — Deterministic verb mapping

```csharp
namespace CffPlanRouter.Understanding;

public sealed class ActionClassifier
{
    private static readonly Dictionary<DomainAction, string[]> ActionVerbs = new()
    {
        [DomainAction.List] = ["show", "list", "display", "get", "fetch", "find", "retrieve", "what are", "which"],
        [DomainAction.Create] = ["create", "make", "generate", "send", "issue", "produce", "build"],
        [DomainAction.Compute] = ["calculate", "compute", "estimate", "determine", "how much", "what is the total"],
        [DomainAction.Compare] = ["compare", "reconcile", "match", "cross-reference", "versus", "vs", "difference between"],
        [DomainAction.Forecast] = ["forecast", "predict", "project", "estimate future", "runway", "how long"],
        [DomainAction.Audit] = ["audit", "check", "verify", "validate", "anomaly", "flag", "detect"],
        [DomainAction.Categorize] = ["categorize", "classify", "group", "segment", "break down"],
        [DomainAction.Optimize] = ["optimize", "improve", "reduce", "maximize", "minimize", "best", "recommend"]
    };

    public ActionClassification Classify(string normalizedQuery)
    {
        var scores = new Dictionary<DomainAction, int>();

        foreach (var (action, verbs) in ActionVerbs)
        {
            var matchCount = verbs.Count(v => normalizedQuery.Contains(v, StringComparison.OrdinalIgnoreCase));
            if (matchCount > 0)
                scores[action] = matchCount;
        }

        if (scores.Count == 0)
            return new ActionClassification(DomainAction.List, [], 0.3f); // Default with low confidence

        var primary = scores.MaxBy(kv => kv.Value).Key;
        var secondary = scores.Where(kv => kv.Key != primary).Select(kv => kv.Key).ToHashSet();

        return new ActionClassification(primary, secondary, Math.Min(1f, scores[primary] * 0.4f));
    }
}

public sealed record ActionClassification(
    DomainAction Action,
    IReadOnlySet<DomainAction> ImpliedActions,
    float Confidence);
```

---

## Layer 2: Plan Retrieval (Structural Index)

This is the key differentiator from the original design. Plans are retrieved by **structural feature matching**, not text similarity.

### Interface

```csharp
namespace CffPlanRouter.Index;

public interface IPlanIndex
{
    /// <summary>
    /// Retrieves candidate plans that are structurally compatible with the query intent.
    /// Returns plans ordered by feature alignment score (deterministic, no LLM).
    /// Typical result: 1–5 candidates from a corpus of 100+ plans.
    /// </summary>
    IReadOnlyList<CandidateResult> Retrieve(QueryIntent intent, int maxCandidates = 5);

    /// <summary>
    /// Returns the cluster a plan belongs to, for diagnostics.
    /// </summary>
    PlanCluster? GetCluster(string planId);

    /// <summary>
    /// Statistics about the index for monitoring.
    /// </summary>
    IndexStats GetStats();
}

public sealed record CandidateResult
{
    public required PlanDefinition Plan { get; init; }
    public required float AlignmentScore { get; init; }       // 0.0–1.0
    public required IReadOnlyList<string> MatchedFeatures { get; init; }
    public required IReadOnlyList<string> MismatchedFeatures { get; init; }
    public required IReadOnlyList<string> UnknownFeatures { get; init; }  // Query has features plan doesn't address
}
```

### PlanIndex — Multi-dimensional filtering

```csharp
namespace CffPlanRouter.Index;

/// <summary>
/// In-memory multi-dimensional index over plan feature vectors.
/// Retrieval is a series of progressive filters that narrow the candidate set.
/// Each filter is independent and debuggable.
/// 
/// Design principle: ELIMINATE wrong plans quickly, don't try to FIND the right one.
/// The right plan is whatever survives all filters.
/// </summary>
public sealed class PlanIndex : IPlanIndex
{
    private readonly IReadOnlyList<PlanDefinition> _allPlans;
    private readonly IReadOnlyDictionary<string, PlanCluster> _clusters;
    private readonly IReadOnlyList<IRetrievalFilter> _filters;
    private readonly ScoringWeights _weights;

    public PlanIndex(
        IReadOnlyList<PlanDefinition> plans,
        IReadOnlyDictionary<string, PlanCluster> clusters,
        ScoringWeights weights)
    {
        _allPlans = plans;
        _clusters = clusters;
        _weights = weights;

        // Filters are applied in order — cheapest/most-discriminating first
        _filters =
        [
            new DomainFilter(),          // Eliminates ~80% of plans immediately
            new ActionFilter(),          // Eliminates plans with wrong action type
            new TemporalFilter(),        // Eliminates plans that can't handle the time scope
            new EntityFilter(),          // Eliminates plans that need entities the query doesn't provide
            new ToolFilter(),            // Eliminates plans requiring unavailable tools
        ];
    }

    public IReadOnlyList<CandidateResult> Retrieve(QueryIntent intent, int maxCandidates = 5)
    {
        // Start with all plans
        var candidates = _allPlans.AsEnumerable();

        // Progressive filtering — each filter narrows the set
        foreach (var filter in _filters)
        {
            candidates = filter.Apply(candidates, intent);

            // If we're down to maxCandidates or fewer, stop filtering
            if (candidates.Count() <= maxCandidates)
                break;
        }

        // Score remaining candidates by feature alignment
        var scored = candidates
            .Select(plan => ScorePlan(plan, intent))
            .OrderByDescending(c => c.AlignmentScore)
            .Take(maxCandidates)
            .ToList();

        return scored;
    }

    private CandidateResult ScorePlan(PlanDefinition plan, QueryIntent intent)
    {
        var matched = new List<string>();
        var mismatched = new List<string>();
        var unknown = new List<string>();
        var score = 0f;
        var totalWeight = 0f;

        // Domain match
        totalWeight += _weights.Domain;
        if (plan.Features.Domain == intent.Domain)
        {
            score += _weights.Domain;
            matched.Add($"domain:{plan.Features.Domain}");
        }
        else
        {
            mismatched.Add($"domain: plan={plan.Features.Domain}, query={intent.Domain}");
        }

        // SubDomain match
        totalWeight += _weights.SubDomain;
        if (plan.Features.SubDomain == intent.SubDomain)
        {
            score += _weights.SubDomain;
            matched.Add($"subDomain:{plan.Features.SubDomain}");
        }
        else if (intent.SubDomain == "unknown")
        {
            score += _weights.SubDomain * 0.5f; // Partial credit — query didn't specify
            unknown.Add($"subDomain: query unspecified, plan={plan.Features.SubDomain}");
        }
        else
        {
            mismatched.Add($"subDomain: plan={plan.Features.SubDomain}, query={intent.SubDomain}");
        }

        // Action match
        totalWeight += _weights.Action;
        if (plan.Features.PrimaryAction == intent.PrimaryAction)
        {
            score += _weights.Action;
            matched.Add($"action:{plan.Features.PrimaryAction}");
        }
        else if (intent.ImpliedActions.Contains(plan.Features.PrimaryAction))
        {
            score += _weights.Action * 0.7f;
            matched.Add($"action:{plan.Features.PrimaryAction} (implied)");
        }
        else
        {
            mismatched.Add($"action: plan={plan.Features.PrimaryAction}, query={intent.PrimaryAction}");
        }

        // Temporal compatibility
        totalWeight += _weights.Temporal;
        if (IsTemporallyCompatible(plan.Features, intent.TemporalScope))
        {
            score += _weights.Temporal;
            matched.Add($"temporal:{plan.Features.TemporalScope}");
        }
        else
        {
            mismatched.Add($"temporal: plan supports {plan.Features.TemporalScope}, query needs {intent.TemporalScope?.Type}");
        }

        // Entity coverage — does the plan need entities the query provides?
        totalWeight += _weights.EntityCoverage;
        var requiredEntities = plan.Features.RequiredEntityTypes;
        var providedEntities = intent.ExtractedSlots.Select(s => s.Name).ToHashSet();
        var coverage = requiredEntities.Count == 0
            ? 1f
            : (float)requiredEntities.Intersect(providedEntities).Count() / requiredEntities.Count;
        score += _weights.EntityCoverage * coverage;
        if (coverage >= 1f)
            matched.Add("entities: all required slots provided");
        else
            mismatched.Add($"entities: plan needs [{string.Join(", ", requiredEntities.Except(providedEntities))}]");

        // Output format match
        totalWeight += _weights.OutputFormat;
        if (plan.Features.OutputFormat == intent.ExpectedOutput)
        {
            score += _weights.OutputFormat;
            matched.Add($"output:{plan.Features.OutputFormat}");
        }

        // Discriminator match (cluster-specific features)
        foreach (var (key, value) in plan.Features.Discriminators)
        {
            totalWeight += _weights.Discriminator;
            var queryHasDiscriminator = MatchDiscriminator(key, value, intent);
            if (queryHasDiscriminator)
            {
                score += _weights.Discriminator;
                matched.Add($"discriminator:{key}={value}");
            }
        }

        return new CandidateResult
        {
            Plan = plan,
            AlignmentScore = totalWeight > 0 ? score / totalWeight : 0f,
            MatchedFeatures = matched,
            MismatchedFeatures = mismatched,
            UnknownFeatures = unknown
        };
    }

    private static bool IsTemporallyCompatible(PlanFeatureVector features, TemporalScope? queryScope)
    {
        if (queryScope is null) return features.TemporalScope == TemporalScopeType.None;

        return queryScope.Type switch
        {
            TemporalScopeType.BoundedPeriod => features.SupportsDateRange,
            TemporalScopeType.PointInTime => features.SupportsPointInTime,
            TemporalScopeType.RelativeWindow => features.SupportsDateRange,
            TemporalScopeType.OpenEnded => true, // Any plan can handle "all time"
            _ => true
        };
    }

    private bool MatchDiscriminator(string key, string value, QueryIntent intent)
    {
        // Discriminators are plan-cluster-specific features
        // e.g., "aggregation=per_customer" matches if query mentions a customer grouping
        return key switch
        {
            "aggregation" => MatchAggregationDiscriminator(value, intent),
            "filter_type" => MatchFilterDiscriminator(value, intent),
            "cross_reference" => intent.PrimaryAction == DomainAction.Compare,
            "includes_forecast" => intent.ImpliedActions.Contains(DomainAction.Forecast),
            _ => false
        };
    }
}
```

### Retrieval Filters

```csharp
namespace CffPlanRouter.Index.Filters;

public interface IRetrievalFilter
{
    IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent);
}

/// <summary>
/// Eliminates plans from the wrong domain.
/// This single filter typically removes 70–85% of candidates.
/// </summary>
public sealed class DomainFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        if (intent.DomainConfidence < 0.4f)
            return candidates; // Don't filter if we're not sure about domain

        return candidates.Where(p =>
            p.Features.Domain == intent.Domain ||
            p.Features.Domain == "general"); // General-purpose plans always pass
    }
}

/// <summary>
/// Eliminates plans whose primary action doesn't match the query's action.
/// </summary>
public sealed class ActionFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        return candidates.Where(p =>
            p.Features.PrimaryAction == intent.PrimaryAction ||
            p.Features.SecondaryActions.Contains(intent.PrimaryAction) ||
            intent.ImpliedActions.Contains(p.Features.PrimaryAction));
    }
}

/// <summary>
/// Eliminates plans that require entity types the query doesn't provide
/// AND that have no default values for those entities.
/// </summary>
public sealed class EntityFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        var providedTypes = intent.ExtractedSlots
            .Where(s => s.Confidence >= 0.5f)
            .Select(s => s.Name)
            .ToHashSet();

        return candidates.Where(p =>
        {
            // Plan passes if all its required entities are either:
            // 1. Provided by the query, OR
            // 2. Optional (in OptionalEntityTypes)
            var unmet = p.Features.RequiredEntityTypes.Except(providedTypes).ToList();
            return unmet.Count == 0;
        });
    }
}

/// <summary>
/// Eliminates plans that can't handle the query's temporal scope.
/// </summary>
public sealed class TemporalFilter : IRetrievalFilter
{
    public IEnumerable<PlanDefinition> Apply(IEnumerable<PlanDefinition> candidates, QueryIntent intent)
    {
        if (intent.TemporalScope is null)
            return candidates;

        return candidates.Where(p => intent.TemporalScope.Type switch
        {
            TemporalScopeType.BoundedPeriod => p.Features.SupportsDateRange,
            TemporalScopeType.RelativeWindow => p.Features.SupportsDateRange,
            TemporalScopeType.PointInTime => p.Features.SupportsPointInTime || p.Features.SupportsDateRange,
            _ => true
        });
    }
}
```

### PlanFeatureExtractor — Computed at ingestion time

```csharp
namespace CffPlanRouter.Index;

/// <summary>
/// Extracts a PlanFeatureVector from a PlanDefinition.
/// Run once at plan ingestion time, not at query time.
/// The output is deterministic and cacheable.
/// </summary>
public sealed class PlanFeatureExtractor
{
    private readonly DomainDictionary _domainDictionary;

    public PlanFeatureVector Extract(PlanDefinition plan)
    {
        var tools = plan.Steps.Select(s => s.ToolName).ToHashSet();
        var allInputs = plan.Steps.SelectMany(s => s.InputSchema.Keys).ToHashSet();
        var allOutputs = plan.Steps.SelectMany(s => s.OutputFields).ToHashSet();

        return new PlanFeatureVector
        {
            Domain = InferDomain(tools),
            SubDomain = InferSubDomain(tools, plan.Description),
            PrimaryAction = InferPrimaryAction(plan.Steps),
            SecondaryActions = InferSecondaryActions(plan.Steps),
            ToolNames = tools,
            StepCount = plan.Steps.Count,
            RequiresCrossReference = DetectCrossReference(plan.Steps),
            RequiredEntityTypes = InferRequiredEntities(allInputs),
            OptionalEntityTypes = InferOptionalEntities(allInputs, plan.Steps),
            TemporalScope = InferTemporalScope(allInputs),
            SupportsDateRange = allInputs.Any(i => i.Contains("Date") || i.Contains("Period") || i.Contains("Start")),
            SupportsPointInTime = allInputs.Any(i => i.Contains("AsOf") || i.Contains("PointInTime")),
            OutputFormat = InferOutputFormat(allOutputs, plan.Steps),
            OutputFieldNames = allOutputs,
            Discriminators = ExtractDiscriminators(plan)
        };
    }

    private string InferDomain(IReadOnlySet<string> tools)
    {
        // Tool naming convention: domain_verb_noun (e.g., sales_list_orders)
        var domainPrefixes = tools
            .Select(t => t.Split('_').FirstOrDefault() ?? "")
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? "general";

        return domainPrefixes;
    }

    private DomainAction InferPrimaryAction(IReadOnlyList<PlanStep> steps)
    {
        // The LAST step's action typically defines the plan's primary purpose
        var lastStep = steps.OrderByDescending(s => s.StepId).First();

        return lastStep.Action.ToLowerInvariant() switch
        {
            var a when a.Contains("list") || a.Contains("fetch") || a.Contains("get") => DomainAction.List,
            var a when a.Contains("create") || a.Contains("generate") || a.Contains("produce") => DomainAction.Create,
            var a when a.Contains("compute") || a.Contains("calculate") || a.Contains("sum") => DomainAction.Compute,
            var a when a.Contains("compare") || a.Contains("reconcile") || a.Contains("match") => DomainAction.Compare,
            var a when a.Contains("forecast") || a.Contains("predict") || a.Contains("project") => DomainAction.Forecast,
            var a when a.Contains("audit") || a.Contains("flag") || a.Contains("detect") => DomainAction.Audit,
            var a when a.Contains("categorize") || a.Contains("classify") || a.Contains("group") => DomainAction.Categorize,
            var a when a.Contains("optimize") || a.Contains("recommend") => DomainAction.Optimize,
            _ => DomainAction.Compute // Default
        };
    }

    private bool DetectCrossReference(IReadOnlyList<PlanStep> steps)
    {
        // A plan cross-references if it has multiple fetch steps whose outputs
        // feed into a comparison/matching step
        var fetchSteps = steps.Where(s =>
            s.Action.Contains("fetch", StringComparison.OrdinalIgnoreCase) ||
            s.Action.Contains("list", StringComparison.OrdinalIgnoreCase) ||
            s.Action.Contains("get", StringComparison.OrdinalIgnoreCase)).ToList();

        var compareSteps = steps.Where(s =>
            s.Action.Contains("match", StringComparison.OrdinalIgnoreCase) ||
            s.Action.Contains("compare", StringComparison.OrdinalIgnoreCase) ||
            s.Action.Contains("reconcile", StringComparison.OrdinalIgnoreCase) ||
            s.Action.Contains("cross", StringComparison.OrdinalIgnoreCase)).ToList();

        return fetchSteps.Count >= 2 && compareSteps.Count >= 1;
    }

    private IReadOnlyDictionary<string, string> ExtractDiscriminators(PlanDefinition plan)
    {
        var discriminators = new Dictionary<string, string>();

        // Aggregation type
        var hasGroupBy = plan.Steps.Any(s =>
            s.InputSchema.Keys.Any(k => k.Contains("GroupBy", StringComparison.OrdinalIgnoreCase)));
        if (hasGroupBy)
        {
            var groupField = plan.Steps
                .SelectMany(s => s.InputSchema)
                .FirstOrDefault(kv => kv.Key.Contains("GroupBy", StringComparison.OrdinalIgnoreCase));
            discriminators["aggregation"] = groupField.Value ?? "unknown";
        }

        // Filter type
        var hasFilter = plan.Steps.Any(s =>
            s.InputSchema.Keys.Any(k => k.Contains("Filter", StringComparison.OrdinalIgnoreCase)));
        if (hasFilter)
        {
            discriminators["filter_type"] = "filtered";
        }

        // Cross-reference
        if (plan.Features?.RequiresCrossReference ?? DetectCrossReference(plan.Steps))
        {
            discriminators["cross_reference"] = "true";
        }

        return discriminators;
    }
}
```

### ClusterBuilder — Groups plans at startup

```csharp
namespace CffPlanRouter.Index;

/// <summary>
/// Groups plans into clusters based on shared structural features.
/// A cluster represents "plans that serve the same general purpose but differ in specifics."
/// Discriminators are the features that distinguish plans within a cluster.
/// </summary>
public sealed class ClusterBuilder
{
    public IReadOnlyDictionary<string, PlanCluster> Build(IReadOnlyList<PlanDefinition> plans)
    {
        // Cluster key = Domain + SubDomain + PrimaryAction
        var groups = plans.GroupBy(p =>
            $"{p.Features.Domain}:{p.Features.SubDomain}:{p.Features.PrimaryAction}");

        var clusters = new Dictionary<string, PlanCluster>();

        foreach (var group in groups)
        {
            var plansInCluster = group.ToList();
            var discriminators = IdentifyDiscriminators(plansInCluster);

            var cluster = new PlanCluster
            {
                ClusterId = group.Key,
                Domain = plansInCluster[0].Features.Domain,
                SubDomain = plansInCluster[0].Features.SubDomain,
                PrimaryAction = plansInCluster[0].Features.PrimaryAction,
                Plans = plansInCluster,
                Discriminators = discriminators
            };

            clusters[group.Key] = cluster;
        }

        return clusters;
    }

    private IReadOnlyList<ClusterDiscriminator> IdentifyDiscriminators(List<PlanDefinition> plans)
    {
        if (plans.Count <= 1)
            return [];

        var discriminators = new List<ClusterDiscriminator>();

        // Find features that VARY across plans in this cluster
        // These are what distinguish one plan from another

        // Tool set variation
        var toolSets = plans.Select(p => p.Features.ToolNames).ToList();
        if (toolSets.Distinct(HashSet<string>.CreateSetComparer()).Count() > 1)
        {
            discriminators.Add(new ClusterDiscriminator
            {
                FeatureName = "tool_chain",
                PossibleValues = toolSets.Select(ts => string.Join("+", ts.OrderBy(t => t))).Distinct().ToList(),
                Description = "Different tool combinations serve different sub-tasks"
            });
        }

        // Entity requirement variation
        var entitySets = plans.Select(p => p.Features.RequiredEntityTypes).ToList();
        if (entitySets.Distinct(HashSet<string>.CreateSetComparer()).Count() > 1)
        {
            discriminators.Add(new ClusterDiscriminator
            {
                FeatureName = "required_entities",
                PossibleValues = entitySets.Select(es => string.Join("+", es.OrderBy(e => e))).Distinct().ToList(),
                Description = "Different plans need different input entities"
            });
        }

        // Step count variation (complexity)
        var stepCounts = plans.Select(p => p.Features.StepCount).Distinct().ToList();
        if (stepCounts.Count > 1)
        {
            discriminators.Add(new ClusterDiscriminator
            {
                FeatureName = "complexity",
                PossibleValues = stepCounts.Select(c => c.ToString()).ToList(),
                Description = "Plans vary in number of steps (simple vs detailed)"
            });
        }

        return discriminators;
    }
}

public sealed record PlanCluster
{
    public required string ClusterId { get; init; }
    public required string Domain { get; init; }
    public required string SubDomain { get; init; }
    public required DomainAction PrimaryAction { get; init; }
    public required IReadOnlyList<PlanDefinition> Plans { get; init; }
    public required IReadOnlyList<ClusterDiscriminator> Discriminators { get; init; }
}

public sealed record ClusterDiscriminator
{
    public required string FeatureName { get; init; }
    public required IReadOnlyList<string> PossibleValues { get; init; }
    public required string Description { get; init; }
}
```

---

## Layer 3: Plan Ranking

When Layer 2 returns multiple candidates (common when a cluster has several plan variants), Layer 3 ranks them.

### Interface

```csharp
namespace CffPlanRouter.Ranking;

public interface IPlanRanker
{
    /// <summary>
    /// Ranks candidate plans by how well they satisfy the query intent.
    /// Returns candidates in descending order of suitability.
    /// </summary>
    Task<RankingResult> RankAsync(QueryIntent intent, IReadOnlyList<CandidateResult> candidates);
}

public sealed record RankingResult
{
    public required IReadOnlyList<RankedCandidate> Candidates { get; init; }
    public required string Reasoning { get; init; }       // Why this ordering
    public required float TopConfidence { get; init; }    // How confident are we in #1
    public required bool IsAmbiguous { get; init; }       // Top 2 are too close to call
}

public sealed record RankedCandidate
{
    public required PlanDefinition Plan { get; init; }
    public required float Score { get; init; }
    public required string Explanation { get; init; }     // Why this plan ranked here
}
```

### Deterministic Ranker (no LLM)

```csharp
namespace CffPlanRouter.Ranking;

/// <summary>
/// Scores candidates by weighted feature alignment.
/// Deterministic, sub-millisecond, fully debuggable.
/// Used when LLM is unavailable or when candidates are clearly differentiated.
/// </summary>
public sealed class FeatureAlignmentRanker : IPlanRanker
{
    private readonly ScoringWeights _weights;

    public Task<RankingResult> RankAsync(QueryIntent intent, IReadOnlyList<CandidateResult> candidates)
    {
        if (candidates.Count == 0)
        {
            return Task.FromResult(new RankingResult
            {
                Candidates = [],
                Reasoning = "No candidates to rank",
                TopConfidence = 0f,
                IsAmbiguous = false
            });
        }

        if (candidates.Count == 1)
        {
            return Task.FromResult(new RankingResult
            {
                Candidates = [new RankedCandidate
                {
                    Plan = candidates[0].Plan,
                    Score = candidates[0].AlignmentScore,
                    Explanation = $"Only candidate. Matched: [{string.Join(", ", candidates[0].MatchedFeatures)}]"
                }],
                Reasoning = "Single candidate — no ranking needed",
                TopConfidence = candidates[0].AlignmentScore,
                IsAmbiguous = false
            });
        }

        var ranked = candidates
            .Select(c => new RankedCandidate
            {
                Plan = c.Plan,
                Score = ComputeRankingScore(c, intent),
                Explanation = BuildExplanation(c, intent)
            })
            .OrderByDescending(r => r.Score)
            .ToList();

        var topTwo = ranked.Take(2).ToList();
        var isAmbiguous = topTwo.Count == 2 && (topTwo[0].Score - topTwo[1].Score) < 0.1f;

        return Task.FromResult(new RankingResult
        {
            Candidates = ranked,
            Reasoning = isAmbiguous
                ? $"Top 2 candidates are within 0.1 of each other — ambiguous"
                : $"Clear winner: {ranked[0].Plan.PlanId} (score gap: {ranked[0].Score - (ranked.Count > 1 ? ranked[1].Score : 0):F2})",
            TopConfidence = ranked[0].Score,
            IsAmbiguous = isAmbiguous
        });
    }

    private float ComputeRankingScore(CandidateResult candidate, QueryIntent intent)
    {
        var baseScore = candidate.AlignmentScore;

        // Bonus: plan complexity matches query complexity
        var queryComplexity = EstimateQueryComplexity(intent);
        var planComplexity = candidate.Plan.Features.StepCount;
        var complexityMatch = 1f - Math.Abs(queryComplexity - planComplexity) * 0.05f;
        baseScore *= Math.Max(0.5f, complexityMatch);

        // Bonus: output fields match what the query seems to want
        var outputOverlap = ComputeOutputRelevance(candidate.Plan, intent);
        baseScore = baseScore * 0.8f + outputOverlap * 0.2f;

        // Penalty: plan has unmet required entities
        var unmetCount = candidate.MismatchedFeatures.Count(f => f.StartsWith("entities:"));
        baseScore -= unmetCount * 0.15f;

        return Math.Clamp(baseScore, 0f, 1f);
    }
}
```

### LLM Ranker (for ambiguous cases)

```csharp
namespace CffPlanRouter.Ranking;

/// <summary>
/// Uses Claude to select the best plan when deterministic ranking is ambiguous.
/// Only called when FeatureAlignmentRanker reports IsAmbiguous=true.
/// This is the ONLY LLM call in the retrieval path (Layer 1 parsing is separate).
/// </summary>
public sealed class LlmPlanJudge : IPlanRanker
{
    private readonly BedrockClient _bedrock;
    private readonly FeatureAlignmentRanker _deterministicRanker;

    private const string SystemPrompt = """
        You are a plan selection judge for a financial application.
        
        Given a user's structured intent and 2-5 candidate execution plans,
        select the plan that BEST satisfies the user's needs.
        
        Consider:
        1. Does the plan retrieve ALL the data the user needs?
        2. Does the plan apply the correct transformations/filters?
        3. Does the plan produce output in the format the user expects?
        4. Is the plan's complexity appropriate (not over-engineered for a simple query)?
        
        Respond with JSON:
        {
          "selectedPlanId": "...",
          "confidence": 0.0-1.0,
          "reasoning": "one sentence explaining why this plan is best",
          "rejectionReasons": { "planId": "why it was rejected", ... }
        }
        
        If NO plan adequately satisfies the query, respond:
        {
          "selectedPlanId": null,
          "confidence": 0.0,
          "reasoning": "why none of the plans work",
          "rejectionReasons": { ... }
        }
        """;

    public async Task<RankingResult> RankAsync(QueryIntent intent, IReadOnlyList<CandidateResult> candidates)
    {
        // First try deterministic ranking
        var deterministicResult = await _deterministicRanker.RankAsync(intent, candidates);

        // Only invoke LLM if ambiguous
        if (!deterministicResult.IsAmbiguous)
            return deterministicResult;

        // Build LLM prompt with structured plan summaries (not raw YAML)
        var prompt = BuildJudgePrompt(intent, candidates);
        var response = await _bedrock.ConverseAsync(SystemPrompt, prompt, maxTokens: 200);
        var judgment = JsonSerializer.Deserialize<JudgmentDto>(response);

        if (judgment?.SelectedPlanId is null)
        {
            return deterministicResult with
            {
                Reasoning = $"LLM judge rejected all candidates: {judgment?.Reasoning}",
                TopConfidence = 0f
            };
        }

        // Reorder candidates based on LLM judgment
        var selected = candidates.FirstOrDefault(c => c.Plan.PlanId == judgment.SelectedPlanId);
        if (selected is null)
            return deterministicResult; // LLM hallucinated a plan ID — fall back

        var reranked = candidates
            .OrderByDescending(c => c.Plan.PlanId == judgment.SelectedPlanId ? 1f : 0f)
            .ThenByDescending(c => c.AlignmentScore)
            .Select(c => new RankedCandidate
            {
                Plan = c.Plan,
                Score = c.Plan.PlanId == judgment.SelectedPlanId ? judgment.Confidence : c.AlignmentScore * 0.5f,
                Explanation = c.Plan.PlanId == judgment.SelectedPlanId
                    ? judgment.Reasoning
                    : judgment.RejectionReasons.GetValueOrDefault(c.Plan.PlanId, "Not selected")
            })
            .ToList();

        return new RankingResult
        {
            Candidates = reranked,
            Reasoning = $"LLM judge selected {judgment.SelectedPlanId}: {judgment.Reasoning}",
            TopConfidence = judgment.Confidence,
            IsAmbiguous = false
        };
    }

    private string BuildJudgePrompt(QueryIntent intent, IReadOnlyList<CandidateResult> candidates)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## User Intent (structured)");
        sb.AppendLine($"Action: {intent.PrimaryAction}");
        sb.AppendLine($"Domain: {intent.Domain}/{intent.SubDomain}");
        sb.AppendLine($"Temporal: {intent.TemporalScope?.Type} ({intent.TemporalScope?.RelativeExpression})");
        sb.AppendLine($"Entities: {string.Join(", ", intent.ExtractedSlots.Select(s => $"{s.Name}={s.Value}"))}");
        sb.AppendLine($"Constraints: {string.Join(", ", intent.Constraints.Select(c => $"{c.Field} {c.Operator} {c.Value}"))}");
        sb.AppendLine($"Expected output: {intent.ExpectedOutput}");
        sb.AppendLine();

        sb.AppendLine("## Candidate Plans");
        foreach (var (candidate, i) in candidates.Select((c, i) => (c, i)))
        {
            sb.AppendLine($"### Plan {i + 1}: {candidate.Plan.PlanId}");
            sb.AppendLine($"Tools: {string.Join(" → ", candidate.Plan.Steps.Select(s => s.ToolName))}");
            sb.AppendLine($"Required inputs: {string.Join(", ", candidate.Plan.RequiredInputSlots)}");
            sb.AppendLine($"Produces: {string.Join(", ", candidate.Plan.ProducedOutputFields)}");
            sb.AppendLine($"Feature alignment: {candidate.AlignmentScore:P0}");
            sb.AppendLine($"Matched: {string.Join(", ", candidate.MatchedFeatures)}");
            sb.AppendLine($"Mismatched: {string.Join(", ", candidate.MismatchedFeatures)}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
```

---

## Layer 4: Plan Validation

Before executing, verify the selected plan can actually run.

```csharp
namespace CffPlanRouter.Validation;

public interface IPlanValidator
{
    /// <summary>
    /// Validates that a selected plan can execute given the available context.
    /// Returns Pass (execute), Warn (execute with caveats), or Fail (reject).
    /// </summary>
    ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings);
}

public sealed record ValidationResult
{
    public required ValidationStatus Status { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<string> MissingSlots { get; init; }
    public required IReadOnlyList<string> DefaultedSlots { get; init; }  // Slots filled with defaults
}

public enum ValidationStatus
{
    Pass,       // All clear — execute
    Warn,       // Can execute but some slots are defaulted or uncertain
    Fail        // Cannot execute — missing critical inputs
}
```

### Composite Validator

```csharp
namespace CffPlanRouter.Validation;

public sealed class CompositeValidator : IPlanValidator
{
    private readonly IReadOnlyList<IPlanValidator> _validators;

    public CompositeValidator(
        SlotCoverageValidator slotValidator,
        ToolAvailabilityValidator toolValidator,
        SchemaCompatibilityValidator schemaValidator)
    {
        _validators = [slotValidator, toolValidator, schemaValidator];
    }

    public ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings)
    {
        var allErrors = new List<string>();
        var allWarnings = new List<string>();
        var allMissing = new List<string>();
        var allDefaulted = new List<string>();

        foreach (var validator in _validators)
        {
            var result = validator.Validate(plan, intent, bindings);
            allErrors.AddRange(result.Errors);
            allWarnings.AddRange(result.Warnings);
            allMissing.AddRange(result.MissingSlots);
            allDefaulted.AddRange(result.DefaultedSlots);
        }

        var status = allErrors.Count > 0
            ? ValidationStatus.Fail
            : allWarnings.Count > 0
                ? ValidationStatus.Warn
                : ValidationStatus.Pass;

        return new ValidationResult
        {
            Status = status,
            Errors = allErrors,
            Warnings = allWarnings,
            MissingSlots = allMissing,
            DefaultedSlots = allDefaulted
        };
    }
}
```

### SlotCoverageValidator

```csharp
namespace CffPlanRouter.Validation;

/// <summary>
/// Checks that every required input slot in the plan has a bound value.
/// </summary>
public sealed class SlotCoverageValidator : IPlanValidator
{
    public ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings)
    {
        var boundSlotNames = bindings.Select(b => b.SlotName).ToHashSet();
        var errors = new List<string>();
        var warnings = new List<string>();
        var missing = new List<string>();
        var defaulted = new List<string>();

        foreach (var requiredSlot in plan.RequiredInputSlots)
        {
            if (boundSlotNames.Contains(requiredSlot))
                continue;

            // Check if there's a default value in the plan
            var hasDefault = plan.Steps
                .SelectMany(s => s.InputSchema)
                .Any(kv => kv.Key == requiredSlot && !string.IsNullOrEmpty(kv.Value));

            if (hasDefault)
            {
                defaulted.Add(requiredSlot);
                warnings.Add($"Slot '{requiredSlot}' not provided by query — using plan default");
            }
            else
            {
                missing.Add(requiredSlot);
                errors.Add($"Required slot '{requiredSlot}' has no value and no default");
            }
        }

        return new ValidationResult
        {
            Status = errors.Count > 0 ? ValidationStatus.Fail : warnings.Count > 0 ? ValidationStatus.Warn : ValidationStatus.Pass,
            Errors = errors,
            Warnings = warnings,
            MissingSlots = missing,
            DefaultedSlots = defaulted
        };
    }
}
```

### SchemaCompatibilityValidator

```csharp
namespace CffPlanRouter.Validation;

/// <summary>
/// Validates that each step's required inputs can be satisfied by
/// either the query's slot bindings or a preceding step's outputs.
/// This catches plans where the tool chain is internally inconsistent.
/// </summary>
public sealed class SchemaCompatibilityValidator : IPlanValidator
{
    public ValidationResult Validate(PlanDefinition plan, QueryIntent intent, IReadOnlyList<SlotBinding> bindings)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Available data starts with query bindings
        var availableFields = new HashSet<string>(bindings.Select(b => b.SlotName));

        // Process steps in topological order
        var orderedSteps = TopologicalSort(plan.Steps);

        foreach (var step in orderedSteps)
        {
            var requiredInputs = step.InputSchema.Keys.ToList();
            var unmet = requiredInputs.Where(input => !availableFields.Contains(input)).ToList();

            if (unmet.Count > 0)
            {
                // Check if any preceding step produces these fields
                var producedByPredecessors = plan.Steps
                    .Where(s => step.DependsOn.Contains(s.StepId))
                    .SelectMany(s => s.OutputFields)
                    .ToHashSet();

                var stillUnmet = unmet.Where(u => !producedByPredecessors.Contains(u)).ToList();

                if (stillUnmet.Count > 0)
                {
                    errors.Add($"Step {step.StepId} ({step.ToolName}) requires [{string.Join(", ", stillUnmet)}] " +
                               $"but no preceding step produces them and they're not in query bindings");
                }
            }

            // After this step executes, its outputs become available
            foreach (var output in step.OutputFields)
                availableFields.Add(output);
        }

        return new ValidationResult
        {
            Status = errors.Count > 0 ? ValidationStatus.Fail : ValidationStatus.Pass,
            Errors = errors,
            Warnings = warnings,
            MissingSlots = [],
            DefaultedSlots = []
        };
    }

    private static IReadOnlyList<PlanStep> TopologicalSort(IReadOnlyList<PlanStep> steps)
    {
        var sorted = new List<PlanStep>();
        var visited = new HashSet<int>();

        void Visit(PlanStep step)
        {
            if (visited.Contains(step.StepId)) return;
            visited.Add(step.StepId);
            foreach (var depId in step.DependsOn)
            {
                var dep = steps.FirstOrDefault(s => s.StepId == depId);
                if (dep is not null) Visit(dep);
            }
            sorted.Add(step);
        }

        foreach (var step in steps) Visit(step);
        return sorted;
    }
}
```

---

## Pipeline Orchestrator

```csharp
namespace CffPlanRouter.Host;

/// <summary>
/// Orchestrates the four layers into a single routing decision.
/// Each layer is independent, testable, and replaceable.
/// The pipeline produces a full diagnostic trace for every decision.
/// </summary>
public sealed class PlanRoutingPipeline
{
    private readonly IQueryParser _queryParser;
    private readonly IPlanIndex _planIndex;
    private readonly IPlanRanker _ranker;
    private readonly IPlanValidator _validator;
    private readonly IPlanExecutor _executor;
    private readonly ILogger<PlanRoutingPipeline> _logger;

    public async Task<RoutingDecision> RouteAsync(string rawQuery, ConversationContext? context = null)
    {
        var trace = new RoutingTrace(rawQuery);
        var stopwatch = Stopwatch.StartNew();

        // ═══════════════════════════════════════════════════════════════
        // Layer 1: Query Understanding
        // ═══════════════════════════════════════════════════════════════
        trace.BeginLayer("Understanding");

        var intent = await _queryParser.ParseAsync(rawQuery, context);
        trace.RecordLayerResult("Understanding", new
        {
            intent.PrimaryAction,
            intent.Domain,
            intent.SubDomain,
            intent.DomainConfidence,
            Slots = intent.ExtractedSlots.Select(s => $"{s.Name}={s.Value}"),
            intent.TemporalScope,
            intent.OverallConfidence
        });

        // Gate: if confidence is too low, reject early
        if (intent.OverallConfidence < 0.3f)
        {
            return RoutingDecision.Rejected(
                "Could not understand the query with sufficient confidence",
                trace.Complete(stopwatch.Elapsed));
        }

        // Handle multi-intent by recursing
        if (intent.SubIntents.Count > 0)
        {
            return await HandleMultiIntentAsync(intent, context, trace, stopwatch);
        }

        // ═══════════════════════════════════════════════════════════════
        // Layer 2: Plan Retrieval
        // ═══════════════════════════════════════════════════════════════
        trace.BeginLayer("Retrieval");

        var candidates = _planIndex.Retrieve(intent, maxCandidates: 5);
        trace.RecordLayerResult("Retrieval", new
        {
            CandidateCount = candidates.Count,
            TopCandidate = candidates.FirstOrDefault()?.Plan.PlanId,
            TopScore = candidates.FirstOrDefault()?.AlignmentScore,
            Candidates = candidates.Select(c => new { c.Plan.PlanId, c.AlignmentScore, c.MatchedFeatures })
        });

        // Gate: no candidates found
        if (candidates.Count == 0)
        {
            return RoutingDecision.NoPlanFound(
                $"No plans match domain={intent.Domain}, action={intent.PrimaryAction}",
                trace.Complete(stopwatch.Elapsed));
        }

        // ═══════════════════════════════════════════════════════════════
        // Layer 3: Plan Ranking
        // ═══════════════════════════════════════════════════════════════
        trace.BeginLayer("Ranking");

        var rankingResult = await _ranker.RankAsync(intent, candidates);
        trace.RecordLayerResult("Ranking", new
        {
            rankingResult.TopConfidence,
            rankingResult.IsAmbiguous,
            rankingResult.Reasoning,
            Winner = rankingResult.Candidates.FirstOrDefault()?.Plan.PlanId
        });

        // Gate: ranking confidence too low
        if (rankingResult.TopConfidence < 0.4f)
        {
            return RoutingDecision.LowConfidence(
                $"Best candidate scored {rankingResult.TopConfidence:P0} — below threshold",
                rankingResult.Candidates.Select(c => c.Plan.PlanId).ToList(),
                trace.Complete(stopwatch.Elapsed));
        }

        var selectedPlan = rankingResult.Candidates[0].Plan;

        // ═══════════════════════════════════════════════════════════════
        // Layer 4: Plan Validation
        // ═══════════════════════════════════════════════════════════════
        trace.BeginLayer("Validation");

        var bindings = BuildSlotBindings(intent, selectedPlan);
        var validation = _validator.Validate(selectedPlan, intent, bindings);
        trace.RecordLayerResult("Validation", new
        {
            validation.Status,
            validation.Errors,
            validation.Warnings,
            validation.MissingSlots,
            validation.DefaultedSlots
        });

        switch (validation.Status)
        {
            case ValidationStatus.Fail:
                // Try next candidate
                var fallback = await TryNextCandidateAsync(intent, rankingResult, trace);
                if (fallback is not null)
                    return fallback with { Trace = trace.Complete(stopwatch.Elapsed) };

                return RoutingDecision.ValidationFailed(
                    $"Plan {selectedPlan.PlanId} cannot execute: {string.Join("; ", validation.Errors)}",
                    trace.Complete(stopwatch.Elapsed));

            case ValidationStatus.Warn:
                _logger.LogWarning("Plan {PlanId} executing with warnings: {Warnings}",
                    selectedPlan.PlanId, string.Join("; ", validation.Warnings));
                break;
        }

        // ═══════════════════════════════════════════════════════════════
        // Success: Return selected plan with bindings
        // ═══════════════════════════════════════════════════════════════
        return RoutingDecision.Success(
            new SelectedPlan
            {
                Plan = selectedPlan,
                Confidence = rankingResult.TopConfidence,
                SlotBindings = bindings,
                ValidationResult = validation,
                Explanation = rankingResult.Candidates[0].Explanation
            },
            trace.Complete(stopwatch.Elapsed));
    }

    private async Task<RoutingDecision> HandleMultiIntentAsync(
        QueryIntent intent, ConversationContext? context, RoutingTrace trace, Stopwatch stopwatch)
    {
        trace.RecordNote($"Multi-intent detected: {intent.SubIntents.Count} sub-intents");

        var results = new List<SelectedPlan>();
        foreach (var subIntent in intent.SubIntents)
        {
            // Recurse for each sub-intent (without multi-intent detection to prevent infinite recursion)
            var subResult = await RouteSubIntentAsync(subIntent, context);
            if (subResult.Status == RoutingStatus.Success)
            {
                results.Add(subResult.SelectedPlan!);
            }
            else
            {
                trace.RecordNote($"Sub-intent {subIntent.PrimaryAction}/{subIntent.Domain} failed: {subResult.Message}");
            }
        }

        if (results.Count == 0)
        {
            return RoutingDecision.NoPlanFound(
                "None of the detected sub-intents could be matched to a plan",
                trace.Complete(stopwatch.Elapsed));
        }

        return RoutingDecision.MultiSuccess(results, trace.Complete(stopwatch.Elapsed));
    }

    private IReadOnlyList<SlotBinding> BuildSlotBindings(QueryIntent intent, PlanDefinition plan)
    {
        var bindings = new List<SlotBinding>();

        // Bind from extracted slots
        foreach (var slot in intent.ExtractedSlots.Where(s => s.Confidence >= 0.5f))
        {
            bindings.Add(new SlotBinding
            {
                SlotName = slot.Name,
                Value = slot.Value,
                Source = BindingSource.QueryExtraction,
                Confidence = slot.Confidence
            });
        }

        // Bind temporal scope
        if (intent.TemporalScope is not null)
        {
            if (intent.TemporalScope.Start.HasValue)
                bindings.Add(new SlotBinding { SlotName = "startDate", Value = intent.TemporalScope.Start.Value.ToString("yyyy-MM-dd"), Source = BindingSource.TemporalResolution, Confidence = 0.9f });
            if (intent.TemporalScope.End.HasValue)
                bindings.Add(new SlotBinding { SlotName = "endDate", Value = intent.TemporalScope.End.Value.ToString("yyyy-MM-dd"), Source = BindingSource.TemporalResolution, Confidence = 0.9f });
        }

        // Bind constraints as filter parameters
        foreach (var constraint in intent.Constraints)
        {
            bindings.Add(new SlotBinding
            {
                SlotName = $"filter_{constraint.Field}",
                Value = $"{constraint.Operator}:{constraint.Value}",
                Source = BindingSource.ConstraintExtraction,
                Confidence = 0.8f
            });
        }

        return bindings;
    }

    private async Task<RoutingDecision?> TryNextCandidateAsync(
        QueryIntent intent, RankingResult ranking, RoutingTrace trace)
    {
        // Try candidates 2–N if #1 failed validation
        foreach (var candidate in ranking.Candidates.Skip(1))
        {
            var bindings = BuildSlotBindings(intent, candidate.Plan);
            var validation = _validator.Validate(candidate.Plan, intent, bindings);

            if (validation.Status != ValidationStatus.Fail)
            {
                trace.RecordNote($"Fell back to candidate #{ranking.Candidates.ToList().IndexOf(candidate) + 1}: {candidate.Plan.PlanId}");

                return RoutingDecision.Success(
                    new SelectedPlan
                    {
                        Plan = candidate.Plan,
                        Confidence = candidate.Score * 0.9f, // Slight penalty for being a fallback
                        SlotBindings = bindings,
                        ValidationResult = validation,
                        Explanation = $"Fallback: {candidate.Explanation}"
                    },
                    default); // Trace completed by caller
            }
        }

        return null;
    }
}
```

---

## Routing Decision — Output Model

```csharp
namespace CffPlanRouter.Domain.Matching;

public sealed record RoutingDecision
{
    public required RoutingStatus Status { get; init; }
    public required string Message { get; init; }
    public SelectedPlan? SelectedPlan { get; init; }
    public IReadOnlyList<SelectedPlan>? MultiPlans { get; init; }  // For multi-intent
    public IReadOnlyList<string>? ConsideredPlanIds { get; init; }
    public required RoutingTraceResult Trace { get; init; }

    public static RoutingDecision Success(SelectedPlan plan, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.Success,
        Message = $"Matched plan {plan.Plan.PlanId} with {plan.Confidence:P0} confidence",
        SelectedPlan = plan,
        Trace = trace
    };

    public static RoutingDecision MultiSuccess(IReadOnlyList<SelectedPlan> plans, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.Success,
        Message = $"Matched {plans.Count} plans for multi-intent query",
        MultiPlans = plans,
        Trace = trace
    };

    public static RoutingDecision Rejected(string reason, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.Rejected,
        Message = reason,
        Trace = trace
    };

    public static RoutingDecision NoPlanFound(string reason, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.NoPlanFound,
        Message = reason,
        Trace = trace
    };

    public static RoutingDecision LowConfidence(string reason, IReadOnlyList<string> considered, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.LowConfidence,
        Message = reason,
        ConsideredPlanIds = considered,
        Trace = trace
    };

    public static RoutingDecision ValidationFailed(string reason, RoutingTraceResult trace) => new()
    {
        Status = RoutingStatus.ValidationFailed,
        Message = reason,
        Trace = trace
    };
}

public enum RoutingStatus
{
    Success,
    Rejected,           // Query not understood
    NoPlanFound,        // Understood but no matching plan exists
    LowConfidence,      // Candidates found but none scored high enough
    ValidationFailed    // Best plan can't execute with available inputs
}

public sealed record SelectedPlan
{
    public required PlanDefinition Plan { get; init; }
    public required float Confidence { get; init; }
    public required IReadOnlyList<SlotBinding> SlotBindings { get; init; }
    public required ValidationResult ValidationResult { get; init; }
    public required string Explanation { get; init; }
}

public sealed record SlotBinding
{
    public required string SlotName { get; init; }
    public required string Value { get; init; }
    public required BindingSource Source { get; init; }
    public required float Confidence { get; init; }
}

public enum BindingSource
{
    QueryExtraction,        // Directly from user query
    TemporalResolution,     // Resolved from relative date expression
    ConstraintExtraction,   // Derived from a filter/condition
    SessionContext,         // Carried over from conversation history
    PlanDefault             // Default value defined in the plan
}
```

---

## Diagnostics — Full Routing Trace

Every routing decision carries a complete trace for debugging:

```csharp
namespace CffPlanRouter.Host.Diagnostics;

/// <summary>
/// Records every decision point in the routing pipeline.
/// This is what you look at when a query routes to the wrong plan.
/// Unlike the original design where you'd have to guess which of 7 stages went wrong,
/// this trace tells you exactly what each layer saw, decided, and why.
/// </summary>
public sealed class RoutingTrace
{
    private readonly string _rawQuery;
    private readonly List<LayerTrace> _layers = [];
    private readonly List<string> _notes = [];

    public RoutingTrace(string rawQuery) => _rawQuery = rawQuery;

    public void BeginLayer(string layerName)
    {
        _layers.Add(new LayerTrace
        {
            LayerName = layerName,
            StartedAt = Stopwatch.GetTimestamp()
        });
    }

    public void RecordLayerResult(string layerName, object result)
    {
        var layer = _layers.Last(l => l.LayerName == layerName);
        layer.Result = result;
        layer.ElapsedMs = GetElapsedMs(layer.StartedAt);
    }

    public void RecordNote(string note) => _notes.Add(note);

    public RoutingTraceResult Complete(TimeSpan totalElapsed) => new()
    {
        RawQuery = _rawQuery,
        TotalElapsedMs = totalElapsed.TotalMilliseconds,
        Layers = _layers.Select(l => new LayerTraceResult
        {
            LayerName = l.LayerName,
            ElapsedMs = l.ElapsedMs,
            Result = l.Result
        }).ToList(),
        Notes = _notes
    };
}

public sealed record RoutingTraceResult
{
    public required string RawQuery { get; init; }
    public required double TotalElapsedMs { get; init; }
    public required IReadOnlyList<LayerTraceResult> Layers { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>
    /// Renders a human-readable diagnostic for console or logging.
    /// </summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"═══ Routing Trace ═══");
        sb.AppendLine($"Query: \"{RawQuery}\"");
        sb.AppendLine($"Total: {TotalElapsedMs:F1} ms");
        sb.AppendLine();

        foreach (var layer in Layers)
        {
            sb.AppendLine($"┌─ {layer.LayerName} ({layer.ElapsedMs:F1} ms)");
            sb.AppendLine($"│  {JsonSerializer.Serialize(layer.Result, new JsonSerializerOptions { WriteIndented = true })}");
            sb.AppendLine($"└─");
        }

        if (Notes.Count > 0)
        {
            sb.AppendLine("Notes:");
            foreach (var note in Notes)
                sb.AppendLine($"  • {note}");
        }

        return sb.ToString();
    }
}
```

---

## Plan Ingestion Tool

The indexer CLI processes raw plan files and builds the structural index:

```csharp
namespace CffPlanRouter.Indexer;

/// <summary>
/// Offline tool that processes plan YAML files and produces:
/// 1. PlanDefinition objects with computed PlanFeatureVectors
/// 2. Plan clusters with identified discriminators
/// 3. A serialized index file (_index.json) for fast startup
/// 
/// Run this whenever plans change. The runtime loads the pre-built index.
/// </summary>
public sealed class PlanIngestionPipeline
{
    private readonly PlanFeatureExtractor _featureExtractor;
    private readonly ClusterBuilder _clusterBuilder;

    public IngestionResult Ingest(string plansDirectory)
    {
        var planFiles = Directory.GetFiles(plansDirectory, "*.yaml", SearchOption.AllDirectories);
        var plans = new List<PlanDefinition>();
        var errors = new List<string>();

        foreach (var file in planFiles)
        {
            try
            {
                var raw = LoadPlanYaml(file);
                var features = _featureExtractor.Extract(raw);
                var plan = raw with { Features = features };
                plans.Add(plan);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // Build clusters
        var clusters = _clusterBuilder.Build(plans);

        // Validate: warn about clusters with no discriminators (plans that can't be distinguished)
        var ambiguousClusters = clusters.Values
            .Where(c => c.Plans.Count > 1 && c.Discriminators.Count == 0)
            .ToList();

        foreach (var cluster in ambiguousClusters)
        {
            errors.Add($"WARNING: Cluster '{cluster.ClusterId}' has {cluster.Plans.Count} plans " +
                       $"but no discriminating features — these plans are indistinguishable");
        }

        // Serialize index
        var index = new SerializedIndex
        {
            Plans = plans,
            Clusters = clusters,
            BuildTimestamp = DateTime.UtcNow,
            PlanCount = plans.Count,
            ClusterCount = clusters.Count
        };

        var outputPath = Path.Combine(plansDirectory, "_index.json");
        File.WriteAllText(outputPath, JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));

        return new IngestionResult
        {
            PlansProcessed = plans.Count,
            ClustersBuilt = clusters.Count,
            AmbiguousClusters = ambiguousClusters.Count,
            Errors = errors,
            IndexPath = outputPath
        };
    }
}
```

---

## Configuration

```json
// appsettings.json
{
  "PlanRouter": {
    "PlansDirectory": "./plans",
    "IndexPath": "./plans/_index.json",

    "Understanding": {
      "Provider": "LLM",           // "LLM" or "RuleBased"
      "MinConfidence": 0.3,
      "MaxConversationTurns": 3
    },

    "Retrieval": {
      "MaxCandidates": 5,
      "Filters": ["Domain", "Action", "Temporal", "Entity", "Tool"]
    },

    "Ranking": {
      "Provider": "Deterministic",  // "Deterministic" or "LlmJudge"
      "LlmJudgeThreshold": 0.1,    // Score gap below which LLM judge is invoked
      "Weights": {
        "Domain": 0.25,
        "SubDomain": 0.15,
        "Action": 0.20,
        "Temporal": 0.10,
        "EntityCoverage": 0.15,
        "OutputFormat": 0.05,
        "Discriminator": 0.10
      }
    },

    "Validation": {
      "FailOnMissingSlots": true,
      "AllowDefaultedSlots": true,
      "EnableSchemaCheck": true
    },

    "Bedrock": {
      "Region": "us-east-1",
      "QueryParserModel": "anthropic.claude-3-haiku-20240307-v1:0",
      "PlanJudgeModel": "anthropic.claude-3-haiku-20240307-v1:0",
      "MaxTokensParser": 300,
      "MaxTokensJudge": 200,
      "Temperature": 0.0
    }
  }
}
```

---

## Scoring Weights — Tunable and Measurable

```csharp
namespace CffPlanRouter.Ranking;

/// <summary>
/// Weights for each feature dimension in plan scoring.
/// These are the ONLY tuning knobs in the system.
/// Each weight has a clear semantic meaning and can be validated empirically:
/// "If I increase Domain weight, do domain-specific queries route more accurately?"
/// </summary>
public sealed record ScoringWeights
{
    public float Domain { get; init; } = 0.25f;          // How important is domain match?
    public float SubDomain { get; init; } = 0.15f;       // How important is sub-domain match?
    public float Action { get; init; } = 0.20f;          // How important is action type match?
    public float Temporal { get; init; } = 0.10f;        // How important is temporal compatibility?
    public float EntityCoverage { get; init; } = 0.15f;  // How important is entity slot coverage?
    public float OutputFormat { get; init; } = 0.05f;    // How important is output format match?
    public float Discriminator { get; init; } = 0.10f;   // How important are cluster discriminators?

    /// <summary>
    /// Validates that weights sum to 1.0 (within floating-point tolerance).
    /// </summary>
    public bool IsValid()
    {
        var sum = Domain + SubDomain + Action + Temporal + EntityCoverage + OutputFormat + Discriminator;
        return Math.Abs(sum - 1.0f) < 0.01f;
    }
}
```

---

## Testing Strategy

The architecture is designed so that **every layer is independently testable with deterministic inputs and outputs**:

```csharp
namespace CffPlanRouter.Index.Tests;

public class PlanIndexTests
{
    private readonly PlanIndex _index;

    public PlanIndexTests()
    {
        // Build index from known test plans — no YAML parsing, no LLM
        var plans = TestPlanFactory.CreateTestCorpus(); // 20 plans across 4 domains
        var clusters = new ClusterBuilder().Build(plans);
        _index = new PlanIndex(plans, clusters, ScoringWeights.Default);
    }

    [Fact]
    public void Retrieve_CashFlowQuery_ReturnsCashFlowPlans()
    {
        var intent = new QueryIntent
        {
            RawQuery = "show cash flow for last month",
            NormalizedQuery = "show cash flow for last month",
            PrimaryAction = DomainAction.Compute,
            ImpliedActions = new HashSet<DomainAction>(),
            Domain = "finance",
            SubDomain = "cashflow",
            DomainConfidence = 0.9f,
            ExtractedSlots = [new QuerySlot { Name = "period", Value = "last month", Type = "temporal", Confidence = 0.95f, IsExplicit = true }],
            TemporalScope = new TemporalScope { Type = TemporalScopeType.RelativeWindow, RelativeExpression = "last month" },
            Constraints = [],
            ExpectedOutput = OutputFormat.Summary,
            SubIntents = [],
            OverallConfidence = 0.9f,
            ParsingNotes = []
        };

        var candidates = _index.Retrieve(intent);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.Equal("finance", c.Plan.Features.Domain));
        Assert.Contains(candidates, c => c.Plan.Features.SubDomain == "cashflow");
        Assert.True(candidates[0].AlignmentScore > 0.7f);
    }

    [Fact]
    public void Retrieve_SalesQuery_DoesNotReturnFinancePlans()
    {
        var intent = new QueryIntent
        {
            PrimaryAction = DomainAction.List,
            Domain = "sales",
            SubDomain = "orders",
            DomainConfidence = 0.95f,
            // ... other fields
        };

        var candidates = _index.Retrieve(intent);

        Assert.All(candidates, c => Assert.NotEqual("finance", c.Plan.Features.Domain));
    }

    [Fact]
    public void Retrieve_QueryNeedingCrossReference_ReturnsOnlyCrossRefPlans()
    {
        var intent = new QueryIntent
        {
            PrimaryAction = DomainAction.Compare,
            Domain = "finance",
            SubDomain = "reconciliation",
            // ... other fields
        };

        var candidates = _index.Retrieve(intent);

        Assert.All(candidates, c => Assert.True(c.Plan.Features.RequiresCrossReference));
    }

    [Fact]
    public void Retrieve_MissingRequiredEntity_FiltersPlanOut()
    {
        // Plan requires "customer" entity but query doesn't provide one
        var intent = new QueryIntent
        {
            PrimaryAction = DomainAction.List,
            Domain = "sales",
            SubDomain = "orders",
            ExtractedSlots = [], // No customer slot
            // ... other fields
        };

        var candidates = _index.Retrieve(intent);

        // Plans requiring "customer" should be filtered out
        Assert.All(candidates, c =>
            Assert.DoesNotContain("customer", c.Plan.Features.RequiredEntityTypes));
    }
}
```

### Validation Tests

```csharp
namespace CffPlanRouter.Validation.Tests;

public class SchemaCompatibilityValidatorTests
{
    [Fact]
    public void Validate_BrokenToolChain_ReturnsFail()
    {
        // Step 2 needs "orderId" but Step 1 doesn't produce it
        var plan = new PlanDefinition
        {
            Steps =
            [
                new PlanStep { StepId = 1, ToolName = "list_customers", OutputFields = ["customerId", "name"], DependsOn = [] },
                new PlanStep { StepId = 2, ToolName = "get_order_details", InputSchema = new Dictionary<string, string> { ["orderId"] = "string" }, DependsOn = [1] }
            ],
            // ... other fields
        };

        var bindings = new List<SlotBinding>(); // No orderId provided by query either
        var validator = new SchemaCompatibilityValidator();

        var result = validator.Validate(plan, TestIntent.Default, bindings);

        Assert.Equal(ValidationStatus.Fail, result.Status);
        Assert.Contains(result.Errors, e => e.Contains("orderId"));
    }

    [Fact]
    public void Validate_CompleteChain_ReturnsPass()
    {
        var plan = new PlanDefinition
        {
            Steps =
            [
                new PlanStep { StepId = 1, ToolName = "list_orders", InputSchema = new Dictionary<string, string> { ["startDate"] = "date" }, OutputFields = ["orderId", "amount"], DependsOn = [] },
                new PlanStep { StepId = 2, ToolName = "compute_total", InputSchema = new Dictionary<string, string> { ["amount"] = "decimal" }, OutputFields = ["total"], DependsOn = [1] }
            ],
            // ... other fields
        };

        var bindings = new List<SlotBinding>
        {
            new() { SlotName = "startDate", Value = "2024-01-01", Source = BindingSource.TemporalResolution, Confidence = 0.9f }
        };

        var validator = new SchemaCompatibilityValidator();
        var result = validator.Validate(plan, TestIntent.Default, bindings);

        Assert.Equal(ValidationStatus.Pass, result.Status);
    }
}
```

### Integration Test with Trace Inspection

```csharp
namespace CffPlanRouter.Integration.Tests;

public class FullPipelineTests
{
    [Fact]
    public async Task Route_AmbiguousQuery_ProducesExplainableTrace()
    {
        var pipeline = BuildPipeline(useLlm: false); // Deterministic for testing

        var result = await pipeline.RouteAsync("show me the numbers for last quarter");

        // Even if routing fails, the trace tells us exactly why
        Assert.NotNull(result.Trace);

        var understandingLayer = result.Trace.Layers.First(l => l.LayerName == "Understanding");
        Assert.NotNull(understandingLayer.Result);

        // We can inspect what the parser extracted
        var parsedIntent = (dynamic)understandingLayer.Result;
        Assert.True(parsedIntent.DomainConfidence < 0.5f); // "numbers" is ambiguous

        // The trace explains the failure path
        if (result.Status == RoutingStatus.LowConfidence)
        {
            Assert.NotEmpty(result.ConsideredPlanIds!);
            // We know which plans were considered and why they scored low
        }
    }
}
```

---

## Key Design Decisions Explained

| Decision | Rationale |
|----------|-----------|
| **No embeddings in the retrieval path** | Embeddings encode semantic meaning of text, not structural compatibility of execution plans. A bag-of-features index with discrete dimensions is deterministic, debuggable, and doesn't require threshold tuning. |
| **LLM only in Layer 1 (understanding) and optionally Layer 3 (ranking)** | The LLM's strength is natural language interpretation and reasoning about plan suitability. It's wasted on similarity search. Use it where it adds unique value. |
| **Progressive filtering (eliminate, don't search)** | Each filter removes plans that are structurally incompatible. The correct plan is whatever survives. This is O(n) with early termination, not O(n²) pairwise comparison. |
| **Validation as a separate layer** | The original design had no way to detect "we picked the wrong plan" before executing it. Validation catches structural incompatibilities before any tool is invoked. |
| **Full diagnostic trace on every decision** | The original design's 7-stage pipeline was opaque — you couldn't tell which stage caused a misroute. Every decision here is recorded with inputs, outputs, and timing. |
| **Offline index building** | Feature extraction and clustering happen once at ingestion time, not at query time. The runtime loads a pre-built index. Adding a new plan means re-running the indexer, not redeploying the application. |
| **Multi-intent handled by recursion, not special-casing** | Each sub-intent goes through the same 4-layer pipeline independently. No "multi-intent mode" with different logic — just the same pipeline called N times. |
| **Explicit failure modes** | Five distinct `RoutingStatus` values instead of a binary success/fail. The caller knows whether the query wasn't understood, no plan exists, confidence was low, or validation failed — and can respond appropriately to each. |
| **Scoring weights as the only tuning knob** | Instead of 5 boolean feature flags creating 32 configurations, there's one `ScoringWeights` object with 7 floats that sum to 1.0. You can A/B test weight configurations against a labelled evaluation set. |
| **Plans indexed by what they NEED and PRODUCE, not what they DESCRIBE** | `RequiredInputSlots` and `ProducedOutputFields` are the ground truth of what a plan does. Natural language descriptions are for humans, not for matching. |

---

## Migration Path from Current Design

If you want to evolve the existing codebase rather than rewrite:

| Step | Change | Risk |
|------|--------|------|
| 1 | Add `PlanFeatureVector` to each plan YAML and compute it in `PlanLoader` | None — additive |
| 2 | Implement `PlanIndex` alongside existing `AgentRegistry` | None — new code path |
| 3 | Add `QueryIntent` as the output of Stage 2 (rewriter) instead of `RewrittenIntent` | Low — extends existing model |
| 4 | Route through `PlanIndex.Retrieve()` when cache misses instead of `IIntentClassifier` | Medium — replaces Stage 4 |
| 5 | Add `SchemaCompatibilityValidator` before `PlanExecutor` | None — additive gate |
| 6 | Add `RoutingTrace` to every response | None — additive diagnostics |
| 7 | Remove semantic cache for plan selection (keep it only for identical repeated queries) | Medium — changes Stage 3 semantics |
| 8 | Remove fallback chain (Stages 4b, 4c) — replaced by structured retrieval | High — removes safety net, but the safety net was masking bugs |