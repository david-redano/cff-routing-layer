# CFF Routing Layer — Redesign Proposal

## Problem Statement

The current four-layer pipeline fails to produce correct plan matches because:

1. **Layer 1 (Understanding)** relies on brittle keyword maps that miss synonyms, paraphrases, and implicit intent
2. **Layer 2 (Retrieval)** uses hard filters that either over-prune (wrong domain detected) or under-prune (low confidence → skip filter → too many candidates)
3. **Layer 3 (Ranking)** uses token overlap as a tiebreaker, which is superficial and fails on semantically equivalent but lexically different queries
4. **The pipeline is serial and rigid** — an early mistake cannot be corrected downstream
5. **No learning loop** — the system cannot improve from failures or user corrections

---

## Redesign Overview

```
User query
    │
    ▼
┌─────────────────────────────────────────────────┐
│  Phase 0 — Semantic Understanding (LLM-based)   │
│  Structured intent extraction via Bedrock        │
└─────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────┐
│  Phase 1 — Coarse Retrieval (Hybrid)            │
│  Embedding similarity + metadata filters         │
└─────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────┐
│  Phase 2 — Fine Ranking (LLM-as-Judge)          │
│  Pairwise or pointwise scoring with reasoning   │
└─────────────────────────────────────────────────┘
    │
    ▼
┌─────────────────────────────────────────────────┐
│  Phase 3 — Validation & Slot Filling            │
│  Schema check + entity resolution               │
└─────────────────────────────────────────────────┘
    │
    ▼
PlanExecutor → Planner/Execution/Solver phases
```

---

## Phase 0 — Semantic Understanding (Replace Rule-Based Parser)

### What changes

Remove `RuleBasedQueryParser`, `DomainDictionary`, `ActionClassifier` as the primary path. Keep them only as a **zero-latency fallback** when LLM is unavailable.

### New approach: LLM-based structured extraction

Use Bedrock Claude with a constrained JSON output schema:

```
System prompt:
You are a query classifier for a financial accounting copilot.
Given a user query, extract the following structured intent.
Respond ONLY with valid JSON matching this schema.

{
  "domain": "finance | sales | operations | hr | unknown",
  "subdomain": "invoicing | tax | reporting | cashflow | reconciliation | forecasting | expenses | payroll | unknown",
  "action": "list | create | compute | compare | forecast | audit | explain | unknown",
  "entities": {
    "companyId": "string | null",
    "customerId": "string | null",
    "amount": "number | null",
    "currency": "string | null",
    "documentType": "string | null"
  },
  "temporalScope": {
    "type": "absolute | relative | none",
    "value": "string | null",
    "resolvedStart": "ISO date | null",
    "resolvedEnd": "ISO date | null"
  },
  "confidence": 0.0-1.0,
  "reasoning": "one sentence explaining classification"
}
```

### Why this fixes the problem

- Handles paraphrases: "Are we bleeding cash?" → `{ domain: "finance", subdomain: "cashflow", action: "compute" }`
- Handles implicit intent: "Pull up everything from last quarter" → `{ action: "list", temporalScope: { type: "relative", value: "last quarter" } }`
- Handles jargon and typos through LLM's language understanding
- The `reasoning` field enables debugging and audit trails

### Latency mitigation

| Strategy | Implementation |
|---|---|
| Use Haiku (fastest Claude model) | Already configured |
| Cache identical/near-identical queries | In-memory LRU cache with normalized query keys |
| Parallel execution | Fire LLM call AND rule-based parser simultaneously; use rule-based if LLM exceeds 500ms |
| Batch classification | If multiple queries queued, batch into single prompt |

### Implementation

```csharp
public class LlmIntentExtractor
{
    private readonly BedrockLlmHelper _llm;
    private readonly IMemoryCache _cache;
    private readonly RuleBasedQueryParser _fallback;

    public async Task<QueryIntent> ExtractIntent(string query, CancellationToken ct)
    {
        var cacheKey = NormalizeForCache(query);
        if (_cache.TryGetValue(cacheKey, out QueryIntent cached))
            return cached;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(800));

        try
        {
            var json = await _llm.ClassifyQuery(query, cts.Token);
            var intent = ParseStructuredResponse(json);
            _cache.Set(cacheKey, intent, TimeSpan.FromMinutes(10));
            return intent;
        }
        catch (OperationCanceledException)
        {
            // Fallback to rule-based parser
            return _fallback.Parse(query);
        }
    }
}
```

---

## Phase 1 — Coarse Retrieval (Replace Hard Filters with Hybrid Search)

### What changes

Remove the serial `DomainFilter → ActionFilter → TemporalFilter → EntityFilter` chain. Replace with a **hybrid retrieval** strategy that combines:

1. **Semantic similarity** (embedding-based)
2. **Structured metadata match** (from Phase 0 output)

### New approach: Dual-signal retrieval

#### Signal A — Embedding similarity

Pre-compute embeddings for each plan using a concatenation of its key fields:

```
Plan embedding input = "{understanding}. {sampleQueries joined}. Domain: {domain}. Action: {capabilities joined}."
```

At query time, embed the user query and retrieve top-K by cosine similarity.

**Embedding model options:**

| Option | Pros | Cons |
|---|---|---|
| Bedrock Titan Embeddings v2 | Native AWS, no infra | API latency (~100ms) |
| Local ONNX model (e5-small) | Zero latency, no network | Must bundle model, less accurate |
| Pre-computed + approximate NN | Fast at scale | Overkill for 114 plans |

**For 114 plans:** Use brute-force cosine similarity against pre-computed vectors. No need for ANN indices at this scale.

#### Signal B — Metadata match score

Score each plan against the structured intent from Phase 0:

```csharp
public float MetadataScore(QueryIntent intent, PlanFeatureVector plan)
{
    float score = 0f;

    // Domain match (hard signal)
    if (intent.Domain == plan.Domain) score += 0.35f;
    else if (intent.Domain == "unknown") score += 0.15f; // neutral

    // Subdomain match
    if (intent.Subdomain == plan.Subdomain) score += 0.25f;
    else if (intent.Subdomain == "unknown") score += 0.10f;

    // Action match
    if (intent.Action == plan.PrimaryAction) score += 0.25f;
    else if (ActionIsCompatible(intent.Action, plan.PrimaryAction)) score += 0.12f;
    else if (intent.Action == "unknown") score += 0.10f;

    // Temporal compatibility
    if (TemporalIsCompatible(intent.TemporalScope, plan.TemporalCapability)) score += 0.15f;

    return score; // max 1.0
}
```

#### Combined retrieval score

```
retrievalScore = (embeddingSimilarity × 0.55) + (metadataScore × 0.45)
```

Return **top-10** candidates (not top-5 — give the ranker more to work with).

### Why this fixes the problem

- Embedding similarity catches semantic matches that keywords miss ("bleeding cash" ↔ "cash flow analysis")
- Metadata match provides hard structural constraints that embeddings alone might miss (a tax plan shouldn't match a payroll query even if embeddings are close)
- No hard filters means no premature pruning — everything gets a score

### Implementation

```csharp
public class HybridPlanRetriever
{
    private readonly IEmbeddingService _embedder;
    private readonly float[][] _planEmbeddings; // pre-computed at startup
    private readonly PlanFeatureVector[] _planFeatures;

    public async Task<List<CandidateResult>> Retrieve(
        string query, QueryIntent intent, int topK = 10)
    {
        // Signal A: semantic similarity
        var queryEmbedding = await _embedder.Embed(query);
        var similarities = _planEmbeddings
            .Select((emb, i) => (Index: i, Score: CosineSimilarity(queryEmbedding, emb)))
            .ToArray();

        // Signal B: metadata match
        var metadataScores = _planFeatures
            .Select((feat, i) => (Index: i, Score: MetadataScore(intent, feat)))
            .ToArray();

        // Combine
        var combined = similarities
            .Zip(metadataScores, (a, b) => new CandidateResult
            {
                PlanIndex = a.Index,
                EmbeddingScore = a.Score,
                MetadataScore = b.Score,
                CombinedScore = (a.Score * 0.55f) + (b.Score * 0.45f)
            })
            .OrderByDescending(c => c.CombinedScore)
            .Take(topK)
            .ToList();

        return combined;
    }
}
```

---

## Phase 2 — Fine Ranking (Replace Token Overlap with LLM-as-Judge)

### What changes

Remove `FeatureAlignmentRanker` with its token-overlap tiebreaker. Replace with an **LLM-based pointwise scorer** that evaluates each candidate's fit.

### New approach: LLM pointwise scoring

For the top-10 candidates from Phase 1, ask the LLM to score each one:

```
System prompt:
You are evaluating whether a pre-saved execution plan matches a user's intent.
Score the match from 0.0 to 1.0 and explain your reasoning in one sentence.

User message:
## User Query
"{query}"

## Extracted Intent
{intent as JSON}

## Candidate Plan
Plan ID: {planId}
Description: {understanding}
Domain: {domain}
Capabilities: {capabilities}
Sample queries this plan handles:
- {sampleQuery1}
- {sampleQuery2}
Expected output fields: {expectedData.summary}

## Scoring criteria
- 1.0 = This plan is exactly what the user is asking for
- 0.7-0.9 = This plan addresses the core need but may need parameter adjustment
- 0.4-0.6 = This plan is related but not quite right
- 0.1-0.3 = This plan is in the wrong domain or action type
- 0.0 = Completely irrelevant

Respond with JSON: {"score": float, "reasoning": "string"}
```

### Batch optimization

Don't make 10 separate LLM calls. Batch all candidates into a single prompt:

```
Score each of the following 10 candidate plans against the user query.
Respond with a JSON array of {planId, score, reasoning}.

User Query: "..."
Extracted Intent: {...}

Plan 1: ...
Plan 2: ...
...
Plan 10: ...
```

This reduces to **1 LLM call** for the entire ranking phase.

### Fallback: Deterministic ranker (improved)

When LLM is unavailable or for latency-critical paths, use an improved deterministic ranker:

```csharp
public class ImprovedDeterministicRanker
{
    public float Score(QueryIntent intent, PlanDefinition plan, string originalQuery)
    {
        float score = 0f;

        // 1. Sample query similarity (most discriminative signal)
        float bestSampleMatch = plan.SampleQueries
            .Max(sq => NormalizedEditSimilarity(originalQuery, sq));
        score += bestSampleMatch * 0.40f;

        // 2. Metadata alignment (from Phase 1, already computed)
        score += metadataScore * 0.35f;

        // 3. Entity/slot coverage
        float slotCoverage = ComputeSlotCoverage(intent.Entities, plan.DefaultEntities);
        score += slotCoverage * 0.15f;

        // 4. Output relevance (do the plan's outputs answer the question?)
        float outputRelevance = ComputeOutputRelevance(intent, plan.ExpectedData);
        score += outputRelevance * 0.10f;

        return score;
    }

    private float NormalizedEditSimilarity(string a, string b)
    {
        // Jaro-Winkler or normalized Levenshtein
        // Much better than token overlap for catching paraphrases
        int distance = LevenshteinDistance(Normalize(a), Normalize(b));
        int maxLen = Math.Max(a.Length, b.Length);
        return 1f - ((float)distance / maxLen);
    }
}
```

### Why this fixes the problem

- LLM understands semantic equivalence: "percentage of invoices paid late" ↔ "Calculate what percentage of total sales invoices were paid after their due date"
- Token overlap fails here because the words are different but meaning is identical
- Batch scoring keeps latency to a single LLM round-trip
- The deterministic fallback uses edit distance on sample queries (much better than token overlap)

---

## Phase 3 — Validation & Slot Filling (Mostly Unchanged)

### What stays

- `SlotCoverageValidator` — verify required slots are present or have defaults
- `SchemaCompatibilityValidator` — verify plan outputs match what user expects
- Fallback to next-best candidate on validation failure

### What changes

#### Add: Entity resolution with LLM assistance

When slots are missing but inferable from context:

```csharp
public class SmartSlotFiller
{
    public async Task<Dictionary<string, string>> FillSlots(
        QueryIntent intent, PlanDefinition plan, CompanyDataStore data)
    {
        var slots = new Dictionary<string, string>();

        // 1. Direct extraction from intent
        foreach (var (key, value) in intent.Entities)
        {
            if (value != null) slots[key] = value;
        }

        // 2. Default values from plan
        foreach (var (key, value) in plan.DefaultEntities)
        {
            if (!slots.ContainsKey(key)) slots[key] = value;
        }

        // 3. Contextual inference (e.g., "this month" → resolve dates)
        if (intent.TemporalScope.Type != "none" && !slots.ContainsKey("startDate"))
        {
            var (start, end) = ResolveTemporalScope(intent.TemporalScope);
            slots["startDate"] = start.ToString("yyyy-MM-dd");
            slots["endDate"] = end.ToString("yyyy-MM-dd");
        }

        // 4. Company context (single-tenant assumption)
        if (!slots.ContainsKey("companyId"))
        {
            slots["companyId"] = data.GetDefaultCompanyId();
        }

        return slots;
    }
}
```

#### Add: Confidence gating with user confirmation

```csharp
public enum RoutingDecision
{
    Execute,          // score > 0.80, clear winner
    ConfirmAndExecute, // score 0.60-0.80, ask user "Did you mean X?"
    Clarify,          // score 0.40-0.60, ask user to rephrase
    Reject,           // score < 0.40, cannot match
    Ambiguous         // top-2 gap < 0.05, ask user to choose
}
```

---

## New Component: Plan Embedding Index

### Startup indexing

```csharp
public class PlanEmbeddingIndex
{
    private readonly Dictionary<string, float[]> _embeddings = new();
    private readonly IEmbeddingService _embedder;

    public async Task BuildIndex(IEnumerable<PlanDefinition> plans)
    {
        foreach (var plan in plans)
        {
            string text = BuildEmbeddingText(plan);
            float[] embedding = await _embedder.Embed(text);
            _embeddings[plan.PlanId] = embedding;
        }
    }

    private string BuildEmbeddingText(PlanDefinition plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine(plan.Understanding);
        foreach (var sq in plan.SampleQueries)
            sb.AppendLine(sq);
        sb.AppendLine($"Domain: {plan.Domain}");
        sb.AppendLine($"Action: {plan.Intent}");
        sb.AppendLine($"Outputs: {string.Join(", ", plan.ExpectedData.Summary)}");
        return sb.ToString();
    }

    public List<(string PlanId, float Score)> Search(float[] queryEmbedding, int topK)
    {
        return _embeddings
            .Select(kvp => (kvp.Key, CosineSimilarity(queryEmbedding, kvp.Value)))
            .OrderByDescending(x => x.Item2)
            .Take(topK)
            .ToList();
    }
}
```

### Embedding service options

```csharp
public interface IEmbeddingService
{
    Task<float[]> Embed(string text);
}

// Option 1: Bedrock Titan
public class BedrockEmbeddingService : IEmbeddingService
{
    // Uses amazon.titan-embed-text-v2:0
    // Dimension: 1024
    // Latency: ~80-120ms per call
}

// Option 2: Local ONNX (zero-latency for query-time)
public class LocalEmbeddingService : IEmbeddingService
{
    // Uses ONNX runtime with e5-small-v2 or similar
    // Dimension: 384
    // Latency: ~5-10ms per call
    // Trade-off: slightly less accurate, but no network dependency
}
```

---

## Handling the 114-Plan Scale

With only 114 plans, several simplifications are possible:

| Concern | Decision |
|---|---|
| Vector index type | Brute-force cosine (no HNSW/IVF needed) |
| Embedding storage | In-memory dictionary (< 500KB for 114 × 1024-dim vectors) |
| LLM batch scoring | All 10 candidates fit in a single prompt (~2K tokens) |
| Plan loading | All plans in memory at startup (already doing this) |
| Cache size | Unbounded LRU is fine (queries won't exhaust memory) |

---

## Latency Budget

| Phase | Target | Strategy |
|---|---|---|
| Phase 0 (Understanding) | < 300ms | Haiku + cache + rule-based race |
| Phase 1 (Retrieval) | < 150ms | Pre-computed embeddings + brute-force search |
| Phase 2 (Ranking) | < 500ms | Single batched LLM call |
| Phase 3 (Validation) | < 10ms | Pure computation |
| **Total** | **< 1000ms** | Acceptable for interactive copilot |

### Fast path (cache hit)

```
Phase 0: cache hit → 0ms
Phase 1: embedding search → 50ms
Phase 2: deterministic ranker (skip LLM if Phase 1 top score > 0.90) → 5ms
Phase 3: validation → 5ms
Total: ~60ms
```

### Optimization: Skip Phase 2 LLM when unnecessary

```csharp
if (retrievalResults[0].CombinedScore > 0.90 &&
    retrievalResults[0].CombinedScore - retrievalResults[1].CombinedScore > 0.20)
{
    // Clear winner from retrieval alone — skip expensive LLM ranking
    return retrievalResults[0];
}
```

---

## Confidence Calibration (Replacing Arbitrary Thresholds)

### Current problem

Your thresholds (0.25, 0.35, 0.65) are arbitrary and not calibrated to actual score distributions.

### Fix: Empirical calibration

1. Create a **test set** of 200+ query → expected plan mappings
2. Run all queries through the pipeline
3. Plot score distributions for correct vs. incorrect matches
4. Set thresholds at the crossover points

```csharp
public class CalibratedThresholds
{
    // Set empirically from test set analysis
    public float ExecuteThreshold { get; set; } = 0.80f;   // P(correct) > 95%
    public float ConfirmThreshold { get; set; } = 0.60f;   // P(correct) > 75%
    public float RejectThreshold { get; set; } = 0.35f;    // P(correct) < 30%
    public float AmbiguityGap { get; set; } = 0.08f;       // top1 - top2

    public RoutingDecision Decide(float topScore, float secondScore)
    {
        float gap = topScore - secondScore;

        if (topScore >= ExecuteThreshold && gap >= AmbiguityGap)
            return RoutingDecision.Execute;
        if (topScore >= ConfirmThreshold && gap >= AmbiguityGap)
            return RoutingDecision.ConfirmAndExecute;
        if (gap < AmbiguityGap && topScore >= ConfirmThreshold)
            return RoutingDecision.Ambiguous;
        if (topScore >= RejectThreshold)
            return RoutingDecision.Clarify;
        return RoutingDecision.Reject;
    }
}
```

---

## Test Set & Evaluation Framework

### Required: A ground-truth evaluation set

This is the **single most important missing piece** in your current system.

```yaml
# test_cases.yaml
- query: "How much tax do we owe this year?"
  expectedPlanId: tax-001
  requiredSlots:
    year: "2024"

- query: "Are we bleeding cash?"
  expectedPlanId: cashflow-001

- query: "Show me all unpaid invoices from last month"
  expectedPlanId: listinvoices-001
  requiredSlots:
    status: unpaid
    startDate: "2024-11-01"
    endDate: "2024-11-30"

- query: "What percentage of invoices were paid late?"
  expectedPlanId: real-059

- query: "Invoice Acme Corp for $5,000"
  expectedPlanId: invoice-001
  requiredSlots:
    customerId: "acme-corp"
    amount: "5000"

- query: "What's the weather like?"
  expectedPlanId: null  # Should reject
  expectedDecision: Reject
```

### Evaluation metrics

```csharp
public class PipelineEvaluator
{
    public EvaluationReport Evaluate(List<TestCase> testCases)
    {
        int correct = 0, incorrect = 0, rejected = 0, falseReject = 0;

        foreach (var tc in testCases)
        {
            var result = _pipeline.Route(tc.Query);

            if (tc.ExpectedPlanId == null)
            {
                if (result.Decision == RoutingDecision.Reject) correct++;
                else incorrect++;
            }
            else
            {
                if (result.SelectedPlanId == tc.ExpectedPlanId) correct++;
                else if (result.Decision == RoutingDecision.Reject) falseReject++;
                else incorrect++;
            }
        }

        return new EvaluationReport
        {
            Accuracy = (float)correct / testCases.Count,
            FalseRejectRate = (float)falseReject / testCases.Count,
            MismatchRate = (float)incorrect / testCases.Count
        };
    }
}
```

### Target metrics

| Metric | Target | Acceptable |
|---|---|---|
| Accuracy (correct plan selected) | > 90% | > 85% |
| False reject rate | < 5% | < 10% |
| Mismatch rate (wrong plan executed) | < 3% | < 5% |
| Ambiguous (asks user to choose) | < 10% | < 15% |

---

## Migration Path (Incremental)

You don't need to rewrite everything at once. Here's the order of impact:

### Week 1: Add evaluation framework + test set

- Create 100+ test cases covering all 114 plans
- Measure current accuracy (likely 40-60%)
- This gives you a baseline to measure improvements against

### Week 2: Replace Layer 1 with LLM intent extraction

- Implement `LlmIntentExtractor` with Haiku
- Keep rule-based as fallback
- Re-run evaluation → expect 10-15% accuracy improvement

### Week 3: Add embedding-based retrieval

- Pre-compute plan embeddings (Bedrock Titan or local ONNX)
- Implement `HybridPlanRetriever` combining embeddings + metadata
- Replace hard filters with soft scoring
- Re-run evaluation → expect 15-20% accuracy improvement

### Week 4: Add LLM-as-Judge ranking

- Implement batched LLM scoring for top-10 candidates
- Add confidence calibration based on test set results
- Re-run evaluation → expect 5-10% accuracy improvement

### Week 5: Polish

- Add user confirmation flow for ambiguous cases
- Tune weights based on error analysis
- Add caching layer
- Expand test set to 200+ cases

---

## Architecture Diagram (Final State)

```
┌──────────────────────────────────────────────────────────────────┐
│                        User Query                                 │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│  PHASE 0: Intent Extraction                                       │
│                                                                    │
│  ┌─────────────────┐    ┌──────────────────────┐                 │
│  │ LLM Classifier  │◄──►│ Query Cache (LRU)    │                 │
│  │ (Bedrock Haiku) │    └──────────────────────┘                 │
│  └────────┬────────┘                                              │
│           │ timeout?                                               │
│           ▼                                                        │
│  ┌─────────────────┐                                              │
│  │ Rule-Based      │  (fallback only)                             │
│  │ Parser          │                                              │
│  └─────────────────┘                                              │
│                                                                    │
│  Output: QueryIntent { domain, subdomain, action, entities,       │
│           temporalScope, confidence, reasoning }                   │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│  PHASE 1: Hybrid Retrieval                                        │
│                                                                    │
│  ┌─────────────────────────┐  ┌─────────────────────────┐       │
│  │ Signal A: Embeddings    │  │ Signal B: Metadata      │       │
│  │                         │  │                         │       │
│  │ query → embed → cosine  │  │ intent vs plan features │       │
│  │ against 114 plan vectors│  │ (domain, action, etc.)  │       │
│  └────────────┬────────────┘  └────────────┬────────────┘       │
│               │                             │                     │
│               └──────────┬──────────────────┘                     │
│                          ▼                                         │
│              combined = 0.55×A + 0.45×B                           │
│              return top-10                                         │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│  PHASE 2: Fine Ranking                                            │
│                                                                    │
│  ┌─────────────────────────────────────────────┐                 │
│  │ IF top1.score > 0.90 AND gap > 0.20:       │                 │
│  │   → Skip LLM, use retrieval winner         │                 │
│  │ ELSE:                                       │                 │
│  │   → Batch LLM scoring (single call)        │                 │
│  │   → Score each candidate 0.0-1.0           │                 │
│  │   → Re-rank by LLM score                   │                 │
│  └─────────────────────────────────────────────┘                 │
│                                                                    │
│  Fallback: Improved deterministic ranker                          │
│  (edit distance on sample queries, not token overlap)             │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│  PHASE 3: Validation & Decision                                   │
│                                                                    │
│  ┌───────────────┐  ┌───────────────┐  ┌───────────────────┐    │
│  │ Slot Coverage │  │ Schema Compat │  │ Confidence Gate   │    │
│  │ Validator     │  │ Validator     │  │                   │    │
│  └───────────────┘  └───────────────┘  │ > 0.80 → Execute │    │
│                                         │ > 0.60 → Confirm │    │
│                                         │ > 0.35 → Clarify │    │
│                                         │ < 0.35 → Reject  │    │
│                                         └───────────────────┘    │
└──────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌──────────────────────────────────────────────────────────────────┐
│  Plan Executor → Planner / Execution / Solver                     │
└──────────────────────────────────────────────────────────────────┘
```

---

## Key Design Principles

1. **Soft scoring everywhere, hard filters nowhere** — Every plan gets a score; nothing is prematurely eliminated
2. **LLM for understanding, deterministic for speed** — Use LLM where language understanding matters; use math where structure matters
3. **Graceful degradation** — Every LLM call has a deterministic fallback
4. **Measure before tuning** — Build the test set first; tune weights empirically
5. **Explain decisions** — Every phase produces reasoning that can be logged and debugged
6. **Cache aggressively** — Same query should never hit LLM twice

---

## Cost Estimate (Bedrock)

| Phase | Calls/query | Input tokens | Output tokens | Cost/query |
|---|---|---|---|---|
| Phase 0 (Intent) | 1 (cache miss) | ~200 | ~150 | ~$0.00005 |
| Phase 1 (Embedding) | 1 | ~50 | — | ~$0.00001 |
| Phase 2 (Ranking) | 1 (batched) | ~3000 | ~500 | ~$0.0001 |
| **Total** | **3** | **~3250** | **~650** | **~$0.00016** |

At 10,000 queries/day: **~$1.60/day** or **~$48/month**

With caching (assuming 40% cache hit rate): **~$29/month**

---

## Summary of Changes

| Current | Proposed | Why |
|---|---|---|
| Rule-based keyword parser | LLM structured extraction | Handles paraphrases, jargon, implicit intent |
| Serial hard filters | Hybrid embedding + metadata soft scoring | No premature pruning |
| Token overlap tiebreaker | LLM-as-Judge batch scoring | Semantic understanding of plan fit |
| Arbitrary thresholds | Empirically calibrated from test set | Data-driven confidence decisions |
| No evaluation framework | 200+ test cases with automated metrics | Can't improve what you can't measure |
| Single "execute or reject" | Execute / Confirm / Clarify / Reject | Graceful handling of uncertainty |
