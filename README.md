# CFF Routing Layer

A hybrid plan-routing pipeline for a .NET 9 financial accounting assistant. Given a free-text user query, it selects and executes the correct pre-saved execution plan from a library of 114+ plans using a combination of LLM intent extraction, Titan V2 embedding similarity, structured metadata scoring, and LLM-based re-ranking.

---

## How It Works

```
User query
    │
    ▼
Phase 0 — Query Understanding
    LlmQueryParser (Bedrock Claude Haiku, ~800 ms timeout)
    → structured QueryIntent: domain, action, slots, temporal scope, reasoning
    Falls back to RuleBasedQueryParser if LLM is unavailable
    │
    ▼
Phase 1 — Hybrid Plan Retrieval  [USE_HYBRID_RETRIEVAL=true]
    HybridPlanRetriever combines:
      55%  Titan Embeddings V2 cosine similarity (pre-built index)
      45%  Metadata score: domain (0.35) + subdomain (0.25) + action (0.25) + temporal (0.15)
    QueryNormalizer applied to query and plan sample queries before embedding
    Outputs: top-10 CandidateResults ranked by combined AlignmentScore
    │
    ▼
Phase 2 — Plan Re-ranking
    LlmPlanJudge (Bedrock Claude Haiku, batch pointwise scoring)
    Falls back to FeatureAlignmentRanker when LLM unavailable:
      base AlignmentScore
      + output-field token overlap × 0.08
      + descriptionRelevance × 0.20   (Levenshtein + keyword-coherence guard)
      − entity mismatch penalty × 0.15 each
    │
    ▼
Phase 3 — Plan Validation
    SlotCoverageValidator + SchemaCompatibilityValidator
    Falls back to next-best candidate if validation fails (up to 3 attempts)
    │
    ▼
Routing decision → Execute / ConfirmAndExecute / Ambiguous / Clarify / Rejected
    │
    ▼
PlanExecutor → step-by-step execution against CompanyDataStore
```

---

## Project Structure

```
CffRoutingLayer.sln
│
├── CffRoutingLayerDemo/          # Main application
│   ├── Program.cs                # Interactive REPL loop (Success / ConfirmAndExecute / Ambiguous / Clarify / Rejected handlers)
│   ├── Core/
│   │   └── PlanRoutingPipeline.cs    # Orchestrates Phases 0–3
│   ├── Understanding/
│   │   ├── RuleBasedQueryParser.cs   # Deterministic parser (Phase 0 fallback)
│   │   ├── LlmQueryParser.cs         # Bedrock Claude intent extraction (Phase 0 primary)
│   │   ├── QueryNormalizer.cs        # Temporal token normalization — applied symmetrically to queries and plan sample queries before embedding (entity names intentionally left as-is)
│   │   ├── ActionClassifier.cs       # Verb → DomainAction enum
│   │   ├── DomainDictionary.cs       # Keyword + phrase → domain/subdomain
│   │   ├── EntityExtractor.cs        # Slot extraction
│   │   └── TemporalResolver.cs       # Relative date → TemporalScope
│   ├── Index/
│   │   ├── PlanIndex.cs              # Structural multi-dimensional index (used when USE_HYBRID_RETRIEVAL=false)
│   │   ├── PlanFeatureExtractor.cs   # Extracts PlanFeatureVector at load time; InferPrimaryAction checks intent name prefix first
│   │   └── Filters/                  # DomainFilter, ActionFilter, TemporalFilter, EntityFilter
│   ├── Retrieval/
│   │   ├── HybridPlanRetriever.cs    # Phase 1: embedding (55%) + metadata (45%) hybrid retrieval
│   │   └── PlanEmbeddingIndex.cs     # Pre-built Titan V2 embeddings for all plans (built on startup)
│   ├── Ranking/
│   │   ├── FeatureAlignmentRanker.cs # Deterministic Phase 2 fallback scorer
│   │   ├── LlmPlanJudge.cs           # Bedrock Claude Phase 2 re-ranker (primary)
│   │   └── ScoringWeights.cs         # Tunable weights
│   ├── Validation/
│   │   └── CompositeValidator.cs     # Slot coverage + schema compatibility
│   ├── Plans/
│   │   ├── PlanLoader.cs             # Deserialises YAML → PlanDefinition
│   │   └── PlanExecutor.cs           # Runs plan steps against CompanyDataStore
│   ├── Bedrock/
│   │   ├── BedrockLlmHelper.cs       # Shared Converse API wrapper
│   │   └── BedrockStreamingConversation.cs  # Streaming fallback for Rejected queries
│   ├── plans/                        # 114+ YAML plan files
│   │   ├── cashflow.yaml             # cashflow-001         GenerateCashFlowReport
│   │   ├── cashrunway.yaml           # runway-001           ForecastCashRunway
│   │   ├── invoice.yaml              # invoice-001          CreateInvoice
│   │   ├── invoice-paid-total.yaml   # invoice-paid-total-001  GetTotalPaidInvoiceAmount
│   │   ├── listinvoices.yaml         # listinvoices-001     ListInvoices
│   │   ├── profitaudit.yaml          # audit-001            AnalyzeProfitAnomaly
│   │   ├── profitloss.yaml           # pl-001               GenerateProfitLoss
│   │   ├── reconcile.yaml            # reconcile-001        ReconcileAccount
│   │   ├── tax.yaml                  # tax-001              EstimateTaxLiability
│   │   ├── taxoptimization.yaml      # taxopt-001           OptimizeTaxDeductions
│   │   └── response_001..109.yaml    # CFF real plans (real-001 … real-109)
│   └── CompanyData/
│       └── CompanyDataStore.cs       # In-memory demo financial data (DEMO-001)
│
├── CffPlanTransformer/           # CLI tool: converts raw CFF API responses → YAML plans
│   └── Transformer/PlanTransformer.cs
│
├── CffRoutingLayerDemo.Tests/    # Unit tests
├── CffRoutingLayerDemo.Benchmarks/
│
└── cff-real-plans/               # Raw CFF plan API responses (source for transformer)
    └── response_001..109.txt
```

---

## Routing Decision Flow

### Confidence thresholds

| Condition | Status | Action |
|---|---|---|
| Phase 0 confidence < 0.28 (`MinPhase0Confidence`) | `Rejected` | Streaming LLM fallback (Bedrock) — no retrieval attempted |
| Phase 1 top score < 0.45 (`MinPhase1Score`) | `NoPlanFound` | No plan confident enough to rank |
| Top score < 0.35 (`RejectThreshold`) | `Rejected` | "No plan found" message |
| Top score ≥ 0.35 and < 0.60 | `Clarify` | Ask user to rephrase or add detail |
| Top score ≥ 0.60, gap < `AmbiguityGap` (0.08) | `Ambiguous` | Present top candidates, ask user to choose |
| Top score ≥ 0.60 and < 0.75, gap ≥ 0.08 | `ConfirmAndExecute` | Show plan, ask "yes/no" before executing |
| Top score ≥ 0.75 and gap ≥ 0.08 | `Success` | Execute plan immediately |
| Validation fails on winner | — | Try next candidate (up to 3 fallbacks) |

### Phase 0 — LLM intent extraction (LlmQueryParser)

Bedrock Claude Haiku produces a structured JSON intent with:

- `domain`, `subdomain`, `action`, `actionConfidence`
- `slots`: extracted entities with confidence scores
- `temporal`: `start`/`end` dates (ISO 8601 or null), resolved from expressions like "this quarter", "FY2025", "last month"
- `reasoning`: brief explanation of the extraction choices

A rich date context block is injected into every prompt:

```
Today            : 2026-06-03
Current month    : June 2026 (2026-06-01 – 2026-06-30)
Current quarter  : Q2 2026 (2026-04-01 – 2026-06-30)
Current FY       : FY2026 (2026-01-01 – 2026-12-31)
Last month       : May 2026 (2026-05-01 – 2026-05-31)
Last quarter     : Q1 2026
Last FY          : FY2025 (2025-01-01 – 2025-12-31)
```

LRU cache (capacity 500) and 800 ms timeout; falls back to `RuleBasedQueryParser` on timeout/error.

### Phase 1 — Hybrid retrieval metadata weights

When `USE_HYBRID_RETRIEVAL=true` the `HybridPlanRetriever` combines embedding and metadata scores:

```
Combined = embedding_cosine × 0.55 + metadata × 0.45

Metadata breakdown:
  Domain match      0.35
  SubDomain match   0.25
  Action match      0.25
  Temporal match    0.15
```

`InferPrimaryAction` (PlanFeatureExtractor) determines each plan's action by checking the **intent name prefix first** (e.g. `List*` → `List`, `Identify*` → `List`, `Generate*` → `Compute`) before falling back to the last step's action string. This prevents the common false `Compute` inference from `compute-*` step names on plans whose purpose is retrieval.

`QueryNormalizer` normalises **temporal** tokens symmetrically on both sides of the cosine comparison (applied to both plan sample queries at build time and incoming queries at retrieval time):

- Fiscal years → `${fiscalYear}` (e.g. `FY2024`)
- Month names → `${month}` (e.g. `March`)
- Calendar quarters → `${quarter}` (e.g. `Q2`)
- 4-digit years → `${year}` (e.g. `2024`)
- ISO / partial dates → `${date}` (e.g. `2024-01-01`)

Named entity values (customer names, invoice IDs, etc.) are intentionally **not** normalized — replacing them with generic placeholders collapses distinct semantic meaning and degrades embedding similarity.

### Phase 2 — Re-ranking score formula (FeatureAlignmentRanker fallback)

```
score = AlignmentScore (Phase 1)
      + outputFieldOverlap    × 0.08    (bonus; up to +0.08)
      + descriptionRelevance  × 0.20    (bonus; up to +0.20)
      − entityMismatchCount   × 0.15    (penalty per unmet entity)
```

`descriptionRelevance` is computed as follows:
- Best normalised Levenshtein edit similarity between the query and any of the plan's `sampleQueries`
- Falls back to corpus keyword overlap against `understanding` text when no sample queries exist
- **Keyword-coherence guard**: result is scaled by `0.25 + 0.75 × keywordCoverage`, where `keywordCoverage` is the fraction of the query's content words (len > 4, non-stop-word) present in the plan's combined `understanding` + `sampleQueries` text. This prevents a plan with coincidental surface similarity from outscoring a semantically correct plan.

`IsAmbiguous` is true when `top1Score − top2Score < AmbiguityGap (0.08)`.

### Multi-intent routing

When Phase 0 returns a `QueryIntent` with `SubIntents.Count > 0` (the LLM detected a compound query), `PlanRoutingPipeline` routes each sub-intent independently through Phases 1–3:

- The root intent describes the **first** task; `SubIntents` contains the additional tasks.
- Each sub-intent runs the full Phase 1 → Phase 2 → Phase 3 pipeline independently.
- Sub-intents that return `Success` **or** `ConfirmAndExecute` are collected — the per-sub-intent confirmation gate is suppressed in batch context since the plan has already passed Phase 3 validation.
- If at least one sub-intent routes successfully the decision is `MultiSuccess`; failed sub-intents are logged as notes but do not block the successful ones.
- If every sub-intent fails routing the decision is `NoPlanFound`.

Example console output for a compound query:

```
  ► Multi-intent: 2 tasks detected
  ┌ Sub-intent: show me the upcoming sales payments
  ► Phase 1  Hybrid retrieval...             ✓ 10 candidates
  ► Phase 2  Fine ranking...                 ✓ 10 candidates
  ► Phase 3  Validation (real-042)...        ✓ Pass
  ┌ Sub-intent: list the customer with more balance
  ► Phase 1  Hybrid retrieval...             ✓ 10 candidates
  ► Phase 2  Fine ranking...                 ✓ 10 candidates
  ► Phase 3  Validation (real-031)...        ✓ Pass
  ► Execute  Sub-task 1/2  Running plan real-042...
  ► Execute  Sub-task 2/2  Running plan real-031...
```

### Plan YAML — required slots

`RequiredInputSlots` is derived exclusively from `defaultEntities` keys. Step-level `parameters:` values are treated as static implementation config and are never required from the user. Plans declare required dynamic inputs via `defaultEntities` (with or without a default value).

---

## Plan YAML Format

```yaml
planId: tax-001
intent: EstimateTaxLiability
agentId: FinancialAgent
domain: finance
displayName: Financial Agent
capabilities:
  - tax-estimation
understanding: "Estimate the tax liability for a given year and entity type based on taxable income after deductions"
expectedData:
  summary:
    - TaxLiability
    - EffectiveRate
    - TaxableIncome
defaultEntities:
  year: "2024"
  entityType: S-Corp
  companyId: DEMO-001
sampleQueries:
  - "How much tax do we owe for 2024?"
  - "What's our tax situation for this year?"
  - "Calculate our tax liability for this year"
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
| `domain` | 1 metadata scoring | Domain match (weight 0.35) |
| `understanding` | 1 embedding text | Embedded as part of plan text |
| `description` | 1 embedding text | Stripped Approach field — adds computation vocabulary (e.g. "aggregate by customer") not always present in `understanding`; embedded between `understanding` and `sampleQueries` |
| `sampleQueries` | 1 embedding + 2 re-ranking | Embedded (normalised); Levenshtein tiebreaker |
| `defaultEntities` | 3 slot coverage | Fills required slots; keys define required inputs |
| `steps[].outputFields` | 2 outputFieldOverlap + 3 validation | Schema compatibility |
| `capabilities` | 1 metadata | Capability/tool matching |

---

## Tech Stack

| Component | Technology |
|---|---|
| Runtime | .NET 9 / C# |
| LLM (intent + ranking) | Amazon Bedrock — `anthropic.claude-3-haiku-20240307-v1:0` |
| Embeddings | Amazon Bedrock — `amazon.titan-embed-text-v2:0` |
| AWS SDK | `AWSSDK.BedrockRuntime 4.0.20` |
| YAML parsing | `YamlDotNet 18.0.0` |
| Console UI | `Spectre.Console 0.55.2` |
| Config | `DotNetEnv 3.2.0` (reads `.env` file) |

---

## Setup

### Prerequisites

- .NET 9 SDK
- AWS credentials configured (`~/.aws/credentials` or environment variables)
- AWS Bedrock access to Claude 3 Haiku in `us-east-1`

### Environment

Create a `.env` file in the repo root (or set environment variables):

```
AWS_REGION=us-east-1
BEDROCK_LLM_MODEL_ID=anthropic.claude-3-haiku-20240307-v1:0
BEDROCK_STREAM_MODEL_ID=anthropic.claude-3-haiku-20240307-v1:0
EMBEDDING_MODEL_ID=amazon.titan-embed-text-v2:0
USE_HYBRID_RETRIEVAL=true
USE_BEDROCK_RAG=false
```

`USE_HYBRID_RETRIEVAL=true` (recommended): activates `HybridPlanRetriever` with Titan V2 embedding index built on startup. Set to `false` to use the structural `PlanIndex` with hard filters instead.

### Run

```bash
cd CffRoutingLayerDemo
dotnet run
```

The REPL accepts natural-language queries. Type `exit` or `quit` to stop.

### Build only

```bash
dotnet build CffRoutingLayerDemo/CffRoutingLayerDemo.csproj
```

### Transform raw CFF plans → YAML

```bash
cd CffPlanTransformer
dotnet run
```

Reads `cff-real-plans/response_*.txt` → writes `CffRoutingLayerDemo/plans/response_*.yaml`.

---

## Example Queries

```
show me my invoices this month
run a P&L report
What's our net income for 2024?
Calculate what percentage of total sales invoices were paid after their due date
What's our tax situation for this year?
Forecast our cash runway for the next 6 months
Invoice that customer again for $2,200
percentage of invoices paid late
reconcile my accounts for Q1
calculate cashflow for company Pepe
total amount of paid invoices this quarter
```

---

## Pipeline Console Output

```
▶ percentage of invoices paid late
  Pipeline stages:
  ► Phase 0  Query understanding...          ✓ Compute/finance (88 %)
  ► Phase 1  Hybrid retrieval...             ✓ 10 candidates
             • real-059 (91 %)
             • real-062 (84 %)
             • real-041 (79 %)
             ...
  ► Phase 2  Fine ranking...                 ✓ 10 candidates
             • real-059 (97 %)
             • real-062 (78 %)
             ...
  ► Phase 3  Validation (real-059)...        ✓ Pass
  Completed in 1 200 ms
  ► Execute  Running plan real-059...
```

Phase 0 also prints detected slots and temporal scope when present:

```
  ► Phase 0  Intent extraction...            ✓ List/finance (72 %)
             temporal: this month  →  2026-06-01 – 2026-06-30
             slot: companyId = "DEMO-001" (80 %)
             reasoning: "user asked to list invoices; 'this month' resolved to current calendar month"
```

When the top plan needs confirmation (`ConfirmAndExecute`):

```
  ● Routing status: ConfirmAndExecute (76 %)
    Top plan: cashflow-001 — GenerateCashFlowReport
    Proceed? [yes/no]:
```
