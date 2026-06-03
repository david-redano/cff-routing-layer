# CFF Routing Layer

A deterministic, four-layer plan-routing pipeline for a .NET 9 financial accounting assistant. Given a free-text user query, it selects and executes the correct pre-saved execution plan from a library of 114 plans — without relying on embedding similarity or semantic search.

---

## How It Works

```
User query
    │
    ▼
Layer 1 — Query Understanding
    Rule-based parser → LlmQueryParser (Bedrock fallback)
    Produces a structured QueryIntent: domain, action, slots, temporal scope
    │
    ▼
Layer 2 — Plan Retrieval
    In-memory multi-dimensional index with 4 progressive filters:
    DomainFilter → ActionFilter → TemporalFilter → EntityFilter
    Outputs: top-5 CandidateResults with AlignmentScore
    │
    ▼
Layer 3 — Plan Ranking
    FeatureAlignmentRanker scores candidates on:
      75% base feature alignment (domain, subdomain, action, temporal, entities, output format)
      10% output-field token overlap with query
      15% understanding/sampleQuery text overlap with query (primary tiebreaker)
    Ambiguous results (gap < 0.10 and top score < 0.65) → NoPlanFound
    │
    ▼
Layer 4 — Plan Validation
    SlotCoverageValidator + SchemaCompatibilityValidator
    Falls back to next-best candidate if validation fails
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
│   ├── Program.cs                # Interactive REPL loop
│   ├── Core/
│   │   └── PlanRoutingPipeline.cs    # Orchestrates Layers 1–4
│   ├── Understanding/
│   │   ├── RuleBasedQueryParser.cs   # Deterministic parser (primary)
│   │   ├── LlmQueryParser.cs         # Bedrock Claude fallback
│   │   ├── ActionClassifier.cs       # Verb → DomainAction enum
│   │   ├── DomainDictionary.cs       # Keyword + phrase → domain/subdomain
│   │   ├── EntityExtractor.cs        # Slot extraction
│   │   └── TemporalResolver.cs       # Relative date → TemporalScope
│   ├── Index/
│   │   ├── PlanIndex.cs              # Multi-dimensional retrieval index
│   │   ├── PlanFeatureExtractor.cs   # Extracts PlanFeatureVector at load time
│   │   └── Filters/                  # DomainFilter, ActionFilter, TemporalFilter, EntityFilter
│   ├── Ranking/
│   │   ├── FeatureAlignmentRanker.cs # Deterministic scorer (primary)
│   │   ├── LlmPlanJudge.cs           # Bedrock Claude judge (optional)
│   │   └── ScoringWeights.cs         # Tunable weights (must sum to 1.0)
│   ├── Validation/
│   │   └── CompositeValidator.cs     # Slot coverage + schema compatibility
│   ├── Plans/
│   │   ├── PlanLoader.cs             # Deserialises YAML → PlanDefinition
│   │   └── PlanExecutor.cs           # Runs plan steps against CompanyDataStore
│   ├── Bedrock/
│   │   ├── BedrockLlmHelper.cs       # Shared Converse API wrapper
│   │   └── BedrockStreamingConversation.cs  # Streaming fallback for Rejected queries
│   ├── plans/                        # 114 YAML plan files
│   │   ├── cashflow.yaml             # cashflow-001     GenerateCashFlowReport
│   │   ├── cashrunway.yaml           # runway-001       ForecastCashRunway
│   │   ├── invoice.yaml              # invoice-001      CreateInvoice
│   │   ├── listinvoices.yaml         # listinvoices-001 ListInvoices
│   │   ├── profitaudit.yaml          # audit-001        AnalyzeProfitAnomaly
│   │   ├── profitloss.yaml           # pl-001           GenerateProfitLoss
│   │   ├── reconcile.yaml            # reconcile-001    ReconcileAccount
│   │   ├── tax.yaml                  # tax-001          EstimateTaxLiability
│   │   ├── taxoptimization.yaml      # taxopt-001       OptimizeTaxDeductions
│   │   └── response_001..105.yaml    # 105 CFF real plans (real-001 … real-105)
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
    └── response_001..105.txt
```

---

## Routing Decision Flow

### Confidence thresholds

| Condition | Status | Action |
|---|---|---|
| Overall confidence < 0.25 | `Rejected` | Streaming LLM fallback (Bedrock) |
| Top score < 0.35 | `NoPlanFound` | "I cannot help with that" message |
| `IsAmbiguous` AND top score < 0.65 | `NoPlanFound` | "I cannot help with that" message |
| Single clear winner | `Success` | Execute plan |
| Validation fails on winner | — | Try next candidate (up to 3 fallbacks) |

### Layer 1 — Domain & Action detection

`DomainDictionary` uses a two-tier keyword map:

- **Phrase map** (weight 2, checked first): `"sales invoice"` → `(sales, invoicing)`, `"sales order"` → `(sales, orders)`, `"purchase order"` → `(finance, invoicing)`
- **Keyword map** (weight 1): `"invoice"` → `(finance, invoicing)`, `"tax"` → `(finance, tax)`, `"sales"` → `(sales, orders)`, `"income"/"earnings"` → `(finance, reporting)`, etc.

`ActionClassifier` maps verb/phrase patterns to `DomainAction`:

| Action | Triggers |
|---|---|
| `List` | show, list, display, get, fetch, find, retrieve |
| `Create` | create, make, send, issue, build, invoice that, invoice this, invoice them |
| `Compute` | calculate, compute, estimate, generate, run, percentage, how much, average, sum |
| `Compare` | compare, reconcile, match, versus |
| `Forecast` | forecast, predict, project, runway |
| `Audit` | audit, check, verify, anomaly, detect |

When `ActionConfidence ≤ 0.35` (no clear verb found), the `ActionFilter` is skipped — all action types remain as candidates.  
When `DomainConfidence < 0.40`, the `DomainFilter` is skipped.

### Layer 2 — Scoring weights

```
Domain          0.25
Action          0.20
SubDomain       0.15
EntityCoverage  0.15
Temporal        0.10
Discriminator   0.10
OutputFormat    0.05
```

Special cases:
- Domain mismatch when `DomainConfidence < 0.40` → **0.4× partial credit** (plan stays in pool instead of being buried)
- Action mismatch when `ActionConfidence ≤ 0.35` → **0.5× partial credit** (action was a default guess)

### Layer 3 — Final score formula

```
score = baseL2Score × 0.75
      + outputFieldOverlap × 0.10
      + descriptionOverlap × 0.15
```

- **`outputFieldOverlap`**: fraction of query tokens (> 3 chars) found in the plan's `expectedData.summary` field names
- **`descriptionOverlap`**: fraction of query tokens (> 3 chars) found in the plan's `understanding` text + `sampleQueries`. This is the primary tiebreaker between structurally identical candidates.

`IsAmbiguous` is true when `top1Score − top2Score < 0.10`. An ambiguous result with `topScore < 0.65` returns `NoPlanFound` rather than guessing.

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

| Field | Layer | Purpose |
|---|---|---|
| `domain` | 2 DomainFilter + scoring | Hard domain match |
| `capabilities` | 2 EntityFilter | Tool/capability matching |
| `understanding` | 3 descriptionOverlap | Tiebreaker scoring |
| `sampleQueries` | 3 descriptionOverlap | Tiebreaker scoring |
| `defaultEntities` | 4 slot coverage | Fills missing required slots |
| `steps[].outputFields` | 3 outputFieldOverlap + 4 validation | Schema compatibility |

---

## Tech Stack

| Component | Technology |
|---|---|
| Runtime | .NET 9 / C# |
| LLM | Amazon Bedrock — `anthropic.claude-3-haiku-20240307-v1:0` |
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
USE_BEDROCK_RAG=false
```

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
```

---

## Pipeline Console Output

```
▶ percentage of invoices paid late
  Pipeline stages:
  ► Layer 1  Query understanding...          ✓ Compute/finance (55 %)
  ► Layer 2  Plan retrieval...               ✓ 5 candidates
             • real-059 (100 %)
             • real-062 (100 %)
             • real-041 (100 %)
             • real-003 (100 %)
             • invoice-001 (100 %)
  ► Layer 3  Plan ranking...                 ✓ 5 candidates
             • real-059 (94 %)
             • real-062 (83 %)
             ...
  ► Layer 4  Validation (real-059)...        ✓ Pass
  Completed in 42 ms FRESH
  ► Execute  Running plan real-059...
```

Layer 1 also prints detected slots and temporal scope when present:

```
  ► Layer 1  Query understanding...          ✓ List/finance (35 %)
             temporal: this month
             slot: companyId = "DEMO-001" (80 %)
```
