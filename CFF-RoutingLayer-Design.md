# CFF Routing Layer Demo — Design Document
> Inspired by Intuit's GenOS routing architecture  
> Target: Local .NET 8 Console Application

---

## 1. How Intuit Handles User Requests — Routing Layer & Intent Caching

### 1.1 Overview

Intuit built **GenOS** (Generative AI Operating System), the platform that powers **Intuit Assist** across QuickBooks, TurboTax, Credit Karma, and Mailchimp. At its core sits a **natural-language routing layer** that acts as the brain between the user and dozens of specialized AI agents.

Rather than sending every user message directly to a large LLM at full cost and latency, Intuit inserts a lightweight, multi-stage pipeline in front:

```
User Request
    │
    ▼
┌─────────────────────────────────────────────┐
│              ROUTING LAYER                  │
│                                             │
│  1. Guardrails & Safety Check               │
│  2. Intent Rewriter  ← NEW                 │
│     (PII → vars, normalise canonical form)  │
│  3. Semantic Intent Cache (lookup)          │
│  4. Intent Classifier (LLM or fine-tuned)  │
│  5. Agent Registry Lookup                   │
│  6. Execution Plan Generation               │
│  7. Agent Dispatch                          │
└─────────────────────────────────────────────┘
    │
    ▼
Specialized Agent (Tax / Bookkeeping / Payroll / etc.)
    │
    ▼
Response → Cache Store → User
```

---

### 1.2 Stage-by-Stage Breakdown

#### Stage 1 — Guardrails & Safety Check
Every incoming request is first validated:
- Is it within the product's domain? (financial, accounting, tax)
- Does it pass responsible-AI content filters?
- Is the user authenticated and authorized for the requested operation?

Out-of-scope or unsafe requests are rejected immediately, before any LLM call is made.

#### Stage 2 — Intent Rewriter (Normalisation + PII Extraction)

Before hitting the semantic cache, the raw user message is rewritten into a **canonical, PII-free form**. This stage does three things in one pass:

| What | Example |
|---|---|
| Extract named entities | `"Acme Corp"` → `entities["customer"] = "Acme Corp"` |
| Replace PII with typed placeholders | `"create invoice for Acme Corp"` → `"create invoice for ${customer}"` |
| Normalise surface variation | `"pnl"`, `"P&L"`, `"profit & loss"` all → `"profit and loss"` |

**Why this matters for the cache**: without rewriting, `"create invoice for Acme Corp"` and `"create invoice for John Smith"` produce different embeddings and both miss the cache. After rewriting they both become `"create invoice for ${customer}"` — identical cache key, guaranteed hit on the second call.

**PII never stored**: the cache, logs, and the embedding model all see the placeholder form. The real values live only in `RewrittenIntent.ExtractedEntities`, which is passed directly to the execution plan.

#### Stage 3 — Semantic Intent Cache (Key mechanism)
This is the most cost-saving innovation. Before classifying intent with an LLM:

1. The user's message is converted to a **vector embedding** using a lightweight embedding model.
2. The embedding is compared (cosine similarity) against a **cache of previously seen intents** stored in a vector store (e.g., Redis with vector search, Pinecone, or Azure AI Search).
3. If similarity score ≥ threshold (e.g., 0.92), it is a **cache hit**:
   - The cached `IntentResult` (intent label + resolved agent + prior execution plan skeleton) is returned immediately.
   - The LLM classification call is skipped entirely.
4. If similarity < threshold, it is a **cache miss** → proceed to Stage 3.

> **Why this matters**: Financial products see highly repetitive question patterns. "What's my cash flow?" and "Show me my current cash flow" are semantically identical. Caching eliminates redundant LLM calls for these.

#### Stage 3 — Intent Classifier
On a cache miss, a fast, fine-tuned (or prompted) LLM classifies the request into a structured `IntentResult`:

```json
{
  "intent": "GenerateCashFlowReport",
  "confidence": 0.97,
  "entities": {
    "period": "last_30_days",
    "format": "summary"
  },
  "agent": "BookkeepingAgent",
  "requires_confirmation": false
}
```

Intuit uses smaller, task-specific models for this classification step (faster, cheaper than GPT-4 class models) and falls back to larger models only for low-confidence results.

#### Stage 4 — Agent Registry Lookup
The classified intent is matched against an **Agent Registry** — a manifest of all available agents, their capabilities, required inputs, and output contracts:

| Intent | Agent | Required Context |
|---|---|---|
| `GenerateCashFlowReport` | `BookkeepingAgent` | date range, company ID |
| `CategorizeTransaction` | `TransactionAgent` | transaction ID |
| `EstimateTaxLiability` | `TaxAgent` | fiscal year, entity type |
| `CreateInvoice` | `InvoiceAgent` | customer, line items |
| `ReconcileAccount` | `ReconciliationAgent` | account ID, statement |

#### Stage 5 — Execution Plan Generation
Instead of letting each agent improvise, the router generates a deterministic **Execution Plan** — an ordered list of steps with inputs and expected outputs:

```json
{
  "planId": "plan-20240529-001",
  "intent": "GenerateCashFlowReport",
  "steps": [
    { "stepId": 1, "action": "FetchTransactions", "input": { "period": "last_30_days" }, "dependsOn": [] },
    { "stepId": 2, "action": "CategorizeTransactions", "input": { "transactions": "$step1.output" }, "dependsOn": [1] },
    { "stepId": 3, "action": "AggregateByCategory", "input": { "categorized": "$step2.output" }, "dependsOn": [2] },
    { "stepId": 4, "action": "RenderReport", "input": { "aggregated": "$step3.output", "format": "summary" }, "dependsOn": [3] }
  ]
}
```

This plan is executed by the agent step-by-step. If any step fails, only that step needs to be retried — not the whole request. The plan is also cached alongside the intent so future identical requests can re-use it.

#### Stage 6 — Agent Dispatch & Response
The agent executes its plan steps, collects results, and returns a structured response. The router then:
- Stores the `(embedding → IntentResult + ExecutionPlan + Response)` tuple in the semantic cache.
- Formats the response for the UI.
- Streams back to the user.

---

### 1.3 Intent Caching — Deeper Dive

```
New Request Embedding
        │
        ▼
  Vector Store Query  ──── hit (similarity ≥ 0.92) ────►  Return cached result
        │
     miss
        │
        ▼
  LLM Classification
        │
        ▼
  Execute Agent Plan
        │
        ▼
  Store in Cache  ──► (embedding, intent, plan, response_template)
```

**Cache eviction strategy:**
- TTL-based: Intent results expire after a configurable window (e.g., 24h for reports, 1h for real-time data).
- Invalidation on data change: If underlying data changes (new transactions, reconciliation), related intents are invalidated.
- Similarity threshold tuning: Threshold is tunable per intent category — stricter for high-stakes operations (tax filing), looser for read-only queries.

---

## 2. CFF Demo — Implemented Architecture (.NET 9 Console App)

### 2.1 Goals
- Demonstrate the Intuit GenOS routing concept locally with a real LLM (Amazon Bedrock).
- Implement hybrid semantic + metadata retrieval over a library of 114+ execution plans.
- Route free-text user queries to the best-matching YAML plan in ≤ 2 s end-to-end.
- Show deterministic execution plans with step-by-step console output.
- Include realistic CFF accounting scenarios sourced from real plan API responses.

### 2.2 Project Structure

```
CffRoutingLayerDemo/
├── CffRoutingLayerDemo.csproj
├── Program.cs                              # Interactive REPL loop
│
├── Core/
│   └── PlanRoutingPipeline.cs              # Orchestrates Phases 0–3
│
├── Understanding/
│   ├── LlmQueryParser.cs                   # Phase 0: Bedrock Claude Haiku → QueryIntent
│   ├── RuleBasedQueryParser.cs             # Phase 0 fallback (deterministic, no LLM)
│   ├── QueryNormalizer.cs                  # Temporal-only token normalization
│   ├── ActionClassifier.cs                 # Verb → DomainAction enum
│   ├── DomainDictionary.cs                 # Keyword → domain/subdomain
│   ├── EntityExtractor.cs                  # Slot extraction
│   └── TemporalResolver.cs                 # Relative date → TemporalScope
│
├── Retrieval/
│   ├── HybridPlanRetriever.cs              # Phase 1: embedding (55%) + metadata (45%)
│   └── PlanEmbeddingIndex.cs              # Pre-built Titan V2 embeddings for all plans
│
├── Index/
│   ├── PlanIndex.cs                        # Structural multi-dimensional index (fallback)
│   ├── PlanFeatureExtractor.cs             # Extracts PlanFeatureVector at load time
│   └── Filters/                            # DomainFilter, ActionFilter, TemporalFilter
│
├── Ranking/
│   ├── LlmPlanJudge.cs                     # Phase 2: Bedrock Claude re-ranker (primary)
│   ├── FeatureAlignmentRanker.cs           # Phase 2: deterministic fallback scorer
│   └── ScoringWeights.cs                   # Tunable weights
│
├── Validation/
│   ├── CompositeValidator.cs               # Runs all validators; up to 3 fallback attempts
│   ├── SlotCoverageValidator.cs            # Required slot keys must be bound or defaulted
│   ├── SchemaCompatibilityValidator.cs     # Output field compatibility check
│   └── SemanticCoherenceValidator.cs       # Warns when plan subject absent from query
│
├── Plans/
│   ├── PlanDefinition.cs                   # YAML plan model (including Description field)
│   ├── PlanLoader.cs                       # Deserialises YAML → PlanDefinition
│   └── PlanExecutor.cs                     # Runs plan steps against CompanyDataStore
│
├── Bedrock/
│   ├── BedrockLlmHelper.cs                 # Shared Converse API wrapper
│   └── BedrockStreamingConversation.cs     # Streaming fallback for Rejected queries
│
├── plans/                                  # 114+ YAML plan files
│   ├── cashflow.yaml   profitloss.yaml  tax.yaml  invoice.yaml  reconcile.yaml  …
│   └── response_001..109.yaml              # CFF real plans (real-001 … real-109)
│
└── CompanyData/
    └── CompanyDataStore.cs                 # In-memory demo financial data (DEMO-001)
```

**Supporting projects:**

```
CffPlanTransformer/          # CLI tool: converts raw CFF API responses → YAML plans
  └── Transformer/PlanTransformer.cs   # Strips Approach → description:; normalizes understanding:

CffRoutingLayerDemo.Tests/
CffRoutingLayerDemo.Benchmarks/

cff-real-plans/              # Raw CFF plan API responses (source for transformer)
cff-real-plans-yaml/         # Intermediate transformer output (pre-normalized)
```

---

### 2.3 Phase 0 — LLM Intent Extraction (LlmQueryParser)

`LlmQueryParser` calls **Bedrock Claude 3 Haiku** with a structured system prompt and produces a `QueryIntent`:

```csharp
public sealed record QueryIntent(
    string Domain,          // e.g. "finance"
    string? SubDomain,
    DomainAction Action,    // List / Compute / Create / etc.
    double ActionConfidence,
    Dictionary<string, SlotValue> Slots,   // extracted entities
    TemporalScope? Temporal,
    string Reasoning,
    List<QueryIntent> SubIntents           // non-empty for compound queries
);
```

Key design decisions in the LLM system prompt:

| Decision | Rationale |
|---|---|
| JSON schema with `"query"` field required | For compound queries the root intent must describe the **first task only** (not the full original query) so each sub-intent gets its own isolated query text for Phase 1 retrieval |
| Slot extraction rules — CRITICAL block | Forbids descriptive phrases as slot values: `"customer"` cannot be `"more balance"`, `"the one with the highest revenue"`, etc. |
| Rich date context block injected every call | Today's date, current/last month/quarter/FY in ISO 8601 so LLM can resolve `"last month"`, `"FY2025"` without ambiguity |
| LRU cache (capacity 500) | Avoids repeat LLM calls for semantically identical queries |
| 800 ms timeout | Falls back to `RuleBasedQueryParser` on timeout or Bedrock error |

#### IsDescriptivePhrase guard (post-parse)

After parsing the LLM JSON response, `ParseIntentElement` applies a deterministic check on every extracted slot value:

```csharp
private static bool IsDescriptivePhrase(string value)
```

If the slot value contains comparative/superlative adjectives (`more`, `most`, `highest`, `lowest`, `largest`, `top`, `bottom`, `greater`, `maximum`, `minimum`, `average`, `latest`, `oldest`, `recent`, `pending`, `unpaid`, `overdue`, …) **or** filter-predicate connectors (`with`, `that`, `having`, `which`, `where`, `who`, `whose`), the slot is silently dropped.

This catches cases where the LLM extracts a filter description as if it were a named entity (e.g., `customer = "more balance"` when the query says "the customer with more balance").

#### Multi-intent splitting

When the LLM detects a compound query (e.g., "show upcoming sales payments and list the customer with the highest balance"), it populates `SubIntents`. The root `query` field is set to the **first task only**; `SubIntents` carries the remaining tasks. `PlanRoutingPipeline` routes each sub-intent independently through Phases 1–3.

---

### 2.4 Phase 1 — Hybrid Retrieval (HybridPlanRetriever + PlanEmbeddingIndex)

At startup, `PlanEmbeddingIndex` calls **Titan Embeddings V2** (`amazon.titan-embed-text-v2:0`) for every plan. The embedding text per plan is:

```
{understanding}
{description}          ← stripped Approach field (computation vocabulary)
{sampleQuery[0]}
{sampleQuery[1]}
…
```

All temporal tokens are normalized symmetrically on both sides of the cosine comparison using `QueryNormalizer`:

| Token type | Example | Normalized form |
|---|---|---|
| Fiscal year | `FY2024` | `${fiscalYear}` |
| Month name | `March` | `${month}` |
| Calendar quarter | `Q2` | `${quarter}` |
| 4-digit year | `2024` | `${year}` |
| ISO / partial date | `2024-01-01` | `${date}` |

**Named entity values (customer names, invoice IDs, etc.) are intentionally NOT normalized** — replacing them with generic placeholders would collapse distinct semantic meaning and degrade cosine similarity.

`HybridPlanRetriever` combines the embedding and metadata scores:

```
Combined = embedding_cosine × 0.55 + metadata × 0.45

Metadata breakdown:
  Domain match      0.35
  SubDomain match   0.25
  Action match      0.25
  Temporal match    0.15
```

Outputs: top-10 `CandidateResult` objects ranked by `AlignmentScore`.

---

### 2.5 Phase 2 — Re-ranking (LlmPlanJudge + FeatureAlignmentRanker)

**Primary path** (when Bedrock is reachable): `LlmPlanJudge` calls Claude Haiku with a pointwise scoring prompt that includes, for each candidate:

```
What it does: {understanding}
How: {description}
Steps: …  Inputs: …  Outputs: …
```

**Fallback** (LLM unavailable or < 2 candidates): `FeatureAlignmentRanker`:

```
score = AlignmentScore (Phase 1)
      + outputFieldOverlap    × 0.08    (camelCase-split token overlap with query words)
      + descriptionRelevance  × 0.20    (Levenshtein tiebreaker with keyword-coherence guard)
      − entityMismatchCount   × 0.15    (penalty per unmet entity)
```

`descriptionRelevance`:
- Best normalized Levenshtein similarity between query and any `sampleQuery`
- Falls back to keyword overlap against `understanding` + `description` when no sample queries
- **Keyword-coherence guard**: result scaled by `0.25 + 0.75 × keywordCoverage`, where `keywordCoverage` = fraction of query content words (len > 4, non-stop-word) found in `understanding + description + sampleQueries`. Prevents plans with coincidental surface similarity from outscoring semantically correct plans.

`IsAmbiguous` = `top1Score − top2Score < AmbiguityGap (0.08)`.

---

### 2.6 Phase 3 — Validation (CompositeValidator)

`CompositeValidator` runs three validators in sequence. If validation fails, the pipeline falls back to the next-best candidate (up to 3 attempts):

#### SlotCoverageValidator
Checks that every key listed in `defaultEntities` either has a default value in the plan or has been supplied by the user. Hard fail if a required slot is missing.

#### SchemaCompatibilityValidator
Checks that the output fields declared across `steps[].outputFields` are internally consistent (no undefined step references in `dependsOn`).

#### SemanticCoherenceValidator
Extracts content words (len > 4, non-stop-word) from `understanding + description`. Measures what fraction of those words appear in the user's query. If **< 20%** the plan's subject is likely absent from the query — emits a **Warn** (not Fail) so the plan is still attempted but the mismatch is visible in the console trace.

Stop-words excluded from coverage: `which, where, about, would, could, should, their, there, these, those, shall, might, shows, lists, gives, based, given, using, across, within, between, through, against, total, value, values, amount, number, count, level, point, items, company, account, report, query`, and common short words (len ≤ 4).

---

### 2.7 Routing Decision Thresholds

| Condition | Status | Action |
|---|---|---|
| Phase 0 confidence < 0.28 | `Rejected` | Streaming LLM fallback; no retrieval attempted |
| Phase 1 top score < 0.45 | `NoPlanFound` | No plan confident enough |
| Top score < 0.35 | `Rejected` | "No plan found" message |
| Top score ≥ 0.35 and < 0.60 | `Clarify` | Ask user to rephrase |
| Top score ≥ 0.60, gap < 0.08 | `Ambiguous` | Present top candidates |
| Top score ≥ 0.60 and < 0.75, gap ≥ 0.08 | `ConfirmAndExecute` | Show plan, ask yes/no |
| Top score ≥ 0.75 and gap ≥ 0.08 | `Success` | Execute immediately |

Multi-intent: sub-intents that return **`Success` or `ConfirmAndExecute`** are collected. At least one success → `MultiSuccess`; all fail → `NoPlanFound`.

---

### 2.8 Plan YAML Format

```yaml
planId: tax-001
intent: EstimateTaxLiability
agentId: FinancialAgent
domain: finance
displayName: Financial Agent
understanding: "Estimate the tax liability for a given year and entity type based on taxable income after deductions"
description: "Fetch taxable income, apply standard deductions, compute tax using bracket tables, deduct prior payments, and suggest quarterly instalments"
sampleQueries:
  - "How much tax do we owe for 2024?"
  - "What's our tax situation for this year?"
defaultEntities:
  year: "2024"
  entityType: S-Corp
  companyId: DEMO-001
steps:
  - id: 1
    action: fetch-taxable-income
    stepType: Tool
    dependsOn: []
    outputFields: [taxableIncome]
  - id: 2
    action: apply-deductions
    stepType: Tool
    dependsOn: [1]
    outputFields: [deductions, adjustedIncome]
  - id: 3
    action: compute-tax-liability
    stepType: Code
    dependsOn: [2]
    outputFields: [taxLiability, effectiveRate]
```

Key fields used by the routing pipeline:

| Field | Phase | Purpose |
|---|---|---|
| `domain` | 1 metadata | Domain match (weight 0.35) |
| `understanding` | 1 embedding + 2 re-ranking | What the plan achieves; embedded and used in keyword-coherence guard |
| `description` | 1 embedding + 2 re-ranking | How the plan works (stripped Approach); adds computation vocabulary absent from `understanding`; embedded between `understanding` and `sampleQueries` |
| `sampleQueries` | 1 embedding + 2 tiebreaker | Embedded (normalised); Levenshtein tiebreaker in FeatureAlignmentRanker |
| `defaultEntities` | 3 slot coverage | Keys define required inputs; values provide defaults |
| `steps[].outputFields` | 2 overlap + 3 schema | Output field token overlap bonus; schema validation |

The `description` field is generated by `CffPlanTransformer` from the plan's `Reasoning → Approach` section: tool invocation phrases (`via tool_name`, `in a Code step`) and bare multi-underscore tool names are stripped, then `NormalizeUnderstanding` is applied.

---

## 3. Accounting Demo Execution Plans

### Plan A — Generate Cash Flow Report

**Trigger phrases**: "show cash flow", "what's my cash flow", "cash flow last month", "how much money came in"

```
Intent: GenerateCashFlowReport
Agent:  BookkeepingAgent
```

| Step | Action | Input | Output |
|---|---|---|---|
| 1 | `FetchTransactions` | `{ period: "last_30_days" }` | Raw transaction list |
| 2 | `ClassifyDirection` | `{ transactions: $step1 }` | Inflows / Outflows split |
| 3 | `SumByCategory` | `{ classified: $step2 }` | Category totals |
| 4 | `ComputeNetCashFlow` | `{ inflows: $step3.in, outflows: $step3.out }` | Net figure |
| 5 | `RenderSummary` | `{ net: $step4, breakdown: $step3 }` | Formatted report |

**Console output preview:**
```
[STEP 1] Fetching transactions for last 30 days...        ✓  47 transactions loaded
[STEP 2] Classifying inflows and outflows...              ✓  32 inflows / 15 outflows
[STEP 3] Summing by category...                           ✓  6 categories
[STEP 4] Computing net cash flow...                       ✓
[STEP 5] Rendering report...                              ✓

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  CASH FLOW REPORT — Last 30 Days
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Total Inflows:   $148,320.00
  Total Outflows:   $92,450.00
  ─────────────────────────────
  NET CASH FLOW:   +$55,870.00  ▲
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

### Plan B — Create and Send Invoice

**Trigger phrases**: "create invoice", "bill a customer", "send invoice to", "invoice for services"

```
Intent: CreateInvoice
Agent:  InvoiceAgent
```

| Step | Action | Input | Output |
|---|---|---|---|
| 1 | `LookupCustomer` | `{ name: "Acme Corp" }` | Customer record |
| 2 | `ValidateLineItems` | `{ items: [{ desc, qty, rate }] }` | Validated items |
| 3 | `CalculateTotals` | `{ items: $step2, taxRate: 0.08 }` | Subtotal, tax, total |
| 4 | `GenerateInvoiceDoc` | `{ customer: $step1, totals: $step3 }` | Invoice object |
| 5 | `AssignInvoiceNumber` | `{ invoice: $step4 }` | INV-2024-0042 |
| 6 | `MarkAsDraft` | `{ invoiceId: $step5 }` | Status: Draft |

**Console output preview:**
```
[STEP 1] Looking up customer "Acme Corp"...               ✓  Found (ID: CUST-00123)
[STEP 2] Validating 3 line items...                       ✓
[STEP 3] Calculating totals (tax: 8%)...                  ✓
[STEP 4] Generating invoice document...                   ✓
[STEP 5] Assigning invoice number...                      ✓  INV-2024-0042
[STEP 6] Saving as draft...                               ✓

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  INVOICE CREATED — INV-2024-0042
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Customer:   Acme Corp
  Items:      3 line items
  Subtotal:   $5,000.00
  Tax (8%):     $400.00
  Total:      $5,400.00
  Status:     DRAFT
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

### Plan C — Reconcile Bank Account

**Trigger phrases**: "reconcile account", "match my bank statement", "reconcile checking", "bank reconciliation"

```
Intent: ReconcileAccount
Agent:  ReconciliationAgent
```

| Step | Action | Input | Output |
|---|---|---|---|
| 1 | `LoadBookBalance` | `{ accountId: "CHK-001" }` | Book balance: $42,100 |
| 2 | `LoadBankStatement` | `{ accountId: "CHK-001", period: "May" }` | Statement balance: $42,850 |
| 3 | `MatchTransactions` | `{ book: $step1.txns, bank: $step2.txns }` | Matched / unmatched |
| 4 | `IdentifyOutstanding` | `{ unmatched: $step3.unmatched }` | Outstanding checks / deposits |
| 5 | `ComputeAdjustedBalance` | `{ bookBal: $step1, outstanding: $step4 }` | Adjusted balance |
| 6 | `GenerateReconciliationReport` | `{ adjusted: $step5, bankBal: $step2 }` | Match/mismatch report |

**Console output preview:**
```
[STEP 1] Loading book balance for CHK-001...              ✓  $42,100.00
[STEP 2] Loading May bank statement...                    ✓  $42,850.00
[STEP 3] Matching 94 transactions...                      ✓  91 matched / 3 unmatched
[STEP 4] Identifying outstanding items...                 ✓  2 outstanding checks ($750)
[STEP 5] Computing adjusted balance...                    ✓
[STEP 6] Generating reconciliation report...              ✓

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  RECONCILIATION — CHK-001  May
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Book Balance:         $42,100.00
  + Outstanding Deps:       $500.00
  - Outstanding Checks:    ($750.00)
  Adjusted Book Balance: $41,850.00

  Bank Statement Bal:    $41,850.00
  ─────────────────────────────────
  DIFFERENCE:                $0.00  ✓ RECONCILED
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

### Plan D — Estimate Tax Liability

**Trigger phrases**: "estimate my taxes", "how much tax do I owe", "quarterly tax estimate", "tax liability"

```
Intent: EstimateTaxLiability
Agent:  TaxAgent
```

| Step | Action | Input | Output |
|---|---|---|---|
| 1 | `FetchYTDRevenue` | `{ year: 2024, entity: "LLC" }` | YTD revenue |
| 2 | `FetchDeductibleExpenses` | `{ year: 2024 }` | Deductible total |
| 3 | `ComputeTaxableIncome` | `{ revenue: $step1, deductions: $step2 }` | Taxable income |
| 4 | `ApplyTaxBrackets` | `{ income: $step3, entityType: "LLC" }` | Bracket breakdown |
| 5 | `DeductPriorPayments` | `{ brackets: $step4, paid: $prior }` | Remaining liability |
| 6 | `SuggestQuarterlyPayment` | `{ remaining: $step5, quartersLeft: 2 }` | Quarterly estimate |

**Console output preview:**
```
[STEP 1] Fetching YTD revenue (2024)...                   ✓  $320,000.00
[STEP 2] Fetching deductible expenses...                  ✓  $87,500.00
[STEP 3] Computing taxable income...                      ✓  $232,500.00
[STEP 4] Applying federal tax brackets (LLC)...           ✓
[STEP 5] Deducting prior payments ($42,000)...            ✓
[STEP 6] Dividing across remaining 2 quarters...          ✓

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  TAX LIABILITY ESTIMATE — 2024
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  YTD Revenue:        $320,000.00
  Deductions:         ($87,500.00)
  Taxable Income:     $232,500.00

  Estimated Tax:       $58,900.00
  Already Paid:       ($42,000.00)
  Remaining:          $16,900.00
  ─────────────────────────────────
  Suggested Q3+Q4:     $8,450.00 /qtr
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

### Plan E — Profit & Loss Summary

**Trigger phrases**: "show P&L", "profit and loss", "are we profitable", "income statement", "how's the business doing"

```
Intent: GenerateProfitLoss
Agent:  ReportingAgent
```

| Step | Action | Input | Output |
|---|---|---|---|
| 1 | `FetchRevenue` | `{ period: "YTD" }` | Revenue by category |
| 2 | `FetchCOGS` | `{ period: "YTD" }` | Cost of goods sold |
| 3 | `ComputeGrossProfit` | `{ revenue: $step1, cogs: $step2 }` | Gross profit / margin |
| 4 | `FetchOperatingExpenses` | `{ period: "YTD" }` | OpEx by category |
| 5 | `ComputeOperatingIncome` | `{ gross: $step3, opex: $step4 }` | EBIT |
| 6 | `ApplyTaxAndInterest` | `{ ebit: $step5, tax: estimated }` | Net income |
| 7 | `RenderPnL` | `{ all steps }` | Formatted P&L |

**Console output preview:**
```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  PROFIT & LOSS — YTD 2024
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  Revenue
    Product Sales:        $280,000
    Services:              $40,000
  Total Revenue:         $320,000

  Cost of Goods Sold:   ($145,000)
  ────────────────────────────────
  GROSS PROFIT:          $175,000   (54.7% margin)

  Operating Expenses
    Salaries:             ($72,000)
    Rent & Utilities:     ($18,000)
    Marketing:            ($12,000)
    Software:              ($5,500)
  Total OpEx:           ($107,500)
  ────────────────────────────────
  OPERATING INCOME:       $67,500

  Interest & Other:        ($2,100)
  Est. Taxes:             ($16,000)
  ────────────────────────────────
  NET INCOME:             $49,400   ▲
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
```

---

## 4. Demo Walkthrough — Console REPL

The console app runs as an interactive loop:

```
═══════════════════════════════════════════════════
  CFF Routing Layer Demo — Powered by GenOS Pattern
═══════════════════════════════════════════════════
  Type a financial question or 'scenarios' to see examples.
  Type 'cache' to inspect the semantic cache. Type 'exit' to quit.
───────────────────────────────────────────────────

You: what's my cash flow this month?

[PIPELINE]
  ► Stage 1  Guardrail check...              ✓ In domain
  ► Stage 2  Semantic cache lookup...        ✗ MISS (no similar queries cached yet)
  ► Stage 3  Intent classification...        ✓ GenerateCashFlowReport (96%)
  ► Stage 4  Agent registry lookup...        ✓ BookkeepingAgent
  ► Stage 5  Execution plan generated...     ✓ 5 steps
  ► Stage 6  Executing plan...

[... step output ...]
[CACHE] Stored intent embedding for future lookups.

───────────────────────────────────────────────────
You: how much money came in recently?

[PIPELINE]
  ► Stage 1  Guardrail check...              ✓ In domain
  ► Stage 2  Semantic cache lookup...        ✓ HIT (similarity: 91%)
             Matched: "what's my cash flow this month?"
             Skipping classification — reusing intent + plan.
  ► Stage 6  Executing plan (from cache)...

[... step output ...]
```

---

## 5. NuGet Dependencies

```xml
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.*" />
<PackageReference Include="Microsoft.Extensions.Logging.Console" Version="8.*" />
<PackageReference Include="Spectre.Console" Version="0.*" />  <!-- Rich console output -->
```

No external AI API keys required. All classification and embedding is simulated locally.

---

## 6. Key Demo Talking Points

| Concept | What to Say |
|---|---|
| **Routing Layer** | "Every user message passes through a pipeline before any agent sees it — guardrails first, then cache, then classification." |
| **Semantic Cache** | "We don't just cache exact strings — we cache *meaning*. Semantically similar questions hit the cache and skip the expensive LLM call." |
| **Intent Registry** | "Agents are registered with explicit capability contracts. The router doesn't know how to do accounting — it knows *who* does and *when* to ask them." |
| **Execution Plans** | "Instead of free-form agent improvisation, we generate deterministic step-by-step plans. This makes the system auditable, retryable, and testable." |
| **Cost savings** | "In production, Intuit reports 40-60% of requests can be served from the semantic cache, dramatically reducing LLM inference costs." |

---

## 7. Extensibility Path

To evolve this demo toward production:

1. **Replace `EmbeddingSimulator`** with real embeddings via AWS Bedrock (see Section 7A below).
2. **Replace `RuleBasedClassifier`** with a Bedrock-backed LLM classifier (see Section 7A).
3. **Replace in-memory cache** with Redis + `NRedisSearch` vector similarity extension.
4. **Add streaming** — stream step results back to the console in real time.
5. **Add plan persistence** — save plans to SQLite so they survive restarts and can be replayed.

---

## 7A. AWS Bedrock Integration — Real Embeddings & LLM Classifier

### 7A.1 Overview

This section replaces the two simulated components (`EmbeddingSimulator` and `RuleBasedClassifier`) with real AWS Bedrock calls:

| Component | Simulator (demo) | Bedrock (production-grade) |
|---|---|---|
| Embeddings | `EmbeddingSimulator` (keyword vectors) | `BedrockEmbeddingProvider` → Amazon Titan Embeddings V2 |
| Intent Classification | `RuleBasedClassifier` (keyword rules) | `BedrockLlmClassifier` → Amazon Nova Pro / Claude 3 Haiku |
| Embedding persistence | none | Local JSON file, computed once at startup |

At startup the app checks for a local cache file (`embeddings.cache.json`). If absent it calls Bedrock to compute embeddings for all canonical intent phrases, writes them to disk, and uses them for cosine similarity matching for the rest of the session.

---

### 7A.2 Environment File

Create a `.env` file at the solution root. **Never commit this file** — add it to `.gitignore`.

```
# .env
# ─── AWS credentials ──────────────────────────────────────────────────────────
AWS_ACCESS_KEY_ID=YOUR_ACCESS_KEY_HERE
AWS_SECRET_ACCESS_KEY=YOUR_SECRET_KEY_HERE
AWS_SESSION_TOKEN=                          # optional, leave blank if not using STS
AWS_REGION=us-east-1

# ─── Bedrock model IDs ────────────────────────────────────────────────────────
# Embedding model — Amazon Titan Embeddings V2 (1536 dimensions)
BEDROCK_EMBEDDING_MODEL_ID=amazon.titan-embed-text-v2:0

# LLM model for intent classification
# Options: amazon.nova-pro-v1:0 | anthropic.claude-3-haiku-20240307-v1:0
BEDROCK_LLM_MODEL_ID=anthropic.claude-3-haiku-20240307-v1:0

# ─── Embedding cache ──────────────────────────────────────────────────────────
# Path relative to the working directory where precomputed embeddings are stored
EMBEDDING_CACHE_FILE=embeddings.cache.json

# ─── Classifier settings ──────────────────────────────────────────────────────
# Minimum LLM confidence to accept a classified intent (0.0 – 1.0)
CLASSIFIER_MIN_CONFIDENCE=0.75

# ─── Semantic cache threshold ─────────────────────────────────────────────────
SEMANTIC_CACHE_SIMILARITY_THRESHOLD=0.88

# ─── Feature flags ────────────────────────────────────────────────────────────
# Set to "true" to enable real Bedrock calls; "false" uses local simulators
USE_BEDROCK_EMBEDDINGS=true
USE_BEDROCK_LLM=true

# ─── Streaming conversation ───────────────────────────────────────────────────
# Enable streaming fallback when routing returns Unknown intent
USE_BEDROCK_STREAMING=true
STREAMING_MAX_TOKENS=1024
STREAMING_TEMPERATURE=0.7
```

```
# .gitignore additions
.env
embeddings.cache.json
```

---

### 7A.3 NuGet Packages (add to main project)

```xml
<!-- CffRoutingLayerDemo/CffRoutingLayerDemo.csproj — additional packages -->
<PackageReference Include="AWSSDK.BedrockRuntime" Version="3.7.*" />
<PackageReference Include="DotNetEnv" Version="3.1.*" />
<PackageReference Include="System.Text.Json" Version="8.0.*" />
<PackageReference Include="YamlDotNet" Version="13.*" />
```

---

### 7A.4 Configuration Loader

```csharp
// Config/AppConfig.cs
namespace CffRoutingLayerDemo.Config;

using DotNetEnv;

/// <summary>
/// Loads configuration from the .env file (falls back to environment variables
/// already present in the process — useful in CI / ECS / Lambda).
/// </summary>
public sealed class AppConfig
{
    public string AwsRegion               { get; init; }
    public string BedrockEmbeddingModelId { get; init; }
    public string BedrockLlmModelId       { get; init; }
    public string EmbeddingCacheFile      { get; init; }
    public double ClassifierMinConfidence { get; init; }
    public double SemanticCacheSimilarityThreshold { get; init; }
    public bool   UseBedrockEmbeddings   { get; init; }
    public bool   UseBedrockLlm          { get; init; }
    public bool   UseBedrockStreaming    { get; init; }
    public int    StreamingMaxTokens     { get; init; }
    public float  StreamingTemperature   { get; init; }

    private AppConfig(
        string awsRegion,
        string bedrockEmbeddingModelId,
        string bedrockLlmModelId,
        string embeddingCacheFile,
        double classifierMinConfidence,
        double semanticCacheSimilarityThreshold,
        bool useBedrockEmbeddings,
        bool useBedrockLlm,
        bool useBedrockStreaming,
        int  streamingMaxTokens,
        float streamingTemperature)
    {
        AwsRegion                      = awsRegion;
        BedrockEmbeddingModelId        = bedrockEmbeddingModelId;
        BedrockLlmModelId              = bedrockLlmModelId;
        EmbeddingCacheFile             = embeddingCacheFile;
        ClassifierMinConfidence        = classifierMinConfidence;
        SemanticCacheSimilarityThreshold = semanticCacheSimilarityThreshold;
        UseBedrockEmbeddings           = useBedrockEmbeddings;
        UseBedrockLlm                  = useBedrockLlm;
        UseBedrockStreaming             = useBedrockStreaming;
        StreamingMaxTokens             = streamingMaxTokens;
        StreamingTemperature           = streamingTemperature;
    }

    /// <summary>
    /// Loads .env from the current directory (or parent directories) then
    /// reads all values, with safe defaults for optional fields.
    /// </summary>
    public static AppConfig Load(string envFile = ".env")
    {
        // DotNetEnv only writes to the process env; it won't override existing vars.
        if (File.Exists(envFile))
            Env.Load(envFile, LoadOptions.TraversePath());

        return new AppConfig(
            awsRegion:                     Var("AWS_REGION", "us-east-1"),
            bedrockEmbeddingModelId:       Var("BEDROCK_EMBEDDING_MODEL_ID", "amazon.titan-embed-text-v2:0"),
            bedrockLlmModelId:             Var("BEDROCK_LLM_MODEL_ID",       "anthropic.claude-3-haiku-20240307-v1:0"),
            embeddingCacheFile:            Var("EMBEDDING_CACHE_FILE",        "embeddings.cache.json"),
            classifierMinConfidence:       double.Parse(Var("CLASSIFIER_MIN_CONFIDENCE",             "0.75")),
            semanticCacheSimilarityThreshold: double.Parse(Var("SEMANTIC_CACHE_SIMILARITY_THRESHOLD","0.88")),
            useBedrockEmbeddings:          bool.Parse(Var("USE_BEDROCK_EMBEDDINGS", "false")),
            useBedrockLlm:                 bool.Parse(Var("USE_BEDROCK_LLM",        "false")),
            useBedrockStreaming:            bool.Parse(Var("USE_BEDROCK_STREAMING",  "false")),
            streamingMaxTokens:            int.Parse(Var("STREAMING_MAX_TOKENS",   "1024")),
            streamingTemperature:          float.Parse(Var("STREAMING_TEMPERATURE", "0.7"))
        );
    }

    private static string Var(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}
```

---

### 7A.5 Bedrock Embedding Provider

```csharp
// Bedrock/BedrockEmbeddingProvider.cs
namespace CffRoutingLayerDemo.Bedrock;

using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Config;

/// <summary>
/// Calls Amazon Titan Embeddings V2 via Bedrock Runtime to produce real
/// 1536-dimension embeddings. Results are cached to disk on first call.
/// </summary>
public sealed class BedrockEmbeddingProvider : IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;
    private readonly string _cacheFile;

    // In-memory store: phrase → embedding vector
    private Dictionary<string, double[]> _diskCache = new(StringComparer.OrdinalIgnoreCase);

    public BedrockEmbeddingProvider(AppConfig config)
    {
        _modelId   = config.BedrockEmbeddingModelId;
        _cacheFile = config.EmbeddingCacheFile;

        _client = new AmazonBedrockRuntimeClient(RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    // ── Startup ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Called at app startup. Loads existing embeddings from disk; computes
    /// and saves any that are missing. Pass the canonical phrases you want
    /// pre-warmed into the semantic cache.
    /// </summary>
    public async Task InitialiseAsync(IEnumerable<string> canonicalPhrases)
    {
        LoadDiskCache();

        var missing = canonicalPhrases
            .Where(p => !_diskCache.ContainsKey(p))
            .ToList();

        if (missing.Count == 0)
        {
            Console.WriteLine($"[Bedrock] Embedding cache loaded — {_diskCache.Count} entries, no new phrases.");
            return;
        }

        Console.WriteLine($"[Bedrock] Computing {missing.Count} missing embedding(s)...");

        foreach (var phrase in missing)
        {
            _diskCache[phrase] = await CallBedrockAsync(phrase);
            Console.WriteLine($"[Bedrock]   ✓  {phrase}");
        }

        SaveDiskCache();
        Console.WriteLine($"[Bedrock] Cache saved → {_cacheFile}");
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns an embedding for any text. Checks the disk cache first; calls
    /// Bedrock only on a cache miss, then persists the new entry.
    /// </summary>
    public async Task<double[]> EmbedAsync(string text)
    {
        if (_diskCache.TryGetValue(text, out var cached))
            return cached;

        var vector = await CallBedrockAsync(text);
        _diskCache[text] = vector;
        SaveDiskCache();          // persist incrementally
        return vector;
    }

    // ── Bedrock Runtime call ─────────────────────────────────────────────────

    private async Task<double[]> CallBedrockAsync(string text)
    {
        // Titan Embeddings V2 request body
        var body = JsonSerializer.Serialize(new
        {
            inputText  = text,
            dimensions = 1536,
            normalize  = true
        });

        var request = new InvokeModelRequest
        {
            ModelId     = _modelId,
            ContentType = "application/json",
            Accept      = "application/json",
            Body        = new MemoryStream(Encoding.UTF8.GetBytes(body))
        };

        var response = await _client.InvokeModelAsync(request);

        using var reader = new StreamReader(response.Body);
        var json = await reader.ReadToEndAsync();

        using var doc = JsonDocument.Parse(json);

        // Titan V2 returns: { "embedding": [float, ...], "inputTextTokenCount": n }
        var embeddingArray = doc.RootElement
            .GetProperty("embedding")
            .EnumerateArray()
            .Select(e => e.GetDouble())
            .ToArray();

        return embeddingArray;
    }

    // ── Disk persistence ─────────────────────────────────────────────────────

    private void LoadDiskCache()
    {
        if (!File.Exists(_cacheFile)) return;

        try
        {
            var json = File.ReadAllText(_cacheFile);
            _diskCache = JsonSerializer.Deserialize<Dictionary<string, double[]>>(json)
                         ?? new(StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"[Bedrock] Loaded {_diskCache.Count} cached embedding(s) from {_cacheFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bedrock] Warning: could not read cache file — {ex.Message}");
            _diskCache = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveDiskCache()
    {
        var json = JsonSerializer.Serialize(_diskCache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_cacheFile, json);
    }

    public void Dispose() => _client.Dispose();
}
```

---

### 7A.6 Bedrock-Backed Semantic Cache

The existing `InMemorySemanticCache` is extended to accept either sync (simulated) or async (real) embedding sources. An adapter wraps `BedrockEmbeddingProvider` so it satisfies `ISemanticCache` without changing the routing engine.

```csharp
// Cache/BedrockSemanticCache.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Bedrock;
using CffRoutingLayerDemo.Core;

/// <summary>
/// Drop-in replacement for InMemorySemanticCache that uses real Bedrock
/// embeddings instead of the keyword simulator. The cosine similarity
/// logic and cache eviction rules are identical.
/// </summary>
public sealed class BedrockSemanticCache : ISemanticCache
{
    private readonly BedrockEmbeddingProvider _embedder;
    private readonly List<CacheEntry> _entries = [];
    private readonly double _threshold;

    public IReadOnlyList<CacheEntry> Entries => _entries.AsReadOnly();

    public BedrockSemanticCache(BedrockEmbeddingProvider embedder, double similarityThreshold = 0.88)
    {
        _embedder  = embedder;
        _threshold = similarityThreshold;
    }

    /// <summary>
    /// Synchronous lookup — runs the async embed call on the thread-pool.
    /// Acceptable for a console demo; in a web service use LookupAsync instead.
    /// </summary>
    public CacheHit? Lookup(string userMessage)
        => LookupAsync(userMessage).GetAwaiter().GetResult();

    public async Task<CacheHit?> LookupAsync(string userMessage)
    {
        if (_entries.Count == 0) return null;

        var queryVector = await _embedder.EmbedAsync(userMessage);

        return _entries
            .Select(e => new { Entry = e, Score = CosineSimilarity(queryVector, e.Vector) })
            .Where(x => x.Score >= _threshold)
            .OrderByDescending(x => x.Score)
            .Select(x => new CacheHit(x.Entry.Intent, x.Entry.Plan, x.Score))
            .FirstOrDefault();
    }

    public void Store(string userMessage, IntentResult intent, ExecutionPlan plan)
        => StoreAsync(userMessage, intent, plan).GetAwaiter().GetResult();

    public async Task StoreAsync(string userMessage, IntentResult intent, ExecutionPlan plan)
    {
        var vector = await _embedder.EmbedAsync(userMessage);
        _entries.Add(new CacheEntry(vector, intent, plan, DateTime.UtcNow));
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot  = a.Zip(b, (x, y) => x * y).Sum();
        double magA = Math.Sqrt(a.Sum(x => x * x));
        double magB = Math.Sqrt(b.Sum(x => x * x));
        return magA == 0 || magB == 0 ? 0 : dot / (magA * magB);
    }
}
```

---

### 7A.7 Bedrock LLM Intent Classifier

```csharp
// Bedrock/BedrockLlmClassifier.cs
namespace CffRoutingLayerDemo.Bedrock;

using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Config;
using CffRoutingLayerDemo.Core;

/// <summary>
/// Classifies user intent by calling a Bedrock LLM (Claude 3 Haiku or
/// Amazon Nova Pro) with a structured prompt. Falls back to "Unknown"
/// if the model cannot produce a parseable response.
/// </summary>
public sealed class BedrockLlmClassifier : IIntentClassifier, IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;
    private readonly double _minConfidence;

    private static readonly string[] SupportedIntents =
    [
        "GenerateCashFlowReport",
        "CreateInvoice",
        "ReconcileAccount",
        "EstimateTaxLiability",
        "GenerateProfitLoss"
    ];

    private static readonly Dictionary<string, string> IntentToAgent = new()
    {
        ["GenerateCashFlowReport"] = "BookkeepingAgent",
        ["CreateInvoice"]          = "InvoiceAgent",
        ["ReconcileAccount"]       = "ReconciliationAgent",
        ["EstimateTaxLiability"]   = "TaxAgent",
        ["GenerateProfitLoss"]     = "ReportingAgent"
    };

    public BedrockLlmClassifier(AppConfig config)
    {
        _modelId       = config.BedrockLlmModelId;
        _minConfidence = config.ClassifierMinConfidence;
        _client        = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    public IntentResult Classify(string userMessage)
        => ClassifyAsync(userMessage).GetAwaiter().GetResult();

    public async Task<IntentResult> ClassifyAsync(string userMessage)
    {
        var prompt = BuildPrompt(userMessage);
        var responseText = await InvokeAsync(prompt);
        return ParseResponse(responseText, userMessage);
    }

    // ── Prompt ────────────────────────────────────────────────────────────────

    private static string BuildPrompt(string userMessage) => $"""
        You are an intent classification engine for a financial accounting assistant.

        Classify the user message below into EXACTLY ONE of the following intents.
        If the message does not match any intent, use "Unknown".

        Supported intents:
        - GenerateCashFlowReport   : User wants to see cash inflows/outflows or net cash flow
        - CreateInvoice            : User wants to create or send an invoice to a customer
        - ReconcileAccount         : User wants to reconcile a bank account or match transactions
        - EstimateTaxLiability     : User wants to estimate taxes owed
        - GenerateProfitLoss       : User wants a profit & loss or income statement report
        - Unknown                  : Message is not related to any of the above

        User message: "{userMessage}"

        Respond ONLY with a valid JSON object in this exact format — no markdown, no extra text:
        {{
          "intent": "<intent name>",
          "confidence": <0.0 to 1.0>,
          "entities": {{
            "period": "<optional period string>",
            "entityType": "<optional entity type>",
            "accountId": "<optional account id>",
            "customer": "<optional customer name>"
          }},
          "requiresConfirmation": <true|false>
        }}
        """;

    // ── Bedrock invocation ────────────────────────────────────────────────────

    private async Task<string> InvokeAsync(string prompt)
    {
        // Claude 3 / Nova Pro use the Messages API (Converse API is the unified path)
        var request = new ConverseRequest
        {
            ModelId  = _modelId,
            Messages =
            [
                new Message
                {
                    Role    = ConversationRole.User,
                    Content = [new ContentBlock { Text = prompt }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = 300,
                Temperature = 0f   // deterministic output for classification
            }
        };

        var response = await _client.ConverseAsync(request);
        return response.Output.Message.Content[0].Text;
    }

    // ── Response parsing ──────────────────────────────────────────────────────

    private IntentResult ParseResponse(string responseText, string originalMessage)
    {
        try
        {
            // Strip accidental markdown fences the model may add
            var json = responseText.Trim().TrimStart('`');
            if (json.StartsWith("json", StringComparison.OrdinalIgnoreCase))
                json = json[4..];
            json = json.TrimEnd('`').Trim();

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var intent     = root.GetProperty("intent").GetString() ?? "Unknown";
            var confidence = root.GetProperty("confidence").GetDouble();
            var requiresConfirmation = root.TryGetProperty("requiresConfirmation", out var rc)
                                       && rc.GetBoolean();

            var entities = new Dictionary<string, string>();
            if (root.TryGetProperty("entities", out var ents))
            {
                foreach (var prop in ents.EnumerateObject())
                {
                    var val = prop.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(val))
                        entities[prop.Name] = val;
                }
            }

            // Safety: reject low-confidence or unrecognised intents
            if (confidence < _minConfidence || !SupportedIntents.Contains(intent))
                return new IntentResult("Unknown", confidence, entities, "None", false);

            var agentId = IntentToAgent.GetValueOrDefault(intent, "None");
            return new IntentResult(intent, confidence, entities, agentId, requiresConfirmation);
        }
        catch
        {
            return new IntentResult("Unknown", 0.0, new(), "None", false);
        }
    }

    public void Dispose() => _client.Dispose();
}
```

---

### 7A.8 Startup Wiring — Canonical Phrases for Pre-Warm

These are the seed phrases embedded at startup so the semantic cache is ready before the first user query.

```csharp
// Bedrock/CanonicalPhrases.cs
namespace CffRoutingLayerDemo.Bedrock;

/// <summary>
/// One representative phrase per supported intent. Embedded at startup
/// and stored to disk so subsequent runs skip the Bedrock call.
/// </summary>
public static class CanonicalPhrases
{
    public static readonly string[] All =
    [
        // GenerateCashFlowReport
        "what is my cash flow this month",
        "show me cash flow for last 30 days",
        "how much money came in recently",

        // CreateInvoice
        "create an invoice for a customer",
        "bill a client for services rendered",

        // ReconcileAccount
        "reconcile my checking account",
        "match my bank statement transactions",

        // EstimateTaxLiability
        "estimate my tax liability for this year",
        "how much tax do I owe",

        // GenerateProfitLoss
        "show me profit and loss for year to date",
        "is the business profitable this year"
    ];
}
```

---

### 7A.9 Updated Program.cs — Bedrock Startup Flow

> The complete dual-mode REPL with streaming fallback is in **Section 7A.12**. The listing below shows only the startup wiring for quick reference.

```csharp
// Program.cs — startup summary (see Section 7A.12 for full REPL)
var config = AppConfig.Load();                          // reads .env

// Embedding provider
if (config.UseBedrockEmbeddings)
{
    var embedder = new BedrockEmbeddingProvider(config);
    await embedder.InitialiseAsync(CanonicalPhrases.All); // disk cache warm-up
    cache = new BedrockSemanticCache(embedder, config.SemanticCacheSimilarityThreshold);
}

// LLM classifier
if (config.UseBedrockLlm)
    classifier = new BedrockLlmClassifier(config);

// Streaming conversation (fallback for unknown intents)
if (config.UseBedrockStreaming)
    streaming = new BedrockStreamingConversation(config);

var engine = new RoutingEngine(cache, classifier, registry, executor);
// → REPL loop (see Section 7A.12)
```

---

### 7A.10 Disk Cache File Format

`embeddings.cache.json` is a flat JSON dictionary — phrase → float array. Example (truncated):

```json
{
  "what is my cash flow this month": [0.0124, -0.0391, 0.0872, "... 1536 values ..."],
  "show me cash flow for last 30 days": [0.0119, -0.0388, 0.0865, "... 1536 values ..."],
  "reconcile my checking account": [-0.0201, 0.0512, -0.0033, "... 1536 values ..."]
}
```

Subsequent app runs load this file at startup (< 5 ms for ~50 phrases), requiring zero Bedrock calls unless new canonical phrases are added.

---

### 7A.11 Startup Sequence Diagram

```
App start
    │
    ├─ AppConfig.Load()          reads .env + process env vars
    │
    ├─ [if USE_BEDROCK_EMBEDDINGS=true]
    │      BedrockEmbeddingProvider.InitialiseAsync(CanonicalPhrases.All)
    │           │
    │           ├─ File.Exists("embeddings.cache.json")?
    │           │       YES → load all vectors from disk (fast)
    │           │       NO  → compute via Bedrock, save to disk
    │           │
    │           └─ Missing phrases → call Bedrock, append to disk
    │
    ├─ [if USE_BEDROCK_LLM=true]
    │      BedrockLlmClassifier initialised (stateless, no startup cost)
    │
    ├─ RoutingEngine assembled
    │
    └─ REPL loop starts ← user interaction begins here
```

---

### 7A.12 Streaming Conversation (ConverseStream)

When the routing engine cannot identify an accounting intent it falls back to a streaming LLM conversation instead of a bare rejection message. Tokens are printed to the console as they arrive — the user sees a live typing effect with no blocking wait.

#### Architecture: Dual-Mode REPL

```
User query
    │
    ├─ RoutingEngine.HandleAsync()
    │       │
    │       ├─ Intent recognised (high confidence)
    │       │     → execute structured plan → print formatted report
    │       │
    │       └─ Intent = Unknown / guardrail failed
    │             → BedrockStreamingConversation.SendAsync()
    │                   streaming tokens → Console.Write() per chunk
    │                   full turn appended to conversation history
    │
    └─ Loop (history preserved across turns)
```

#### Additional `.env` Variables

```ini
# .env  (append to existing block)
USE_BEDROCK_STREAMING=true     # enable streaming conversation fallback
STREAMING_MAX_TOKENS=1024      # max tokens per streamed response
STREAMING_TEMPERATURE=0.7      # creativity for conversational replies
```

#### `AppConfig.cs` additions

```csharp
// add inside AppConfig record / class
public bool   UseBedrockStreaming       => Env("USE_BEDROCK_STREAMING") == "true";
public int    StreamingMaxTokens        => int.TryParse(Env("STREAMING_MAX_TOKENS"), out var v) ? v : 1024;
public float  StreamingTemperature      => float.TryParse(Env("STREAMING_TEMPERATURE"), out var v) ? v : 0.7f;
```

#### `BedrockStreamingConversation.cs`

```csharp
// Bedrock/BedrockStreamingConversation.cs
namespace CffRoutingLayerDemo.Bedrock;

using System.Text;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using CffRoutingLayerDemo.Config;

/// <summary>
/// Multi-turn streaming conversation backed by the Bedrock ConverseStream API.
/// Each call to <see cref="SendAsync"/> streams tokens through <paramref name="onToken"/>
/// as they arrive, then appends the completed turn to in-memory history so context
/// is preserved across REPL iterations.
/// </summary>
public sealed class BedrockStreamingConversation : IAsyncDisposable, IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly string _modelId;
    private readonly int    _maxTokens;
    private readonly float  _temperature;
    private readonly List<Message> _history = [];

    private static readonly string SystemPrompt = """
        You are a helpful financial accounting assistant for small businesses.
        You can answer questions about bookkeeping, invoicing, tax estimates,
        bank reconciliation, and profit & loss. Keep answers concise and practical.
        If the user asks about something outside accounting or finance, politely
        redirect them back to accounting topics.
        """;

    public IReadOnlyList<Message> History => _history.AsReadOnly();

    public BedrockStreamingConversation(AppConfig config)
    {
        _modelId     = config.BedrockLlmModelId;
        _maxTokens   = config.StreamingMaxTokens;
        _temperature = config.StreamingTemperature;
        _client      = new AmazonBedrockRuntimeClient(
            RegionEndpoint.GetBySystemName(config.AwsRegion));
    }

    /// <summary>
    /// Sends <paramref name="userMessage"/> to the model and streams back the reply.
    /// <paramref name="onToken"/> is called for each text chunk as it arrives.
    /// Returns the complete assistant reply after the stream closes.
    /// </summary>
    public async Task<string> SendAsync(
        string          userMessage,
        Action<string>? onToken = null,
        CancellationToken ct    = default)
    {
        _history.Add(new Message
        {
            Role    = ConversationRole.User,
            Content = [new ContentBlock { Text = userMessage }]
        });

        var request = new ConverseStreamRequest
        {
            ModelId = _modelId,
            System  = [new SystemContentBlock { Text = SystemPrompt }],
            Messages = _history,
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = _maxTokens,
                Temperature = _temperature
            }
        };

        var response = await _client.ConverseStreamAsync(request, ct);
        var sb = new StringBuilder();

        await foreach (var evt in response.Stream.WithCancellation(ct))
        {
            switch (evt)
            {
                case ContentBlockDeltaEvent delta when delta.Delta?.Text is { } chunk:
                    sb.Append(chunk);
                    onToken?.Invoke(chunk);
                    break;

                // MessageStopEvent signals end of generation — nothing to capture
                case MessageStopEvent:
                    break;
            }
        }

        var reply = sb.ToString();

        _history.Add(new Message
        {
            Role    = ConversationRole.Assistant,
            Content = [new ContentBlock { Text = reply }]
        });

        return reply;
    }

    /// <summary>Clears conversation history without disposing the client.</summary>
    public void ClearHistory() => _history.Clear();

    public void Dispose()         => _client.Dispose();
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
```

#### Updated `Program.cs` — Dual-Mode REPL

```csharp
// Program.cs  (Bedrock-enabled version with streaming fallback)
using CffRoutingLayerDemo.Bedrock;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Config;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Demo;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;
using System.Diagnostics;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── 1. Configuration ───────────────────────────────────────────────────────
var config = AppConfig.Load();

// ── 2. Embedding provider & semantic cache ─────────────────────────────────
ISemanticCache cache;
BedrockEmbeddingProvider? bedrockEmbedder = null;

if (config.UseBedrockEmbeddings)
{
    Console.WriteLine("[Startup] Bedrock embeddings ENABLED");
    bedrockEmbedder = new BedrockEmbeddingProvider(config);
    await bedrockEmbedder.InitialiseAsync(CanonicalPhrases.All);
    cache = new BedrockSemanticCache(bedrockEmbedder, config.SemanticCacheSimilarityThreshold);
}
else
{
    Console.WriteLine("[Startup] Using local EmbeddingSimulator");
    cache = new InMemorySemanticCache(new EmbeddingSimulator());
}

// ── 3. Intent classifier ───────────────────────────────────────────────────
IIntentClassifier classifier;
BedrockLlmClassifier? bedrockClassifier = null;

if (config.UseBedrockLlm)
{
    Console.WriteLine("[Startup] Bedrock LLM classifier ENABLED");
    bedrockClassifier = new BedrockLlmClassifier(config);
    classifier = bedrockClassifier;
}
else
{
    Console.WriteLine("[Startup] Using local RuleBasedClassifier");
    classifier = new RuleBasedClassifier();
}

// ── 4. Streaming conversation (fallback for unknown intents) ───────────────
BedrockStreamingConversation? streaming = null;

if (config.UseBedrockStreaming)
{
    Console.WriteLine("[Startup] Bedrock streaming conversation ENABLED");
    streaming = new BedrockStreamingConversation(config);
}

// ── 5. Pipeline assembly ───────────────────────────────────────────────────
var registry = AgentRegistry.BuildDefault();
var executor = new PlanExecutor();
var engine   = new RoutingEngine(cache, classifier, registry, executor);

ConsoleRenderer.PrintBanner();
Console.WriteLine("[Tip] Type 'stream' to toggle streaming-only mode. 'Ctrl+C' to quit.\n");

bool streamingOnlyMode = false;

// ── 6. REPL loop ───────────────────────────────────────────────────────────
while (!cts.IsCancellationRequested)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write(streamingOnlyMode ? "\n[Chat] You: " : "\nYou: ");
    Console.ResetColor();

    string? input;
    try   { input = Console.ReadLine()?.Trim(); }
    catch { break; }

    if (string.IsNullOrWhiteSpace(input)) continue;

    switch (input.ToLowerInvariant())
    {
        case "exit":
        case "quit":
            goto Cleanup;

        case "clear":
            Console.Clear();
            ConsoleRenderer.PrintBanner();
            streaming?.ClearHistory();
            continue;

        case "stream":
            if (streaming is null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error] Set USE_BEDROCK_STREAMING=true in .env to enable streaming.");
                Console.ResetColor();
            }
            else
            {
                streamingOnlyMode = !streamingOnlyMode;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"[Mode] Streaming-only: {(streamingOnlyMode ? "ON" : "OFF")}");
                Console.ResetColor();
            }
            continue;

        case "history":
            if (streaming is null || streaming.History.Count == 0)
                Console.WriteLine("  (no conversation history)");
            else
                foreach (var msg in streaming.History)
                    Console.WriteLine($"  [{msg.Role}] {msg.Content[0].Text[..Math.Min(80, msg.Content[0].Text.Length)]}…");
            continue;

        case "scenarios":
            ConsoleRenderer.PrintScenarios();
            continue;

        case "cache":
            ConsoleRenderer.PrintCacheState(cache.Entries);
            continue;

        case "benchmark":
            await RunQuickBenchmarkAsync(engine, cache);
            continue;
    }

    // ── Streaming-only mode: bypass routing, always stream ─────────────────
    if (streamingOnlyMode && streaming is not null)
    {
        await StreamReplyAsync(streaming, input, cts.Token);
        continue;
    }

    // ── Normal mode: try routing first, fall back to streaming ─────────────
    ConsoleRenderer.PrintPipelineHeader();

    var context = new RoutingContext(
        RequestId:   Guid.NewGuid().ToString(),
        UserMessage: input,
        CompanyId:   "DEMO-CO-001",
        Timestamp:   DateTime.UtcNow);

    var sw     = Stopwatch.StartNew();
    var result = await engine.HandleAsync(context);
    sw.Stop();

    // Routing returned an "out of domain" guardrail message
    bool wasRejected = result.Contains("only assist", StringComparison.OrdinalIgnoreCase);

    if (wasRejected && streaming is not null)
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("[Routing] No accounting intent found — falling back to streaming conversation.");
        Console.ResetColor();
        await StreamReplyAsync(streaming, input, cts.Token);
    }
    else
    {
        ConsoleRenderer.PrintResult(result);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  ⏱  {sw.ElapsedMilliseconds} ms");
        Console.ResetColor();
        Console.WriteLine(new string('─', 60));
    }
}

Cleanup:
bedrockEmbedder?.Dispose();
bedrockClassifier?.Dispose();
if (streaming is not null) await streaming.DisposeAsync();
Console.WriteLine("\nGoodbye.");

// ── Helpers ────────────────────────────────────────────────────────────────

static async Task StreamReplyAsync(
    BedrockStreamingConversation conv,
    string userMessage,
    CancellationToken ct)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("\nAssistant: ");
    Console.ResetColor();

    var sw = Stopwatch.StartNew();

    try
    {
        await conv.SendAsync(
            userMessage,
            onToken: chunk =>
            {
                Console.Write(chunk);   // print each token immediately
            },
            ct: ct);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n[Cancelled]");
        return;
    }

    sw.Stop();
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"\n\n  ⏱  {sw.ElapsedMilliseconds} ms (streamed)");
    Console.ResetColor();
    Console.WriteLine(new string('─', 60));
}

static async Task RunQuickBenchmarkAsync(RoutingEngine engine, ISemanticCache cache)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\nQUICK BENCHMARK — 5 intents × 10 iterations");
    Console.WriteLine(new string('─', 60));
    Console.ResetColor();

    var scenarios = DemoScenarios.All;

    foreach (var (label, query) in scenarios)
    {
        var ctx = new RoutingContext(Guid.NewGuid().ToString(), query, "BENCH", DateTime.UtcNow);
        await engine.HandleAsync(ctx);
    }

    foreach (var (label, query) in scenarios)
    {
        var coldCtx = new RoutingContext(Guid.NewGuid().ToString(), query + " unique", "BENCH", DateTime.UtcNow);
        var coldSw  = Stopwatch.StartNew();
        await engine.HandleAsync(coldCtx);
        coldSw.Stop();

        var hotTimes = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            var ctx = new RoutingContext(Guid.NewGuid().ToString(), query, "BENCH", DateTime.UtcNow);
            var sw  = Stopwatch.StartNew();
            await engine.HandleAsync(ctx);
            sw.Stop();
            hotTimes.Add(sw.ElapsedMilliseconds);
        }

        Console.WriteLine(
            $"  {label,-28}  cold: {coldSw.ElapsedMilliseconds,4} ms  |  cache hit avg: {hotTimes.Average(),5:F1} ms");
    }

    Console.WriteLine(new string('─', 60));
    Console.WriteLine($"  Cache entries: {cache.Entries.Count}");
    Console.WriteLine();
}
```

#### Streaming Flow Diagram

```
User: "what are the depreciation rules for equipment?"
    │
    ├─ RoutingEngine → guardrail → "only assist with accounting"
    │       wasRejected = true
    │
    └─ BedrockStreamingConversation.SendAsync()
            │
            ├─ ConverseStreamRequest → Bedrock
            │
            └─ await foreach (ConverseStreamOutput evt)
                    ├─ ContentBlockDeltaEvent "Depreciation"  → Console.Write()
                    ├─ ContentBlockDeltaEvent " of equipment" → Console.Write()
                    ├─ ContentBlockDeltaEvent " is typically" → Console.Write()
                    ├─ ...  (hundreds of tokens)
                    └─ MessageStopEvent → stream closed, history updated
```

#### REPL Mode Reference

| Command | Behaviour |
|---|---|
| `stream` | Toggle streaming-only mode (all queries → LLM, bypass routing) |
| `history` | Print current conversation history (role + first 80 chars) |
| `clear` | Clear screen AND conversation history |
| any accounting query | Routing first; falls back to streaming if unknown |
| any off-topic query | Guardrail → streaming fallback if `USE_BEDROCK_STREAMING=true` |

---

## 8. Benchmark Design & Test Cases

### 8.1 Goals

| Benchmark Goal | What It Proves |
|---|---|
| Cache hit vs. miss latency | The semantic cache eliminates expensive classification overhead |
| Per-intent routing throughput | The pipeline handles concurrent requests without bottleneck |
| Execution plan step latency | Each agent step completes within acceptable wall-clock time |
| Similarity threshold sensitivity | Correct hit/miss behavior across paraphrase distance |
| Classifier accuracy | The rule-based classifier resolves the right intent per query family |

### 8.2 Project Layout (add alongside demo project)

```
CffRoutingLayerDemo/              ← main console app (see Section 8)
CffRoutingLayerDemo.Benchmarks/   ← BenchmarkDotNet project
CffRoutingLayerDemo.Tests/        ← xUnit unit + integration tests
```

### 8.3 Benchmark YAML Definitions

All benchmark parameters — query phrases, paraphrases, iteration counts, and latency targets — live in YAML. The C# runner reads these files; no hardcoded strings exist in code.

```yaml
# benchmarks/routing-benchmarks.yaml
meta:
  warmupCount: 3
  iterationCount: 10
  memoryDiagnoser: true

scenarios:
  - id: cash-flow
    phrase:     "what is my cash flow this month"
    paraphrase: "show me cash flow report"

  - id: create-invoice
    phrase:     "create an invoice for Acme Corp"
    paraphrase: "make a new invoice for customer"

  - id: reconcile-account
    phrase:     "reconcile my checking account"
    paraphrase: "match my bank statement transactions"

  - id: tax-liability
    phrase:     "estimate my tax liability"
    paraphrase: "calculate my taxes owed"

  - id: profit-loss
    phrase:     "show me profit and loss"
    paraphrase: "display income statement"

  - id: profit-audit
    phrase:     "why did my profit drop last month"
    paraphrase: "why did my expenses increase this month"

  - id: tax-optimization
    phrase:     "find business tax write-offs from my expenses"
    paraphrase: "what deductions can I claim"

  - id: cash-runway
    phrase:     "project my cash runway for the next quarter"
    paraphrase: "how long can we sustain current spending"

targets:
  cacheHitMaxMs:         1
  cacheMissMaxMs:        10
  embeddingOnlyMaxUs:    100
  classifierOnlyMaxUs:   200
  allocBudgetCacheHitKb: 5
  allocBudgetCacheMissKb: 20
```

```yaml
# benchmarks/plan-execution-benchmarks.yaml
meta:
  warmupCount: 2
  iterationCount: 8
  memoryDiagnoser: true

plans:
  - planFile:    plans/cash-flow-report.yaml
    description: "Execute CashFlow plan (5 steps)"
    targetMaxMs: 5
    allocKb:     10

  - planFile:    plans/tax-liability.yaml
    description: "Execute TaxLiability plan (6 steps)"
    targetMaxMs: 6
    allocKb:     12

  - planFile:    plans/reconcile-account.yaml
    description: "Execute Reconciliation plan (6 steps)"
    targetMaxMs: 6
    allocKb:     12

  - planFile:    plans/profit-loss.yaml
    description: "Execute ProfitLoss plan (7 steps)"
    targetMaxMs: 7
    allocKb:     14

  - planFile:    plans/profit-audit.yaml
    description: "Execute ProfitAudit plan (4 steps)"
    targetMaxMs: 5
    allocKb:     10

  - planFile:    plans/cash-runway-forecast.yaml
    description: "Execute CashRunway plan (4 steps)"
    targetMaxMs: 5
    allocKb:     10
```

---

### 8.3A Benchmark C# Runner (reads YAML)

```csharp
// CffRoutingLayerDemo.Benchmarks/BenchmarkConfig.cs
namespace CffRoutingLayerDemo.Benchmarks;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public sealed record RoutingBenchmarkConfig(
    RoutingBenchmarkMeta Meta,
    List<BenchmarkScenario> Scenarios,
    BenchmarkTargets Targets
);

public sealed record RoutingBenchmarkMeta(int WarmupCount, int IterationCount, bool MemoryDiagnoser);
public sealed record BenchmarkScenario(string Id, string Phrase, string Paraphrase);
public sealed record BenchmarkTargets(
    int CacheHitMaxMs, int CacheMissMaxMs,
    int EmbeddingOnlyMaxUs, int ClassifierOnlyMaxUs,
    int AllocBudgetCacheHitKb, int AllocBudgetCacheMissKb
);

public sealed record PlanBenchmarkConfig(
    PlanBenchmarkMeta Meta,
    List<PlanBenchmarkEntry> Plans
);

public sealed record PlanBenchmarkMeta(int WarmupCount, int IterationCount, bool MemoryDiagnoser);
public sealed record PlanBenchmarkEntry(string PlanFile, string Description, int TargetMaxMs, int AllocKb);

public static class BenchmarkConfigLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static RoutingBenchmarkConfig LoadRouting(string path = "benchmarks/routing-benchmarks.yaml")
        => Yaml.Deserialize<RoutingBenchmarkConfig>(File.ReadAllText(path));

    public static PlanBenchmarkConfig LoadPlanExecution(string path = "benchmarks/plan-execution-benchmarks.yaml")
        => Yaml.Deserialize<PlanBenchmarkConfig>(File.ReadAllText(path));
}
```

```csharp
// CffRoutingLayerDemo.Benchmarks/RoutingBenchmarks.cs
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using CffRoutingLayerDemo.Benchmarks;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

[MemoryDiagnoser]
public class RoutingBenchmarks
{
    private RoutingEngine _engine = null!;
    private InMemorySemanticCache _cache = null!;
    private EmbeddingSimulator _embedder = null!;
    private RuleBasedClassifier _classifier = null!;
    private RoutingBenchmarkConfig _config = null!;

    // Parameterised at runtime from YAML — no hardcoded strings in code
    [ParamsSource(nameof(GetPhrases))]
    public string Query { get; set; } = string.Empty;

    public IEnumerable<string> GetPhrases()
    {
        var cfg = BenchmarkConfigLoader.LoadRouting();
        return cfg.Scenarios.Select(s => s.Phrase);
    }

    [GlobalSetup]
    public void Setup()
    {
        _config    = BenchmarkConfigLoader.LoadRouting();
        _embedder  = new EmbeddingSimulator();
        _cache     = new InMemorySemanticCache(_embedder);
        _classifier = new RuleBasedClassifier();
        var registry = AgentRegistry.BuildDefault();
        var executor = new PlanExecutor();
        _engine = new RoutingEngine(_cache, _classifier, registry, executor);

        foreach (var scenario in _config.Scenarios)
        {
            var ctx = new RoutingContext(Guid.NewGuid().ToString(), scenario.Phrase, "BENCH", DateTime.UtcNow);
            _engine.HandleAsync(ctx).GetAwaiter().GetResult();
        }
    }

    [Benchmark(Description = "Cache HIT — full pipeline")]
    public async Task<string> CacheHit_FullPipeline()
    {
        var scenario = _config.Scenarios.First(s => s.Phrase == Query);
        var ctx = new RoutingContext(Guid.NewGuid().ToString(), scenario.Paraphrase, "BENCH", DateTime.UtcNow);
        return await _engine.HandleAsync(ctx);
    }

    [Benchmark(Description = "Cache MISS — classify + plan build")]
    public async Task<string> CacheMiss_ClassifyAndBuild()
    {
        var ctx = new RoutingContext(Guid.NewGuid().ToString(), Query + " " + Guid.NewGuid(), "BENCH", DateTime.UtcNow);
        return await _engine.HandleAsync(ctx);
    }

    [Benchmark(Description = "Embedding + cosine similarity only")]
    public double EmbeddingAndCosineSimilarity()
    {
        var v1 = _embedder.Embed(Query);
        var v2 = _embedder.Embed(_config.Scenarios[0].Phrase);
        return CosineSimilarity(v1, v2);
    }

    [Benchmark(Description = "Intent classification only")]
    public IntentResult IntentClassificationOnly() => _classifier.Classify(Query);

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot  = a.Zip(b, (x, y) => x * y).Sum();
        double magA = Math.Sqrt(a.Sum(x => x * x));
        double magB = Math.Sqrt(b.Sum(x => x * x));
        return magA == 0 || magB == 0 ? 0 : dot / (magA * magB);
    }
}
```

```csharp
// CffRoutingLayerDemo.Benchmarks/PlanExecutionBenchmarks.cs
using BenchmarkDotNet.Attributes;
using CffRoutingLayerDemo.Benchmarks;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

[MemoryDiagnoser]
public class PlanExecutionBenchmarks
{
    private PlanExecutor _executor = null!;
    private PlanBenchmarkConfig _config = null!;
    private Dictionary<string, ExecutionPlan> _plans = new();
    private RoutingContext _ctx = null!;

    [GlobalSetup]
    public void Setup()
    {
        _config   = BenchmarkConfigLoader.LoadPlanExecution();
        _executor = new PlanExecutor();
        _ctx      = new RoutingContext("bench-01", "benchmark", "BENCH", DateTime.UtcNow);

        var loader   = new PlanLoader();
        var registry = AgentRegistry.BuildDefault();

        foreach (var entry in _config.Plans)
        {
            var planDef = loader.Load(entry.PlanFile);
            var agent   = registry.Resolve(planDef.AgentId);
            var intent  = new IntentResult(planDef.Intent, 1.0, planDef.DefaultEntities, planDef.AgentId, false);
            _plans[entry.PlanFile] = agent.BuildPlan(intent, _ctx);
        }
    }

    // Benchmark method generated once per plan entry from the YAML config
    [ParamsSource(nameof(GetPlanFiles))]
    public string PlanFile { get; set; } = string.Empty;

    public IEnumerable<string> GetPlanFiles()
        => BenchmarkConfigLoader.LoadPlanExecution().Plans.Select(p => p.PlanFile);

    [Benchmark]
    public Task<string> ExecutePlan() => _executor.ExecuteAsync(_plans[PlanFile], _ctx);
}
```

```csharp
// CffRoutingLayerDemo.Benchmarks/Program.cs
using BenchmarkDotNet.Running;

BenchmarkRunner.Run<RoutingBenchmarks>();
BenchmarkRunner.Run<PlanExecutionBenchmarks>();
```

### 8.4 Expected Benchmark Targets

Targets are embedded in the YAML and validated by the runner's output assertion step:

| Benchmark | Target Mean | Alloc Budget |
|---|---|---|
| Cache HIT — full pipeline | < 1 ms | < 5 KB |
| Cache MISS — classify + build | < 10 ms | < 20 KB |
| Embedding + cosine similarity | < 100 µs | < 2 KB |
| Intent classification only | < 200 µs | < 1 KB |
| Execute any 5-step plan | < 5 ms | < 10 KB |
| Execute any 6-step plan | < 6 ms | < 12 KB |
| Execute any 7-step plan | < 7 ms | < 14 KB |

---

### 8.5 Test YAML Definitions

```yaml
# tests/classifier-tests.yaml
classifierTests:
  - id: cash-flow
    expectedIntent: GenerateCashFlowReport
    minConfidence: 0.80
    queries:
      - "what is my cash flow this month"
      - "show me cash flow for last 30 days"
      - "how much money came in recently"
      - "cash flow report please"
      - "show inflows and outflows"

  - id: create-invoice
    expectedIntent: CreateInvoice
    minConfidence: 0.80
    queries:
      - "create an invoice for Acme Corp"
      - "bill a customer for consulting"
      - "make a new invoice"
      - "send invoice to client"
      - "I need to invoice someone"

  - id: reconcile-account
    expectedIntent: ReconcileAccount
    minConfidence: 0.80
    queries:
      - "reconcile my checking account"
      - "bank reconciliation for May"
      - "match my bank statement"
      - "reconcile CHK-001"

  - id: tax-liability
    expectedIntent: EstimateTaxLiability
    minConfidence: 0.80
    queries:
      - "estimate my tax liability"
      - "how much tax do I owe"
      - "quarterly tax estimate"
      - "what is my tax bill for 2024"

  - id: profit-loss
    expectedIntent: GenerateProfitLoss
    minConfidence: 0.80
    queries:
      - "show me profit and loss"
      - "are we profitable this year"
      - "income statement please"
      - "how is the business doing"
      - "P&L report"

  - id: profit-audit
    expectedIntent: AnalyzeProfitAnomaly
    minConfidence: 0.80
    queries:
      - "why did my profit drop last month"
      - "why did my expenses increase this month"
      - "what caused the revenue decline"

  - id: tax-optimization
    expectedIntent: OptimizeTaxDeductions
    minConfidence: 0.80
    queries:
      - "find business tax write-offs from my expenses"
      - "what deductions can I claim"
      - "surface potential tax write-offs"

  - id: cash-runway
    expectedIntent: ForecastCashRunway
    minConfidence: 0.80
    queries:
      - "project my cash runway for the next quarter"
      - "how long can we sustain current spending"
      - "show my cash runway horizon calculations"

  - id: out-of-domain
    expectedIntent: Unknown
    maxConfidence: 0.50
    queries:
      - "write me a poem"
      - "what is the weather today"
      - "book me a flight to Paris"
      - "who won the football game last night"
```

```yaml
# tests/cache-tests.yaml
cacheTests:
  - id: empty-cache-miss
    seedPhrases: []
    lookupPhrase: "what is my cash flow"
    expectHit: false

  - id: exact-match-hit
    seedPhrases:
      - phrase: "what is my cash flow this month"
        intent: GenerateCashFlowReport
    lookupPhrase: "what is my cash flow this month"
    expectHit: true
    minScore: 0.99

  - id: semantic-paraphrase-hit
    seedPhrases:
      - phrase: "what is my cash flow this month"
        intent: GenerateCashFlowReport
    lookupPhrase: "show me cash flow report"
    expectHit: true

  - id: unrelated-miss
    seedPhrases:
      - phrase: "what is my cash flow this month"
        intent: GenerateCashFlowReport
    lookupPhrase: "estimate my tax liability for 2024"
    expectHit: false

  - id: multi-entry-disambiguation
    seedPhrases:
      - phrase: "what is my cash flow"
        intent: GenerateCashFlowReport
      - phrase: "reconcile my bank account"
        intent: ReconcileAccount
      - phrase: "estimate tax liability"
        intent: EstimateTaxLiability
    lookupPhrase: "show cash flow summary"
    expectHit: true
    expectedIntent: GenerateCashFlowReport

  - id: cross-intent-isolation
    seedPhrases:
      - phrase: "what is my cash flow this month"
        intent: GenerateCashFlowReport
    lookupPhrase: "estimate my tax liability"
    expectHit: false

  - id: high-volume-store
    storageCount: 100
    expectNoException: true
```

```yaml
# tests/plan-tests.yaml
planStructureTests:
  - planFile: plans/cash-flow-report.yaml
    expectedStepCount: 5
    expectedOutputContains: "CASH FLOW"

  - planFile: plans/create-invoice.yaml
    expectedStepCount: 6
    expectedOutputContains: "INV-"

  - planFile: plans/reconcile-account.yaml
    expectedStepCount: 6
    expectedOutputContains: "RECONCIL"

  - planFile: plans/tax-liability.yaml
    expectedStepCount: 6
    expectedOutputContains: "TAX"

  - planFile: plans/profit-loss.yaml
    expectedStepCount: 7
    expectedOutputContains: "PROFIT"

  - planFile: plans/profit-audit.yaml
    expectedStepCount: 4
    expectedOutputContains: "ANOMALY"

  - planFile: plans/tax-optimization.yaml
    expectedStepCount: 4
    expectedOutputContains: "DEDUCTION"

  - planFile: plans/cash-runway-forecast.yaml
    expectedStepCount: 4
    expectedOutputContains: "RUNWAY"

planInvariantTests:
  - invariant: SequentialStepIds
    description: "Step IDs must be sequential starting from 1"
    applyToAll: true

  - invariant: DependenciesReferenceEarlierSteps
    description: "Every dependsOn ID must be less than the current step ID"
    applyToAll: true
```

```yaml
# tests/integration-tests.yaml
guardrailTests:
  - id: poem-rejected
    query: "write me a poem about flowers"
    expectOutputContains: "only assist"
  - id: sports-rejected
    query: "who won the football game last night"
    expectOutputContains: "only assist"
  - id: joke-rejected
    query: "tell me a joke"
    expectOutputContains: "only assist"

endToEndTests:
  - id: cash-flow-routing
    query: "what is my cash flow this month"
    expectedOutputContains: "CASH FLOW"

  - id: invoice-routing
    query: "create invoice for Acme Corp"
    expectedOutputContains: "INV-"

  - id: reconciliation-routing
    query: "reconcile my checking account"
    expectedOutputContains: "RECONCIL"

  - id: tax-routing
    query: "estimate my tax liability"
    expectedOutputContains: "TAX"

  - id: profit-loss-routing
    query: "show profit and loss"
    expectedOutputContains: "PROFIT"

  - id: profit-audit-routing
    query: "why did my profit drop last month"
    expectedOutputContains: "ANOMALY"

  - id: cash-runway-routing
    query: "project my cash runway for the next quarter"
    expectedOutputContains: "RUNWAY"

cacheCoherenceTests:
  - id: second-similar-hits-cache
    firstQuery:  "what is my cash flow this month"
    secondQuery: "show me cash flow report"
    expectCacheHitOnSecond: true
    minScore: 0.88

  - id: different-intents-do-not-cross-contaminate
    firstQuery:   "what is my cash flow this month"
    unrelatedQuery: "estimate my tax liability"
    expectNoHit: true

idempotencyTests:
  - id: repeated-pnl-query
    query: "show me profit and loss"
    repeatCount: 2
    allOutputsMustContain: "NET INCOME"
```

---

### 8.5A Test C# Runner (reads YAML)

```csharp
// CffRoutingLayerDemo.Tests/YamlTestLoader.cs
namespace CffRoutingLayerDemo.Tests;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public static class YamlTestLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static T Load<T>(string path)
        => Yaml.Deserialize<T>(File.ReadAllText(path));
}

// ── Model types ──────────────────────────────────────────────────────────────

public record ClassifierTestSuite(List<ClassifierTestCase> ClassifierTests);
public record ClassifierTestCase(
    string Id,
    string ExpectedIntent,
    double MinConfidence,
    double MaxConfidence,
    List<string> Queries
);

public record CacheTestSuite(List<CacheTestCase> CacheTests);
public record CacheTestCase(
    string Id,
    List<CacheSeedPhrase> SeedPhrases,
    string LookupPhrase,
    bool ExpectHit,
    double MinScore,
    string? ExpectedIntent,
    int StorageCount,
    bool ExpectNoException
);
public record CacheSeedPhrase(string Phrase, string Intent);

public record PlanTestSuite(
    List<PlanStructureTest> PlanStructureTests,
    List<PlanInvariantTest> PlanInvariantTests
);
public record PlanStructureTest(
    string PlanFile, int ExpectedStepCount, string ExpectedOutputContains
);
public record PlanInvariantTest(string Invariant, string Description, bool ApplyToAll);

public record IntegrationTestSuite(
    List<GuardrailTest> GuardrailTests,
    List<EndToEndTest> EndToEndTests,
    List<CacheCoherenceTest> CacheCoherenceTests,
    List<IdempotencyTest> IdempotencyTests
);
public record GuardrailTest(string Id, string Query, string ExpectOutputContains);
public record EndToEndTest(string Id, string Query, string ExpectedOutputContains);
public record CacheCoherenceTest(
    string Id, string FirstQuery, string SecondQuery,
    string UnrelatedQuery, bool ExpectCacheHitOnSecond,
    bool ExpectNoHit, double MinScore
);
public record IdempotencyTest(string Id, string Query, int RepeatCount, string AllOutputsMustContain);
```

```csharp
// CffRoutingLayerDemo.Tests/IntentClassifierTests.cs
using Xunit;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Tests;

public class IntentClassifierTests
{
    private readonly RuleBasedClassifier _classifier = new();
    private static readonly ClassifierTestSuite Suite =
        YamlTestLoader.Load<ClassifierTestSuite>("tests/classifier-tests.yaml");

    public static IEnumerable<object[]> InDomainCases() =>
        Suite.ClassifierTests
            .Where(tc => tc.ExpectedIntent != "Unknown")
            .SelectMany(tc => tc.Queries.Select(q => new object[] { q, tc.ExpectedIntent, tc.MinConfidence }));

    public static IEnumerable<object[]> OutOfDomainCases() =>
        Suite.ClassifierTests
            .Where(tc => tc.ExpectedIntent == "Unknown")
            .SelectMany(tc => tc.Queries.Select(q => new object[] { q, tc.MaxConfidence }));

    [Theory]
    [MemberData(nameof(InDomainCases))]
    public void ShouldClassify_ToExpectedIntent(string query, string expectedIntent, double minConfidence)
    {
        var result = _classifier.Classify(query);
        Assert.Equal(expectedIntent, result.Intent);
        Assert.True(result.Confidence >= minConfidence,
            $"Expected confidence >= {minConfidence} but got {result.Confidence} for '{query}'");
    }

    [Theory]
    [MemberData(nameof(OutOfDomainCases))]
    public void ShouldReturnUnknown_ForOutOfDomainQueries(string query, double maxConfidence)
    {
        var result = _classifier.Classify(query);
        Assert.Equal("Unknown", result.Intent);
        Assert.True(result.Confidence < maxConfidence,
            $"Expected confidence < {maxConfidence} but got {result.Confidence} for '{query}'");
    }
}
```

```csharp
// CffRoutingLayerDemo.Tests/SemanticCacheTests.cs
using Xunit;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Tests;

public class SemanticCacheTests
{
    private readonly EmbeddingSimulator _embedder = new();
    private static readonly CacheTestSuite Suite =
        YamlTestLoader.Load<CacheTestSuite>("tests/cache-tests.yaml");

    private InMemorySemanticCache BuildSeededCache(IEnumerable<CacheSeedPhrase> seeds)
    {
        var cache = new InMemorySemanticCache(_embedder);
        foreach (var seed in seeds)
        {
            var intent = new IntentResult(seed.Intent, 0.95, new(), "TestAgent", false);
            var plan   = new ExecutionPlan("plan-test", seed.Intent,
                [new PlanStep(1, "DoSomething", new(), [])]);
            cache.Store(seed.Phrase, intent, plan);
        }
        return cache;
    }

    public static IEnumerable<object[]> AllCases() =>
        Suite.CacheTests.Select(tc => new object[] { tc });

    [Theory]
    [MemberData(nameof(AllCases))]
    public void CacheTest(CacheTestCase tc)
    {
        if (tc.StorageCount > 0)
        {
            // High-volume store test — no exception expected
            var cache = new InMemorySemanticCache(_embedder);
            var ex    = Record.Exception(() =>
            {
                for (int i = 0; i < tc.StorageCount; i++)
                    cache.Store($"query {i}",
                        new IntentResult("TestIntent", 0.9, new(), "TestAgent", false),
                        new ExecutionPlan("plan-test", "TestIntent",
                            [new PlanStep(1, "DoSomething", new(), [])]));
            });
            if (tc.ExpectNoException) Assert.Null(ex);
            return;
        }

        var seeded = BuildSeededCache(tc.SeedPhrases);
        var hit    = seeded.Lookup(tc.LookupPhrase);

        if (tc.ExpectHit)
        {
            Assert.NotNull(hit);
            if (tc.MinScore > 0)  Assert.True(hit!.Score >= tc.MinScore);
            if (tc.ExpectedIntent is not null)
                Assert.Equal(tc.ExpectedIntent, hit!.Intent.Intent);
        }
        else
        {
            Assert.Null(hit);
        }
    }
}
```

```csharp
// CffRoutingLayerDemo.Tests/ExecutionPlanTests.cs
using Xunit;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;
using CffRoutingLayerDemo.Tests;

public class ExecutionPlanTests
{
    private static readonly PlanTestSuite Suite =
        YamlTestLoader.Load<PlanTestSuite>("tests/plan-tests.yaml");

    private readonly PlanLoader    _loader   = new();
    private readonly PlanExecutor  _executor = new();
    private readonly AgentRegistry _registry = AgentRegistry.BuildDefault();
    private readonly RoutingContext _ctx =
        new("test-01", "test query", "TEST-CO", DateTime.UtcNow);

    public static IEnumerable<object[]> StructureCases() =>
        Suite.PlanStructureTests.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(StructureCases))]
    public async Task PlanTest(PlanStructureTest tc)
    {
        var planDef = _loader.Load(tc.PlanFile);
        var agent   = _registry.Resolve(planDef.AgentId);
        var intent  = new IntentResult(planDef.Intent, 0.97, planDef.DefaultEntities, planDef.AgentId, false);
        var plan    = agent.BuildPlan(intent, _ctx);

        // Structure
        Assert.Equal(tc.ExpectedStepCount, plan.Steps.Count);

        // Sequential IDs
        var ids = plan.Steps.Select(s => s.StepId).ToList();
        Assert.Equal(ids.OrderBy(x => x), ids);

        // Dependencies reference only earlier steps
        foreach (var step in plan.Steps)
            foreach (var dep in step.DependsOn)
                Assert.True(dep < step.StepId);

        // Execution output
        var result = await _executor.ExecuteAsync(plan, _ctx);
        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains(tc.ExpectedOutputContains, result, StringComparison.OrdinalIgnoreCase);
    }
}
```

```csharp
// CffRoutingLayerDemo.Tests/RoutingEngineIntegrationTests.cs
using Xunit;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;
using CffRoutingLayerDemo.Tests;

public class RoutingEngineIntegrationTests
{
    private static readonly IntegrationTestSuite Suite =
        YamlTestLoader.Load<IntegrationTestSuite>("tests/integration-tests.yaml");

    private RoutingEngine BuildEngine() =>
        new(new InMemorySemanticCache(new EmbeddingSimulator()),
            new RuleBasedClassifier(),
            AgentRegistry.BuildDefault(),
            new PlanExecutor());

    private static RoutingContext Ctx(string msg) =>
        new(Guid.NewGuid().ToString(), msg, "INT-TEST-CO", DateTime.UtcNow);

    public static IEnumerable<object[]> GuardrailCases() =>
        Suite.GuardrailTests.Select(t => new object[] { t.Query, t.ExpectOutputContains });

    public static IEnumerable<object[]> EndToEndCases() =>
        Suite.EndToEndTests.Select(t => new object[] { t.Query, t.ExpectedOutputContains });

    [Theory]
    [MemberData(nameof(GuardrailCases))]
    public async Task GuardrailTest(string query, string expectedFragment)
    {
        var result = await BuildEngine().HandleAsync(Ctx(query));
        Assert.Contains(expectedFragment, result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(EndToEndCases))]
    public async Task EndToEndRoutingTest(string query, string expectedFragment)
    {
        var result = await BuildEngine().HandleAsync(Ctx(query));
        Assert.Contains(expectedFragment, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CacheCoherence_SecondSimilarHitsCache()
    {
        var tc    = Suite.CacheCoherenceTests.First(t => t.ExpectCacheHitOnSecond);
        var cache = new InMemorySemanticCache(new EmbeddingSimulator());
        var engine = new RoutingEngine(cache, new RuleBasedClassifier(),
            AgentRegistry.BuildDefault(), new PlanExecutor());

        await engine.HandleAsync(Ctx(tc.FirstQuery));
        var hit = cache.Lookup(tc.SecondQuery);

        Assert.NotNull(hit);
        Assert.True(hit!.Score >= tc.MinScore);
    }

    [Fact]
    public async Task CacheCoherence_DifferentIntentsDoNotCross()
    {
        var tc    = Suite.CacheCoherenceTests.First(t => t.ExpectNoHit);
        var cache = new InMemorySemanticCache(new EmbeddingSimulator());
        var engine = new RoutingEngine(cache, new RuleBasedClassifier(),
            AgentRegistry.BuildDefault(), new PlanExecutor());

        await engine.HandleAsync(Ctx(tc.FirstQuery));
        var hit = cache.Lookup(tc.UnrelatedQuery);
        Assert.Null(hit);
    }

    [Fact]
    public async Task Idempotency_RepeatedQueriesProduceSameStructure()
    {
        foreach (var tc in Suite.IdempotencyTests)
        {
            var engine = BuildEngine();
            var results = new List<string>();
            for (int i = 0; i < tc.RepeatCount; i++)
                results.Add(await engine.HandleAsync(Ctx(tc.Query)));

            foreach (var r in results)
                Assert.Contains(tc.AllOutputsMustContain, r, StringComparison.OrdinalIgnoreCase);
        }
    }
}

    [Fact]
    public void Store_ShouldNotThrow_WhenCalledMultipleTimes()
    {
        var cache = BuildCache();
        for (int i = 0; i < 100; i++)
        {
            cache.Store($"query {i}", DummyIntent("TestIntent"), DummyPlan("TestIntent"));
        }
        // No assertion needed — validates no exception / memory corruption
    }
}
```

```csharp
// CffRoutingLayerDemo.Tests/ExecutionPlanTests.cs
using Xunit;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

public class ExecutionPlanTests
{
    private readonly AgentRegistry _registry = AgentRegistry.BuildDefault();
    private readonly PlanExecutor _executor = new();
    private readonly RoutingContext _ctx =
        new("test-01", "test query", "TEST-CO", DateTime.UtcNow);

    // ── Plan structure tests ──────────────────────────────────────────────

    [Fact]
    public void CashFlowPlan_ShouldHave5Steps()
    {
        var intent = new IntentResult("GenerateCashFlowReport", 0.97,
            new() { ["period"] = "last_30_days" }, "BookkeepingAgent", false);
        var plan = _registry.Resolve("BookkeepingAgent").BuildPlan(intent, _ctx);

        Assert.Equal(5, plan.Steps.Count);
    }

    [Fact]
    public void InvoicePlan_ShouldHave6Steps()
    {
        var intent = new IntentResult("CreateInvoice", 0.95,
            new() { ["customer"] = "Acme Corp" }, "InvoiceAgent", false);
        var plan = _registry.Resolve("InvoiceAgent").BuildPlan(intent, _ctx);

        Assert.Equal(6, plan.Steps.Count);
    }

    [Fact]
    public void ReconciliationPlan_ShouldHave6Steps()
    {
        var intent = new IntentResult("ReconcileAccount", 0.96,
            new() { ["accountId"] = "CHK-001", ["period"] = "May" }, "ReconciliationAgent", false);
        var plan = _registry.Resolve("ReconciliationAgent").BuildPlan(intent, _ctx);

        Assert.Equal(6, plan.Steps.Count);
    }

    [Fact]
    public void TaxPlan_ShouldHave6Steps()
    {
        var intent = new IntentResult("EstimateTaxLiability", 0.98,
            new() { ["year"] = "2024", ["entityType"] = "LLC" }, "TaxAgent", false);
        var plan = _registry.Resolve("TaxAgent").BuildPlan(intent, _ctx);

        Assert.Equal(6, plan.Steps.Count);
    }

    [Fact]
    public void ProfitLossPlan_ShouldHave7Steps()
    {
        var intent = new IntentResult("GenerateProfitLoss", 0.94,
            new() { ["period"] = "YTD" }, "ReportingAgent", false);
        var plan = _registry.Resolve("ReportingAgent").BuildPlan(intent, _ctx);

        Assert.Equal(7, plan.Steps.Count);
    }

    // ── Plan step ordering & dependency validation ────────────────────────

    [Fact]
    public void PlanSteps_ShouldHaveSequentialStepIds()
    {
        var intent = new IntentResult("GenerateCashFlowReport", 0.97,
            new() { ["period"] = "last_30_days" }, "BookkeepingAgent", false);
        var plan = _registry.Resolve("BookkeepingAgent").BuildPlan(intent, _ctx);

        var ids = plan.Steps.Select(s => s.StepId).ToList();
        Assert.Equal(ids.OrderBy(x => x), ids);
    }

    [Fact]
    public void PlanSteps_DependenciesShouldReferenceEarlierSteps()
    {
        var intent = new IntentResult("GenerateProfitLoss", 0.94,
            new() { ["period"] = "YTD" }, "ReportingAgent", false);
        var plan = _registry.Resolve("ReportingAgent").BuildPlan(intent, _ctx);

        foreach (var step in plan.Steps)
        {
            foreach (var dep in step.DependsOn)
            {
                Assert.True(dep < step.StepId,
                    $"Step {step.StepId} depends on step {dep} which is not earlier");
            }
        }
    }

    // ── Plan execution output tests ───────────────────────────────────────

    [Fact]
    public async Task ExecuteCashFlowPlan_ShouldReturnNonEmptyResult()
    {
        var intent = new IntentResult("GenerateCashFlowReport", 0.97,
            new() { ["period"] = "last_30_days" }, "BookkeepingAgent", false);
        var plan = _registry.Resolve("BookkeepingAgent").BuildPlan(intent, _ctx);

        var result = await _executor.ExecuteAsync(plan, _ctx);

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Contains("CASH FLOW", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteInvoicePlan_ShouldContainInvoiceNumber()
    {
        var intent = new IntentResult("CreateInvoice", 0.95,
            new() { ["customer"] = "Acme Corp" }, "InvoiceAgent", false);
        var plan = _registry.Resolve("InvoiceAgent").BuildPlan(intent, _ctx);

        var result = await _executor.ExecuteAsync(plan, _ctx);

        Assert.Contains("INV-", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteReconciliationPlan_ShouldIndicateReconciled()
    {
        var intent = new IntentResult("ReconcileAccount", 0.96,
            new() { ["accountId"] = "CHK-001", ["period"] = "May" }, "ReconciliationAgent", false);
        var plan = _registry.Resolve("ReconciliationAgent").BuildPlan(intent, _ctx);

        var result = await _executor.ExecuteAsync(plan, _ctx);

        Assert.Contains("RECONCIL", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteTaxPlan_ShouldContainEstimate()
    {
        var intent = new IntentResult("EstimateTaxLiability", 0.98,
            new() { ["year"] = "2024", ["entityType"] = "LLC" }, "TaxAgent", false);
        var plan = _registry.Resolve("TaxAgent").BuildPlan(intent, _ctx);

        var result = await _executor.ExecuteAsync(plan, _ctx);

        Assert.Contains("TAX", result, StringComparison.OrdinalIgnoreCase);
    }
}
```

```csharp
// CffRoutingLayerDemo.Tests/RoutingEngineIntegrationTests.cs
using Xunit;
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

public class RoutingEngineIntegrationTests
{
    private RoutingEngine BuildEngine() =>
        new(new InMemorySemanticCache(new EmbeddingSimulator()),
            new RuleBasedClassifier(),
            AgentRegistry.BuildDefault(),
            new PlanExecutor());

    private static RoutingContext Ctx(string msg) =>
        new(Guid.NewGuid().ToString(), msg, "INT-TEST-CO", DateTime.UtcNow);

    // ── Guardrail tests ───────────────────────────────────────────────────

    [Theory]
    [InlineData("write me a poem about flowers")]
    [InlineData("who won the football game last night")]
    [InlineData("tell me a joke")]
    public async Task OutOfDomainRequest_ShouldBeRejectedByGuardrail(string query)
    {
        var engine = BuildEngine();
        var result = await engine.HandleAsync(Ctx(query));

        Assert.Contains("only assist", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── Cache behaviour ────────────────────────────────────────────────────

    [Fact]
    public async Task SecondSimilarRequest_ShouldBeServedFromCache()
    {
        var cache = new InMemorySemanticCache(new EmbeddingSimulator());
        var engine = new RoutingEngine(cache, new RuleBasedClassifier(),
            AgentRegistry.BuildDefault(), new PlanExecutor());

        // First request — cold
        await engine.HandleAsync(Ctx("what is my cash flow this month"));

        // Confirm something is in the cache
        var hit = cache.Lookup("show me cash flow report");
        Assert.NotNull(hit);
        Assert.True(hit!.Score >= 0.88);
    }

    [Fact]
    public async Task DifferentIntents_ShouldNotCrossContaminateCache()
    {
        var cache = new InMemorySemanticCache(new EmbeddingSimulator());
        var engine = new RoutingEngine(cache, new RuleBasedClassifier(),
            AgentRegistry.BuildDefault(), new PlanExecutor());

        await engine.HandleAsync(Ctx("what is my cash flow this month"));

        // Tax query should NOT hit the cash-flow cache entry
        var taxHit = cache.Lookup("estimate my tax liability");
        Assert.Null(taxHit);
    }

    // ── End-to-end routing per plan ────────────────────────────────────────

    [Theory]
    [InlineData("what is my cash flow this month", "CASH FLOW")]
    [InlineData("create invoice for Acme Corp", "INV-")]
    [InlineData("reconcile my checking account", "RECONCIL")]
    [InlineData("estimate my tax liability", "TAX")]
    [InlineData("show profit and loss", "PROFIT")]
    public async Task EndToEnd_ShouldRouteToCorrectAgentAndReturnExpectedOutput(
        string query, string expectedOutputFragment)
    {
        var engine = BuildEngine();
        var result = await engine.HandleAsync(Ctx(query));

        Assert.Contains(expectedOutputFragment, result, StringComparison.OrdinalIgnoreCase);
    }

    // ── Idempotency ────────────────────────────────────────────────────────

    [Fact]
    public async Task SameQueryRunTwice_ShouldProduceSameStructuralOutput()
    {
        var engine = BuildEngine();
        var ctx1 = Ctx("show me profit and loss");
        var ctx2 = Ctx("show me profit and loss");

        var result1 = await engine.HandleAsync(ctx1);
        var result2 = await engine.HandleAsync(ctx2);

        // Both should contain the same structural markers even though
        // the second is served from cache
        Assert.Contains("NET INCOME", result1, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NET INCOME", result2, StringComparison.OrdinalIgnoreCase);
    }
}
```

---

## 9. Full Implementation Code

### 9.1 Solution & Project Files

```xml
<!-- CffRoutingLayerDemo/CffRoutingLayerDemo.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Console" Version="8.0.0" />
    <PackageReference Include="Spectre.Console" Version="0.49.1" />
    <PackageReference Include="AWSSDK.BedrockRuntime" Version="3.7.*" />
    <PackageReference Include="DotNetEnv" Version="3.1.*" />
    <PackageReference Include="YamlDotNet" Version="13.*" />
  </ItemGroup>
</Project>
```

```xml
<!-- CffRoutingLayerDemo.Tests/CffRoutingLayerDemo.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.10.0" />
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="YamlDotNet" Version="13.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\CffRoutingLayerDemo\CffRoutingLayerDemo.csproj" />
  </ItemGroup>
</Project>
```

```xml
<!-- CffRoutingLayerDemo.Benchmarks/CffRoutingLayerDemo.Benchmarks.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.14.0" />
    <PackageReference Include="YamlDotNet" Version="13.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\CffRoutingLayerDemo\CffRoutingLayerDemo.csproj" />
  </ItemGroup>
</Project>
```

---

### 9.2 Core Models

```csharp
// Core/IntentResult.cs
namespace CffRoutingLayerDemo.Core;

public record IntentResult(
    string Intent,
    double Confidence,
    Dictionary<string, string> Entities,
    string AgentId,
    bool RequiresConfirmation,
    bool FromCache = false
);
```

```csharp
// Core/ExecutionPlan.cs
namespace CffRoutingLayerDemo.Core;

public record PlanStep(
    int StepId,
    string Action,
    Dictionary<string, string> Input,
    int[] DependsOn
);

public record ExecutionPlan(
    string PlanId,
    string Intent,
    List<PlanStep> Steps
);
```

```csharp
// Core/RoutingContext.cs
namespace CffRoutingLayerDemo.Core;

public record RoutingContext(
    string RequestId,
    string UserMessage,
    string CompanyId,
    DateTime Timestamp
);
```

---

### 9.3 Embedding & Semantic Cache

```csharp
// Cache/EmbeddingSimulator.cs
namespace CffRoutingLayerDemo.Cache;

/// <summary>
/// Produces keyword-dimension vectors that simulate semantic similarity
/// without any external API. Phrases sharing domain keywords score high
/// cosine similarity; unrelated phrases score near zero.
/// </summary>
public sealed class EmbeddingSimulator
{
    private static readonly string[] Dimensions =
    [
        "cashflow", "cash", "flow", "inflow", "outflow",
        "invoice", "bill", "customer", "billing",
        "reconcile", "reconciliation", "bank", "statement", "match",
        "tax", "taxes", "liability", "deduction", "bracket",
        "profit", "loss", "revenue", "expense", "income", "ebit",
        "report", "summary", "forecast", "budget",
        "balance", "account", "transaction", "payment", "vendor"
    ];

    public double[] Embed(string text)
    {
        var lower = text.ToLowerInvariant();
        return Dimensions
            .Select(d => lower.Contains(d)
                ? 1.0 + lower.Split(' ').Count(w => w.Contains(d)) * 0.15
                : 0.0)
            .ToArray();
    }
}
```

```csharp
// Cache/ISemanticCache.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;

public interface ISemanticCache
{
    CacheHit? Lookup(string userMessage);
    void Store(string userMessage, IntentResult intent, ExecutionPlan plan);
    IReadOnlyList<CacheEntry> Entries { get; }
}
```

```csharp
// Cache/CacheEntry.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;

public record CacheEntry(
    double[] Vector,
    IntentResult Intent,
    ExecutionPlan Plan,
    DateTime StoredAt
);

public record CacheHit(
    IntentResult Intent,
    ExecutionPlan Plan,
    double Score
);
```

```csharp
// Cache/InMemorySemanticCache.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;

public sealed class InMemorySemanticCache : ISemanticCache
{
    private readonly List<CacheEntry> _entries = [];
    private readonly EmbeddingSimulator _embedder;
    private const double SimilarityThreshold = 0.88;

    public IReadOnlyList<CacheEntry> Entries => _entries.AsReadOnly();

    public InMemorySemanticCache(EmbeddingSimulator embedder)
    {
        _embedder = embedder;
    }

    public CacheHit? Lookup(string userMessage)
    {
        if (_entries.Count == 0) return null;

        var queryVector = _embedder.Embed(userMessage);

        return _entries
            .Select(e => new { Entry = e, Score = CosineSimilarity(queryVector, e.Vector) })
            .Where(x => x.Score >= SimilarityThreshold)
            .OrderByDescending(x => x.Score)
            .Select(x => new CacheHit(x.Entry.Intent, x.Entry.Plan, x.Score))
            .FirstOrDefault();
    }

    public void Store(string userMessage, IntentResult intent, ExecutionPlan plan)
    {
        _entries.Add(new CacheEntry(
            Vector: _embedder.Embed(userMessage),
            Intent: intent,
            Plan: plan,
            StoredAt: DateTime.UtcNow
        ));
    }

    private static double CosineSimilarity(double[] a, double[] b)
    {
        double dot = a.Zip(b, (x, y) => x * y).Sum();
        double magA = Math.Sqrt(a.Sum(x => x * x));
        double magB = Math.Sqrt(b.Sum(x => x * x));
        return magA == 0 || magB == 0 ? 0 : dot / (magA * magB);
    }
}
```

---

### 9.4 Intent Classifier

```csharp
// Classification/IIntentClassifier.cs
namespace CffRoutingLayerDemo.Classification;

using CffRoutingLayerDemo.Core;

public interface IIntentClassifier
{
    IntentResult Classify(string userMessage);
}
```

```csharp
// Classification/RuleBasedClassifier.cs
namespace CffRoutingLayerDemo.Classification;

using CffRoutingLayerDemo.Core;

/// <summary>
/// Keyword-rule classifier. Matches the highest-scoring intent pattern.
/// Replace with an LLM-backed classifier in production.
/// </summary>
public sealed class RuleBasedClassifier : IIntentClassifier
{
    private record IntentRule(
        string Intent,
        string AgentId,
        string[] Keywords,
        bool RequiresConfirmation = false
    );

    private static readonly IntentRule[] Rules =
    [
        new("GenerateCashFlowReport", "BookkeepingAgent",
            ["cash flow", "cashflow", "inflow", "outflow", "money came in", "money went out", "cash report"]),

        new("CreateInvoice", "InvoiceAgent",
            ["create invoice", "make invoice", "new invoice", "bill a customer", "send invoice", "invoice for"],
            RequiresConfirmation: true),

        new("ReconcileAccount", "ReconciliationAgent",
            ["reconcile", "reconciliation", "bank statement", "match transactions", "match my bank"]),

        new("EstimateTaxLiability", "TaxAgent",
            ["tax liability", "tax estimate", "how much tax", "tax owe", "quarterly tax", "tax bill", "taxes"]),

        new("GenerateProfitLoss", "ReportingAgent",
            ["profit and loss", "profit & loss", "p&l", "income statement", "are we profitable",
             "how is the business", "net income", "ebit", "gross profit"])
    ];

    public IntentResult Classify(string userMessage)
    {
        var lower = userMessage.ToLowerInvariant();

        var best = Rules
            .Select(rule => new
            {
                Rule = rule,
                Score = (double)rule.Keywords.Count(kw => lower.Contains(kw)) / rule.Keywords.Length
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .FirstOrDefault();

        if (best is null)
            return new IntentResult("Unknown", 0.1, new(), "None", false);

        // Confidence scales with keyword match ratio, capped at 0.99
        double confidence = Math.Min(0.70 + best.Score * 0.29, 0.99);

        var entities = ExtractEntities(lower, best.Rule.Intent);

        return new IntentResult(
            best.Rule.Intent,
            confidence,
            entities,
            best.Rule.AgentId,
            best.Rule.RequiresConfirmation
        );
    }

    private static Dictionary<string, string> ExtractEntities(string lower, string intent) =>
        intent switch
        {
            "GenerateCashFlowReport" => new()
            {
                ["period"] = lower.Contains("last month") ? "last_30_days"
                           : lower.Contains("this month") ? "current_month"
                           : lower.Contains("ytd") ? "YTD"
                           : "last_30_days"
            },
            "EstimateTaxLiability" => new()
            {
                ["year"] = DateTime.UtcNow.Year.ToString(),
                ["entityType"] = lower.Contains("llc") ? "LLC"
                               : lower.Contains("s-corp") ? "S-Corp"
                               : "LLC"
            },
            "ReconcileAccount" => new()
            {
                ["accountId"] = lower.Contains("checking") ? "CHK-001"
                              : lower.Contains("savings") ? "SAV-001"
                              : "CHK-001",
                ["period"] = DateTime.UtcNow.ToString("MMMM")
            },
            "GenerateProfitLoss" => new()
            {
                ["period"] = lower.Contains("ytd") ? "YTD"
                           : lower.Contains("last year") ? "last_year"
                           : "YTD"
            },
            _ => new()
        };
}
```

---

### 9.5 Agent Registry

```csharp
// Registry/AgentManifest.cs
namespace CffRoutingLayerDemo.Registry;

public record AgentManifest(
    string AgentId,
    string DisplayName,
    string[] SupportedIntents,
    string[] RequiredEntities
);
```

```csharp
// Registry/AgentRegistry.cs
namespace CffRoutingLayerDemo.Registry;

using CffRoutingLayerDemo.Agents;

public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents;

    private AgentRegistry(Dictionary<string, IAgent> agents)
    {
        _agents = agents;
    }

    public static AgentRegistry BuildDefault() =>
        new(new Dictionary<string, IAgent>
        {
            ["BookkeepingAgent"]    = new BookkeepingAgent(),
            ["InvoiceAgent"]        = new InvoiceAgent(),
            ["ReconciliationAgent"] = new ReconciliationAgent(),
            ["TaxAgent"]            = new TaxAgent(),
            ["ReportingAgent"]      = new ReportingAgent()
        });

    public IAgent Resolve(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
            return agent;

        throw new InvalidOperationException($"No agent registered for id '{agentId}'.");
    }

    public IReadOnlyDictionary<string, IAgent> All => _agents;
}
```

---

### 9.6 Agents

```csharp
// Agents/IAgent.cs
namespace CffRoutingLayerDemo.Agents;

using CffRoutingLayerDemo.Core;

public interface IAgent
{
    string AgentId { get; }
    ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context);
}
```

```csharp
// Agents/BookkeepingAgent.cs
namespace CffRoutingLayerDemo.Agents;

using CffRoutingLayerDemo.Core;

public sealed class BookkeepingAgent : IAgent
{
    public string AgentId => "BookkeepingAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context)
    {
        var period = intent.Entities.GetValueOrDefault("period", "last_30_days");
        return new ExecutionPlan(
            PlanId: $"plan-cf-{context.RequestId[..8]}",
            Intent: intent.Intent,
            Steps:
            [
                new(1, "FetchTransactions",    new() { ["period"] = period },                     []),
                new(2, "ClassifyDirection",    new() { ["transactions"] = "$step1" },             [1]),
                new(3, "SumByCategory",        new() { ["classified"] = "$step2" },              [2]),
                new(4, "ComputeNetCashFlow",   new() { ["inflows"] = "$step3.in", ["outflows"] = "$step3.out" }, [3]),
                new(5, "RenderSummary",        new() { ["net"] = "$step4", ["breakdown"] = "$step3" }, [4])
            ]
        );
    }
}
```

```csharp
// Agents/InvoiceAgent.cs
namespace CffRoutingLayerDemo.Agents;

using CffRoutingLayerDemo.Core;

public sealed class InvoiceAgent : IAgent
{
    public string AgentId => "InvoiceAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context)
    {
        var customer = intent.Entities.GetValueOrDefault("customer", "Customer");
        return new ExecutionPlan(
            PlanId: $"plan-inv-{context.RequestId[..8]}",
            Intent: intent.Intent,
            Steps:
            [
                new(1, "LookupCustomer",       new() { ["name"] = customer },                   []),
                new(2, "ValidateLineItems",     new() { ["customer"] = "$step1" },              [1]),
                new(3, "CalculateTotals",       new() { ["items"] = "$step2", ["taxRate"] = "0.08" }, [2]),
                new(4, "GenerateInvoiceDoc",    new() { ["customer"] = "$step1", ["totals"] = "$step3" }, [1, 3]),
                new(5, "AssignInvoiceNumber",   new() { ["invoice"] = "$step4" },               [4]),
                new(6, "MarkAsDraft",           new() { ["invoiceId"] = "$step5" },             [5])
            ]
        );
    }
}
```

```csharp
// Agents/ReconciliationAgent.cs
namespace CffRoutingLayerDemo.Agents;

using CffRoutingLayerDemo.Core;

public sealed class ReconciliationAgent : IAgent
{
    public string AgentId => "ReconciliationAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context)
    {
        var accountId = intent.Entities.GetValueOrDefault("accountId", "CHK-001");
        var period    = intent.Entities.GetValueOrDefault("period", DateTime.UtcNow.ToString("MMMM"));
        return new ExecutionPlan(
            PlanId: $"plan-rec-{context.RequestId[..8]}",
            Intent: intent.Intent,
            Steps:
            [
                new(1, "LoadBookBalance",              new() { ["accountId"] = accountId },                   []),
                new(2, "LoadBankStatement",            new() { ["accountId"] = accountId, ["period"] = period }, []),
                new(3, "MatchTransactions",            new() { ["book"] = "$step1.txns", ["bank"] = "$step2.txns" }, [1, 2]),
                new(4, "IdentifyOutstanding",          new() { ["unmatched"] = "$step3.unmatched" },         [3]),
                new(5, "ComputeAdjustedBalance",       new() { ["bookBal"] = "$step1", ["outstanding"] = "$step4" }, [1, 4]),
                new(6, "GenerateReconciliationReport", new() { ["adjusted"] = "$step5", ["bankBal"] = "$step2" }, [2, 5])
            ]
        );
    }
}
```

```csharp
// Agents/TaxAgent.cs
namespace CffRoutingLayerDemo.Agents;

using CffRoutingLayerDemo.Core;

public sealed class TaxAgent : IAgent
{
    public string AgentId => "TaxAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context)
    {
        var year       = intent.Entities.GetValueOrDefault("year", DateTime.UtcNow.Year.ToString());
        var entityType = intent.Entities.GetValueOrDefault("entityType", "LLC");
        return new ExecutionPlan(
            PlanId: $"plan-tax-{context.RequestId[..8]}",
            Intent: intent.Intent,
            Steps:
            [
                new(1, "FetchYTDRevenue",          new() { ["year"] = year, ["entity"] = entityType }, []),
                new(2, "FetchDeductibleExpenses",  new() { ["year"] = year },                         []),
                new(3, "ComputeTaxableIncome",     new() { ["revenue"] = "$step1", ["deductions"] = "$step2" }, [1, 2]),
                new(4, "ApplyTaxBrackets",         new() { ["income"] = "$step3", ["entityType"] = entityType }, [3]),
                new(5, "DeductPriorPayments",      new() { ["brackets"] = "$step4" },                [4]),
                new(6, "SuggestQuarterlyPayment",  new() { ["remaining"] = "$step5", ["quartersLeft"] = "2" }, [5])
            ]
        );
    }
}
```

```csharp
// Agents/ReportingAgent.cs
namespace CffRoutingLayerDemo.Agents;

using CffRoutingLayerDemo.Core;

public sealed class ReportingAgent : IAgent
{
    public string AgentId => "ReportingAgent";

    public ExecutionPlan BuildPlan(IntentResult intent, RoutingContext context)
    {
        var period = intent.Entities.GetValueOrDefault("period", "YTD");
        return new ExecutionPlan(
            PlanId: $"plan-pnl-{context.RequestId[..8]}",
            Intent: intent.Intent,
            Steps:
            [
                new(1, "FetchRevenue",            new() { ["period"] = period },                   []),
                new(2, "FetchCOGS",               new() { ["period"] = period },                   []),
                new(3, "ComputeGrossProfit",       new() { ["revenue"] = "$step1", ["cogs"] = "$step2" }, [1, 2]),
                new(4, "FetchOperatingExpenses",   new() { ["period"] = period },                   []),
                new(5, "ComputeOperatingIncome",   new() { ["gross"] = "$step3", ["opex"] = "$step4" }, [3, 4]),
                new(6, "ApplyTaxAndInterest",      new() { ["ebit"] = "$step5" },                  [5]),
                new(7, "RenderPnL",               new() { ["net"] = "$step6", ["all"] = "true" }, [6])
            ]
        );
    }
}
```

---

### 9.7 Plan Executor

```csharp
// Plans/PlanStepResult.cs
namespace CffRoutingLayerDemo.Plans;

public record PlanStepResult(
    int StepId,
    string Action,
    string Output,
    bool Success,
    TimeSpan Duration
);
```

```csharp
// Plans/PlanExecutor.cs
namespace CffRoutingLayerDemo.Plans;

using CffRoutingLayerDemo.Core;

/// <summary>
/// Executes an ExecutionPlan step by step in dependency order.
/// Steps with no dependencies on in-flight steps could be parallelised;
/// kept sequential here for clarity in demo output.
/// </summary>
public sealed class PlanExecutor
{
    private static readonly Random Rng = new(42); // deterministic for demo

    public async Task<string> ExecuteAsync(ExecutionPlan plan, RoutingContext context)
    {
        var results = new Dictionary<int, PlanStepResult>();

        foreach (var step in plan.Steps.OrderBy(s => s.StepId))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Simulate async I/O (data fetch, computation)
            await Task.Delay(Rng.Next(5, 30));

            var output = SimulateStepOutput(step, plan.Intent, results);
            sw.Stop();

            results[step.StepId] = new PlanStepResult(
                step.StepId, step.Action, output, true, sw.Elapsed);
        }

        return BuildFinalReport(plan.Intent, results);
    }

    private static string SimulateStepOutput(
        PlanStep step,
        string intent,
        Dictionary<int, PlanStepResult> prior) =>
        step.Action switch
        {
            // Cash flow steps
            "FetchTransactions"    => "47 transactions loaded",
            "ClassifyDirection"    => "32 inflows ($148,320) / 15 outflows ($92,450)",
            "SumByCategory"        => "6 categories aggregated",
            "ComputeNetCashFlow"   => "NET: +$55,870.00",
            "RenderSummary"        => BuildCashFlowReport(),

            // Invoice steps
            "LookupCustomer"       => "CUST-00123 | Acme Corp | Net-30",
            "ValidateLineItems"    => "3 items validated",
            "CalculateTotals"      => "Subtotal: $5,000 | Tax: $400 | Total: $5,400",
            "GenerateInvoiceDoc"   => "Document generated",
            "AssignInvoiceNumber"  => "INV-2024-0042",
            "MarkAsDraft"          => "Status: DRAFT",

            // Reconciliation steps
            "LoadBookBalance"              => "Book balance: $42,100.00",
            "LoadBankStatement"            => "Statement balance: $41,850.00",
            "MatchTransactions"            => "91 matched / 3 unmatched",
            "IdentifyOutstanding"          => "Outstanding: 2 checks ($750) / 1 deposit ($500)",
            "ComputeAdjustedBalance"       => "Adjusted book: $41,850.00",
            "GenerateReconciliationReport" => BuildReconciliationReport(),

            // Tax steps
            "FetchYTDRevenue"         => "YTD Revenue: $320,000",
            "FetchDeductibleExpenses" => "Deductions: $87,500",
            "ComputeTaxableIncome"    => "Taxable income: $232,500",
            "ApplyTaxBrackets"        => "Estimated tax: $58,900",
            "DeductPriorPayments"     => "Remaining: $16,900",
            "SuggestQuarterlyPayment" => BuildTaxReport(),

            // P&L steps
            "FetchRevenue"          => "Revenue: $320,000",
            "FetchCOGS"             => "COGS: $145,000",
            "ComputeGrossProfit"    => "Gross profit: $175,000 (54.7%)",
            "FetchOperatingExpenses"=> "OpEx: $107,500",
            "ComputeOperatingIncome"=> "EBIT: $67,500",
            "ApplyTaxAndInterest"   => "Net income: $49,400",
            "RenderPnL"             => BuildPnLReport(),

            _ => $"{step.Action} completed"
        };

    private static string BuildCashFlowReport() => """
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
          CASH FLOW REPORT — Last 30 Days
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
          Total Inflows:    $148,320.00
          Total Outflows:    $92,450.00
          ────────────────────────────
          NET CASH FLOW:    +$55,870.00  ▲
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        """;

    private static string BuildReconciliationReport() => """
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
          RECONCILIATION — CHK-001
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
          Book Balance:         $42,100.00
          + Outstanding Deps:      $500.00
          - Outstanding Checks:   ($750.00)
          Adjusted Book Bal:    $41,850.00
          Bank Statement Bal:   $41,850.00
          ─────────────────────────────────
          DIFFERENCE:               $0.00  ✓ RECONCILED
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        """;

    private static string BuildTaxReport() => """
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
          TAX LIABILITY ESTIMATE — 2024
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
          YTD Revenue:        $320,000.00
          Deductions:         ($87,500.00)
          Taxable Income:     $232,500.00
          Estimated Tax:       $58,900.00
          Already Paid:       ($42,000.00)
          Remaining:           $16,900.00
          ────────────────────────────────
          Suggested Q3+Q4:     $8,450.00 /qtr
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        """;

    private static string BuildPnLReport() => """
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
          PROFIT & LOSS — YTD 2024
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
          Revenue
            Product Sales:      $280,000
            Services:            $40,000
          Total Revenue:        $320,000
          Cost of Goods Sold:  ($145,000)
          ──────────────────────────────
          GROSS PROFIT:         $175,000   (54.7%)

          Operating Expenses
            Salaries:            ($72,000)
            Rent & Utilities:    ($18,000)
            Marketing:           ($12,000)
            Software:             ($5,500)
          Total OpEx:           ($107,500)
          ──────────────────────────────
          OPERATING INCOME:      $67,500
          Interest & Other:       ($2,100)
          Est. Taxes:            ($16,000)
          ──────────────────────────────
          NET INCOME:            $49,400   ▲
        ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        """;

    private static string BuildFinalReport(string intent, Dictionary<int, PlanStepResult> results)
    {
        var lastStep = results.Values.OrderByDescending(r => r.StepId).First();
        return lastStep.Output;
    }
}
```

---

### 9.8 Routing Engine

```csharp
// Core/RoutingEngine.cs
namespace CffRoutingLayerDemo.Core;

using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;

public sealed class RoutingEngine
{
    private readonly ISemanticCache _cache;
    private readonly IIntentClassifier _classifier;
    private readonly AgentRegistry _registry;
    private readonly PlanExecutor _executor;

    private static readonly HashSet<string> DomainKeywords =
    [
        "cash", "flow", "invoice", "bill", "reconcile", "tax", "profit",
        "loss", "revenue", "expense", "income", "account", "payment",
        "transaction", "financial", "balance", "bank", "report", "budget",
        "forecast", "p&l", "bookkeeping", "vendor", "customer", "payroll"
    ];

    public RoutingEngine(
        ISemanticCache cache,
        IIntentClassifier classifier,
        AgentRegistry registry,
        PlanExecutor executor)
    {
        _cache      = cache;
        _classifier = classifier;
        _registry   = registry;
        _executor   = executor;
    }

    public async Task<string> HandleAsync(RoutingContext context)
    {
        // Stage 1 — Guardrails
        if (!IsInDomain(context.UserMessage))
            return "I can only assist with accounting and financial tasks. " +
                   "Try asking about cash flow, invoices, reconciliation, taxes, or P&L.";

        // Stage 2 — Semantic Cache Lookup
        var cached = _cache.Lookup(context.UserMessage);
        if (cached is not null)
        {
            Console.WriteLine(
                $"  ► Stage 2  Semantic cache lookup...        ✓ HIT  (similarity: {cached.Score:P0})");
            Console.WriteLine(
                $"             Intent reused: {cached.Intent.Intent}");
            Console.WriteLine(
                $"  ► Stage 6  Executing plan (from cache)...");
            return await _executor.ExecuteAsync(cached.Plan, context);
        }

        Console.WriteLine("  ► Stage 2  Semantic cache lookup...        ✗ MISS");

        // Stage 3 — Intent Classification
        var intent = _classifier.Classify(context.UserMessage);

        if (intent.Intent == "Unknown")
            return "I could not determine what you need. Please rephrase your accounting question.";

        Console.WriteLine(
            $"  ► Stage 3  Intent classification...        ✓ {intent.Intent} ({intent.Confidence:P0})");

        // Stage 4 — Agent Registry Lookup
        var agent = _registry.Resolve(intent.AgentId);
        Console.WriteLine(
            $"  ► Stage 4  Agent registry lookup...        ✓ {agent.AgentId}");

        // Stage 5 — Execution Plan Generation
        var plan = agent.BuildPlan(intent, context);
        Console.WriteLine(
            $"  ► Stage 5  Execution plan generated...     ✓ {plan.Steps.Count} steps");

        // Stage 6 — Execute
        Console.WriteLine("  ► Stage 6  Executing plan...");
        var result = await _executor.ExecuteAsync(plan, context);

        // Store in cache for future similar queries
        _cache.Store(context.UserMessage, intent, plan);
        Console.WriteLine("             [Stored in semantic cache]");

        return result;
    }

    private static bool IsInDomain(string message)
    {
        var lower = message.ToLowerInvariant();
        return DomainKeywords.Any(lower.Contains);
    }
}
```

---

### 9.9 Console Renderer & Entry Point

```csharp
// Demo/ConsoleRenderer.cs
namespace CffRoutingLayerDemo.Demo;

using CffRoutingLayerDemo.Cache;

public static class ConsoleRenderer
{
    public static void PrintBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("""
            ═══════════════════════════════════════════════════════════
              CFF Routing Layer Demo  —  Powered by GenOS Pattern
            ═══════════════════════════════════════════════════════════
              Type a financial question, or use a shortcut:
                scenarios  — list demo scenarios
                cache      — inspect semantic cache
                benchmark  — run a quick local benchmark
                clear      — clear the screen
                exit       — quit
            ───────────────────────────────────────────────────────────
            """);
        Console.ResetColor();
    }

    public static void PrintScenarios()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("""

            DEMO SCENARIOS
            ──────────────
            [A] "what is my cash flow this month"
            [B] "create an invoice for Acme Corp"
            [C] "reconcile my checking account"
            [D] "estimate my tax liability"
            [E] "show me profit and loss"

            CACHE TEST (run A first, then try):
            "show me cash flow report"        ← should HIT cache
            "how much money came in recently" ← should HIT cache
            """);
        Console.ResetColor();
    }

    public static void PrintCacheState(IReadOnlyList<CacheEntry> entries)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"\nSEMANTIC CACHE — {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");
        Console.WriteLine(new string('─', 60));
        if (entries.Count == 0)
        {
            Console.WriteLine("  (empty — ask a question first)");
        }
        else
        {
            foreach (var (entry, i) in entries.Select((e, i) => (e, i + 1)))
            {
                Console.WriteLine($"  [{i}] {entry.Intent.Intent,-30}  stored: {entry.StoredAt:HH:mm:ss}");
            }
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void PrintResult(string result)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine(result);
        Console.ResetColor();
    }

    public static void PrintPipelineHeader()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\n[PIPELINE]");
        Console.WriteLine("  ► Stage 1  Guardrail check...              ✓ In domain");
        Console.ResetColor();
    }

    public static void PrintError(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ✗ {msg}");
        Console.ResetColor();
    }
}
```

```csharp
// Demo/DemoScenarios.cs
namespace CffRoutingLayerDemo.Demo;

public static class DemoScenarios
{
    public static readonly (string Label, string Query)[] All =
    [
        ("Cash Flow Report",       "what is my cash flow this month"),
        ("Create Invoice",         "create an invoice for Acme Corp"),
        ("Reconcile Account",      "reconcile my checking account"),
        ("Tax Liability Estimate", "estimate my tax liability"),
        ("Profit & Loss Report",   "show me profit and loss")
    ];
}
```

```csharp
// Program.cs
using CffRoutingLayerDemo.Cache;
using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Demo;
using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Registry;
using System.Diagnostics;

var embedder    = new EmbeddingSimulator();
var cache       = new InMemorySemanticCache(embedder);
var classifier  = new RuleBasedClassifier();
var registry    = AgentRegistry.BuildDefault();
var executor    = new PlanExecutor();
var engine      = new RoutingEngine(cache, classifier, registry, executor);

ConsoleRenderer.PrintBanner();

while (true)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write("\nYou: ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(input)) continue;

    switch (input.ToLowerInvariant())
    {
        case "exit":
        case "quit":
            Console.WriteLine("Goodbye.");
            return;

        case "clear":
            Console.Clear();
            ConsoleRenderer.PrintBanner();
            continue;

        case "scenarios":
            ConsoleRenderer.PrintScenarios();
            continue;

        case "cache":
            ConsoleRenderer.PrintCacheState(cache.Entries);
            continue;

        case "benchmark":
            await RunQuickBenchmarkAsync(engine, cache);
            continue;
    }

    ConsoleRenderer.PrintPipelineHeader();

    var context = new RoutingContext(
        RequestId: Guid.NewGuid().ToString(),
        UserMessage: input,
        CompanyId: "DEMO-CO-001",
        Timestamp: DateTime.UtcNow
    );

    var sw = Stopwatch.StartNew();
    var result = await engine.HandleAsync(context);
    sw.Stop();

    ConsoleRenderer.PrintResult(result);

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"\n  ⏱  {sw.ElapsedMilliseconds} ms");
    Console.ResetColor();
    Console.WriteLine(new string('─', 60));
}

static async Task RunQuickBenchmarkAsync(RoutingEngine engine, InMemorySemanticCache cache)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\nQUICK BENCHMARK — 5 intents × 10 iterations");
    Console.WriteLine(new string('─', 60));
    Console.ResetColor();

    var scenarios = DemoScenarios.All;

    // Cold run
    foreach (var (label, query) in scenarios)
    {
        var ctx = new RoutingContext(Guid.NewGuid().ToString(), query, "BENCH", DateTime.UtcNow);
        await engine.HandleAsync(ctx); // warm cache
    }

    foreach (var (label, query) in scenarios)
    {
        var coldSw = Stopwatch.StartNew();
        var coldCtx = new RoutingContext(Guid.NewGuid().ToString(), query + " x", "BENCH", DateTime.UtcNow);
        await engine.HandleAsync(coldCtx);
        coldSw.Stop();

        var hotTimes = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            var ctx = new RoutingContext(Guid.NewGuid().ToString(), query, "BENCH", DateTime.UtcNow);
            var sw = Stopwatch.StartNew();
            await engine.HandleAsync(ctx);
            sw.Stop();
            hotTimes.Add(sw.ElapsedMilliseconds);
        }

        var avgHot = hotTimes.Average();
        Console.WriteLine(
            $"  {label,-28}  cold: {coldSw.ElapsedMilliseconds,4} ms  |  cache hit avg: {avgHot,5:F1} ms");
    }

    Console.WriteLine(new string('─', 60));
    Console.WriteLine($"  Cache entries: {cache.Entries.Count}");
    Console.WriteLine();
}
```
