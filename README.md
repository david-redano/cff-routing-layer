# CFF Routing Layer

An intent-driven **agent routing layer** for accounting and finance workflows, built on .NET 9 and AWS Bedrock. Natural-language queries are normalised, classified, and dispatched to the appropriate agent — each producing a structured, multi-step execution plan derived entirely from YAML. Execution steps retrieve real financial records from `CompanyDataStore` and, when enabled, generate LLM-narrated reports via a RAG loop (Retrieve → Augment → Generate). No C# change is needed to add a new intent or agent.

---

## Table of Contents

- [Overview](#overview)
- [Solution Structure](#solution-structure)
- [Seven-Stage Routing Pipeline](#seven-stage-routing-pipeline)
- [Component Reference](#component-reference)
  - [Intent Rewriter](#intent-rewriter)
  - [Semantic Cache](#semantic-cache)
  - [Intent Classifier](#intent-classifier)
  - [Agent Registry](#agent-registry)
  - [Execution Plans](#execution-plans)
  - [Company Data Store](#company-data-store)
  - [RAG Summarizer](#rag-summarizer)
  - [Conversation History](#conversation-history)
- [YAML Plan Schema](#yaml-plan-schema)
- [Registered Agents](#registered-agents)
- [AWS Bedrock Integration](#aws-bedrock-integration)
- [Feature Flags](#feature-flags)
- [Configuration Reference](#configuration-reference)
- [Getting Started](#getting-started)
- [Running Tests](#running-tests)
- [Running Benchmarks](#running-benchmarks)

---

## Overview

```
User message
     │
     ▼
┌─────────────────────────────────────────────────────────┐
│                   RoutingEngine                         │
│                                                         │
│  Stage 1  Guardrails (domain keyword check)             │
│  Stage 2  Intent Rewriter  (LLM or regex)               │
│  Stage 3  Semantic Cache   (cosine similarity lookup)   │
│  Stage 4  Intent Classifier (rule-based or LLM)         │
│  Stage 4b Capability Fallback (if Unknown)              │
│  Stage 5  Agent Registry Lookup                         │
│  Stage 6  Execution Plan Generation (from YAML)         │
│  Stage 7  Plan Execution + Cache Store                  │
│           ↳ CompanyDataStore (retrieve)                 │
│           ↳ RagSummarizer   (generate, optional)        │
└─────────────────────────────────────────────────────────┘
     │
     ▼
Structured result string
```

Every component has two modes controlled by feature flags: a **local mode** (no AWS credentials needed) and a **live Bedrock mode** for production quality.

---

## Solution Structure

```
cff-routing-layer/
├── CffRoutingLayer.sln
│
├── CffRoutingLayerDemo/                  # Main application
│   ├── Program.cs                        # Startup, REPL
│   ├── .env / .env.template              # Runtime configuration
│   │
│   ├── Bedrock/
│   │   ├── BedrockEmbeddingProvider.cs   # Titan Embeddings V2 + local disk cache
│   │   ├── BedrockLlmClassifier.cs       # Claude Haiku intent classifier
│   │   ├── BedrockSemanticCache.cs       # Bedrock-backed semantic cache
│   │   ├── BedrockStreamingConversation.cs  # Streaming fallback for Unknown intents
│   │   ├── CanonicalPhrases.cs           # Intent ↔ AgentId map (loaded from plans)
│   │   ├── LlmIntentRewriter.cs          # Claude Haiku rewriter with runtime context
│   │   └── RagSummarizer.cs              # Claude Haiku RAG report generation
│   │
│   ├── Cache/
│   │   ├── CacheEntry.cs                 # Cache record (vector, intent, plan, timestamp)
│   │   ├── CacheWarmer.cs                # Pre-warms cache from plan sample queries
│   │   ├── EmbeddingSimulator.cs         # Local keyword-vector simulator (no AWS)
│   │   ├── ISemanticCache.cs             # Cache interface
│   │   └── InMemorySemanticCache.cs      # In-memory cosine similarity cache
│   │
│   ├── Classification/
│   │   ├── IIntentClassifier.cs          # Classifier interface
│   │   └── RuleBasedClassifier.cs        # Keyword/regex classifier (no AWS)
│   │
│   ├── Config/
│   │   └── AppConfig.cs                  # Environment-variable configuration
│   │
│   ├── Conversation/
│   │   ├── ConversationHistory.cs        # Multi-turn history (provided to rewriter)
│   │   └── ConversationTurn.cs           # Single-turn record
│   │
│   ├── Core/
│   │   ├── ExecutionPlan.cs              # PlanStep + ExecutionPlan records
│   │   ├── IntentResult.cs               # Classification output record
│   │   ├── RoutingContext.cs             # Per-request context (companyId, timestamp, …)
│   │   └── RoutingEngine.cs              # Orchestrates all 7 stages
│   │
│   ├── Demo/
│   │   ├── ConsoleRenderer.cs            # Spectre.Console UI
│   │   └── DemoScenarios.cs             # Canned demo query sequences
│   │
│   ├── Normalization/
│   │   ├── IntentRewriter.cs             # Regex rewriter (local mode)
│   │   └── RewrittenIntent.cs            # Rewriter output record
│   │
│   ├── CompanyData/
│   │   ├── FinancialRecord.cs            # Income/Expense record (record type)
│   │   └── CompanyDataStore.cs           # In-memory ledger (40 seeded records)
│   │
│   ├── Plans/
│   │   ├── PlanDefinition.cs             # YAML model (includes YamlPlanStep)
│   │   ├── PlanExecutor.cs               # Data-driven step execution + RAG
│   │   ├── PlanLoader.cs                 # YamlDotNet deserialization
│   │   └── PlanStepResult.cs             # Per-step result record
│   │
│   ├── Registry/
│   │   ├── AgentManifest.cs              # Agent metadata + plan factory
│   │   └── AgentRegistry.cs             # Dynamic registry (loaded from YAML)
│   │
│   └── plans/                            # Agent plan definitions (YAML)
│       ├── cashflow.yaml
│       ├── cashrunway.yaml
│       ├── invoice.yaml
│       ├── profitaudit.yaml
│       ├── profitloss.yaml
│       ├── reconcile.yaml
│       ├── tax.yaml
│       └── taxoptimization.yaml
│
├── CffRoutingLayerDemo.Tests/            # xUnit test suite (20 tests)
│   └── UnitTest1.cs
│
└── CffRoutingLayerDemo.Benchmarks/      # BenchmarkDotNet
    ├── CacheWarmerBenchmarks.cs
    ├── RewriterBenchmarks.cs
    └── RoutingPipelineBenchmarks.cs
```

---

## Seven-Stage Routing Pipeline

`RoutingEngine.HandleAsync()` runs every request through these stages in order:

### Stage 1 — Guardrails

A fast keyword check against a hardcoded `DomainKeywords` set (`cash`, `invoice`, `tax`, `reconcile`, `profit`, `runway`, …). Messages with no domain signal are rejected immediately with a polite refusal.

**Exception:** when conversation history exists, the guardrail is skipped. Coreference turns ("do the same for Q2") contain no domain keywords — the rewriter handles context resolution.

### Stage 2 — Intent Rewriter

Normalises the raw user message before any further processing:

| Mode | Component | Behaviour |
|------|-----------|-----------|
| Local | `RegexIntentRewriter` | Regex substitutions: currency spellings, date aliases, synonym normalisation |
| Live  | `LlmIntentRewriter` | Claude Haiku call with two system blocks: static rules + dynamic runtime context (current date, fiscal quarter, previous periods) |

The rewriter also extracts named entities (`companyId`, `period`, `amount`, `customer`, …) and resolves coreferences using the last N conversation turns.

Output: `RewrittenIntent { NormalizedText, ExtractedEntities }`.

### Stage 3 — Semantic Cache

Cosine-similarity lookup on the **normalised text** (not the raw input):

| Mode | Component | Notes |
|------|-----------|-------|
| Local | `InMemorySemanticCache` + `EmbeddingSimulator` | Keyword TF-IDF-style vectors, threshold 0.75 |
| Live  | `BedrockSemanticCache` + `BedrockEmbeddingProvider` | Titan Embeddings V2 (1536-dim), threshold 0.88, disk cache at `embeddings_cache.json` |

A **cache HIT** skips Stages 4–6 entirely and jumps straight to Stage 7 plan execution, reusing the cached `IntentResult` and `ExecutionPlan`. The cache is pre-warmed at startup from each plan's `sampleQueries`.

### Stage 4 — Intent Classification

Classifies the normalised message into one of the registered intents:

| Mode | Component | Behaviour |
|------|-----------|-----------|
| Local | `RuleBasedClassifier` | Deterministic keyword/regex rules, O(n) per message |
| Live  | `BedrockLlmClassifier` | Claude Haiku with the full `CanonicalPhrases.Intents` list injected into the system prompt |

`CanonicalPhrases.Intents` and `IntentToAgent` are loaded dynamically from the YAML plan files at startup — the classifier always reflects the current set of registered agents.

Output: `IntentResult { Intent, Confidence, Entities, AgentId }`.

### Stage 4b — Capability Fallback

When Stage 4 returns `Unknown`, `AgentRegistry.FindByCapability()` scans all registered agents. For each manifest it checks whether any declared capability keyword appears in the normalised message (hyphen-separated capabilities are split into component words). The first matching agent is used with confidence `0.5`. This provides a secondary routing path without requiring a precise classifier match.

```
► Stage 4  Intent classification...  ✗ Unknown (0%)
► Stage 4b Capability match...       ✓ BookkeepingAgent [cash-flow, bookkeeping]
► Stage 5  Agent registry lookup...  ✓ BookkeepingAgent
```

If no capability matches, a "could not determine" response is returned.

### Stage 5 — Agent Registry Lookup

`AgentRegistry.Resolve(agentId)` performs an O(1) dictionary lookup by `AgentId`, returning the `AgentManifest` that holds the plan factory.

### Stage 6 — Execution Plan Generation

`AgentManifest.BuildPlan(intent, context)` invokes the plan factory, which was constructed from the YAML step definitions:

1. **Base parameters** — `defaultEntities` from the YAML plan merged with `companyId` from the routing context
2. **Entity override** — extracted entities from the rewriter/classifier are merged on top
3. **Step parameters** — static parameters declared per-step in YAML override the runtime base (highest precedence)

Output: `ExecutionPlan { PlanId, Intent, Steps[] }` where each `PlanStep` has a step ID, action name, merged parameter dictionary, and dependency list.

### Stage 7 — Execute & Cache

`PlanExecutor.ExecuteAsync()` runs each step in topological order (respecting `DependsOn`), then stores the result in the semantic cache and appends the turn to `ConversationHistory`.

**Data retrieval:** before steps run, `CompanyDataStore.GetTransactions(companyId, period)` fetches all matching financial records for the resolved period. Every step receives this pre-fetched `ExecutionData` (records, current balance, period, company ID) so computed values — net flow, burn rate, tax liability, runway — are derived from real seeded transactions, not stub strings.

**RAG generation (optional):** report steps (`generate-cash-flow-report`, `generate-pl-report`, `generate-runway-report`) detect a live `RagSummarizer` and, when present, pass the retrieved records as context to Claude Haiku, which narrates a grounded financial narrative. When `RagSummarizer` is absent (`USE_BEDROCK_RAG=false`), a local formatter produces the report with no Bedrock call.

---

## Component Reference

### Intent Rewriter

**Local** (`RegexIntentRewriter`): pure string transforms — currency words → symbols, relative dates → canonical aliases, synonym normalisation (e.g. "bookkeeping" → "cash flow").

**Live** (`LlmIntentRewriter`): two `SystemContentBlock` messages sent to Claude Haiku:
1. Static prompt — rewriting rules, entity extraction format, output constraints
2. Dynamic runtime context — current date, month, prior month, fiscal quarter, prior quarter, fiscal year, YTD start date, day of week

The runtime context lets the LLM resolve relative references such as "last quarter" or "this fiscal year" precisely against the actual calendar.

### Semantic Cache

`ISemanticCache` interface with two implementations. Both share:
- `Lookup(text)` — returns `(CacheHit, score)` or null
- `Store(text, intent, plan)` — stores normalised entry
- `SimilarityThreshold` — cosine score required for a hit

`BedrockEmbeddingProvider` maintains a **local disk cache** (`embeddings_cache.json`) so that Titan Embeddings calls are never repeated for the same text across restarts. The cache is loaded at startup and updated on every new embedding.

### Intent Classifier

`IIntentClassifier.Classify(text)` returns `IntentResult`.

`RuleBasedClassifier` uses ordered keyword rules per intent. Intents are tried in declaration order; the first match wins. Adding rules for new intents requires no YAML change — only code.

`BedrockLlmClassifier` sends the normalised text to Claude Haiku with a structured system prompt listing every intent from `CanonicalPhrases.Intents`. The response is parsed to extract intent name, confidence, and any entity key/value pairs.

### Agent Registry

`AgentRegistry` is a flat `Dictionary<string, AgentManifest>` keyed by `AgentId`.

**`LoadFromPlans(directory)`** (preferred) — reads all `*.yaml` plan files and constructs one `AgentManifest` per plan. The plan factory closure captures the `PlanDefinition` and builds `ExecutionPlan` instances dynamically. Adding a new agent requires only a new YAML file.

**`FindByCapability(text)`** — secondary routing path. Checks each manifest's `Capabilities` array against the normalised text. A capability string is matched if (a) the full phrase appears, or (b) any individual word in the phrase (> 3 chars) appears. Returns the first matching manifest.

**`BuildDefault()`** — lightweight fallback used by unit tests. No plan factories; manifests return a generic 3-step plan (authenticate → execute → generate-report).

### Execution Plans

```csharp
record PlanStep(int StepId, string Action, Dictionary<string,string> Input, int[] DependsOn);
record ExecutionPlan(string PlanId, string Intent, List<PlanStep> Steps);
```

Steps form a DAG. `PlanExecutor` respects `DependsOn` ordering (currently all steps are simulated sequentially). The `Input` dictionary on each step contains the full merged runtime context: company ID, extracted entities, and any static values from the YAML declaration.

### Company Data Store

`CompanyDataStore` is an in-memory ledger seeded with 40 financial records for company `DEMO-001` spanning January–May 2026:

| Category | Records | Notes |
|----------|---------|-------|
| Income — Consulting | 5 | TechVentures LLC, $5 K/month |
| Income — Licenses | 5 | GlobalCorp, Acme, RetailCo, StartupCo |
| Expense — Payroll | 5 | $18 K/month |
| Expense — Rent | 5 | $3.5 K/month |
| Expense — AWS | 5 | ~$1.1 K/month |
| Expense — Other | 15 | Marketing, Travel, Insurance, Equipment, Microsoft 365 |

Key aggregate facts baked into the seed data:
- May net flow: **+$4,390** (positive — best month)
- April net flow: **−$6,890** (negative — equipment purchase)
- Q1 2026 net flow: **−$25,460** (ramp-up quarter)
- Current balance: **$142,500**

`GetTransactions(companyId, period)` understands natural-language period strings: `"last month"`, `"this month"`, `"last 30 days"`, `"last 6 months"`, `"Q1 2026"`, `"March"`, `"FY2024"`, and bare years.

### RAG Summarizer

`RagSummarizer` closes the Retrieve → Augment → Generate loop. It receives the pre-fetched `FinancialRecord` list and calls Claude Haiku with the transactions serialised as the data context:

| Method | Prompt focus |
|--------|--------------|
| `SummarizeCashFlowAsync` | Net flow, top inflows/outflows, period trend |
| `SummarizeProfitLossAsync` | Revenue, COGS estimate, gross margin, net income |
| `SummarizeRunwayAsync` | Monthly burn rate, runway months from current balance |

Settings: `MaxTokens = 400`, `Temperature = 0.3f`, system prompt instructs Claude to behave as a concise financial analyst using specific dollar amounts with no markdown headers.

Enabled by `USE_BEDROCK_RAG=true`. Falls back transparently to local formatters when disabled.

### Conversation History

`ConversationHistory` holds an ordered list of `ConversationTurn` records (user message, normalised message, entities, intent, agent, response, timestamp). The rewriter receives the last `RewriterContextTurns` turns (default 4) as context for coreference resolution.

The guardrail at Stage 1 is suppressed when `history.TotalTurns > 0`, preventing false rejections on follow-up queries that reference prior context without domain keywords.

---

## YAML Plan Schema

Each file in `plans/` describes one agent and its execution steps:

```yaml
planId: cashflow-001          # unique plan identifier
intent: GenerateCashFlowReport # intent string matched by the classifier
agentId: BookkeepingAgent      # AgentId key in the registry
displayName: Bookkeeping Agent # human-readable label
description: "..."

capabilities:                  # keyword tags used for capability-based routing
  - cash-flow
  - bookkeeping

defaultEntities:               # injected into every step as base parameters
  companyId: DEMO-001
  period: last month

sampleQueries:                 # used to pre-warm the semantic cache at startup
  - "Generate a cash flow report for last month"
  - "What's the cash position for this quarter?"

steps:                         # ordered plan steps (form a DAG via dependsOn)
  - id: 1
    action: fetch-transactions
    dependsOn: []              # no dependencies — runs first

  - id: 2
    action: classify-transactions
    dependsOn: [1]
    parameters:
      ledgerType: operating    # static override — wins over runtime entities

  - id: 3
    action: compute-net-flow
    dependsOn: [2]

  - id: 4
    action: generate-cash-flow-report
    dependsOn: [3]
```

**Parameter precedence (highest → lowest):**
1. YAML `parameters` on the step
2. Entities extracted by the rewriter/classifier
3. `defaultEntities` from the plan
4. `companyId` from `RoutingContext`

---

## Registered Agents

| Agent ID | Intent | Capabilities | Steps |
|----------|--------|--------------|-------|
| `BookkeepingAgent` | `GenerateCashFlowReport` | cash-flow, bookkeeping | fetch-transactions → classify-transactions → compute-net-flow → generate-cash-flow-report |
| `InvoiceAgent` | `CreateInvoice` | invoicing, billing | validate-customer → create-invoice-record → apply-tax → send-invoice |
| `ReconciliationAgent` | `ReconcileAccount` | reconciliation, bookkeeping | fetch-bank-statement → fetch-ledger-entries → match-transactions → flag-discrepancies → generate-reconciliation-report |
| `TaxAgent` | `EstimateTaxLiability` | tax, compliance | fetch-taxable-income → apply-deductions → compute-tax-liability → generate-tax-estimate-report |
| `ReportingAgent` | `GenerateProfitLoss` | profit-loss, reporting | fetch-revenue → fetch-expenses → compute-gross-profit → compute-net-income → generate-pl-report |
| `ProfitAuditAgent` | `AnalyzeProfitAnomaly` | profit-audit, anomaly-detection | fetch-profit-series → compute-zscore → flag-anomalies → generate-anomaly-report |
| `TaxOptimizationAgent` | `OptimizeTaxDeductions` | tax-optimization, deduction-analysis | fetch-expenses → categorize-deductibles → rank-deductions → generate-optimization-report |
| `CashRunwayAgent` | `ForecastCashRunway` | cash-runway, forecasting | fetch-transactions → compute-burn-rate → compute-cash-balance → forecast-runway → generate-runway-report |

---

## AWS Bedrock Integration

| Service | Model | Used For |
|---------|-------|----------|
| Claude 3 Haiku | `anthropic.claude-3-haiku-20240307-v1:0` | Intent classification, intent rewriting, streaming fallback conversation |
| Titan Embeddings V2 | `amazon.titan-embed-text-v2:0` | Semantic cache embeddings (1536 dimensions, normalised) |

All Bedrock calls use the **Converse API** (`ConverseAsync`) for Claude and `InvokeModelAsync` for Titan. Temporary session credentials (access key, secret key, session token) are supported via `.env`.

**Embedding disk cache:** `BedrockEmbeddingProvider` persists all Titan embeddings to `embeddings_cache.json` in the output directory. On restart, previously embedded strings are served from disk with no Bedrock call.

---

## Feature Flags

All flags default to `false` (fully local, no AWS required):

| Flag | `false` (default) | `true` (live) |
|------|-------------------|---------------|
| `USE_LLM_REWRITER` | `RegexIntentRewriter` (< 1 ms) | `LlmIntentRewriter` via Claude Haiku |
| `USE_BEDROCK_CACHE` | `InMemorySemanticCache` + `EmbeddingSimulator` | `BedrockSemanticCache` + Titan Embeddings V2 |
| `USE_BEDROCK_CLASSIFIER` | `RuleBasedClassifier` (deterministic) | `BedrockLlmClassifier` via Claude Haiku |
| `USE_BEDROCK_RAG` | Local formatters (no Bedrock call) | `RagSummarizer` via Claude Haiku — LLM-narrated reports grounded in retrieved transactions |

The four flags are independent and can be combined freely. A common setup for development: `USE_LLM_REWRITER=true` only, to test coreference resolution without incurring embedding or classification costs.

---

## Configuration Reference

Copy `.env.template` to `.env` and set values:

```bash
# AWS
AWS_REGION=us-east-1

# Bedrock model IDs
BEDROCK_LLM_MODEL_ID=anthropic.claude-3-haiku-20240307-v1:0
BEDROCK_STREAM_MODEL_ID=anthropic.claude-3-haiku-20240307-v1:0
BEDROCK_REWRITER_MODEL_ID=anthropic.claude-3-haiku-20240307-v1:0
EMBEDDING_MODEL_ID=amazon.titan-embed-text-v2:0

# Feature flags (false = local/offline mode)
USE_LLM_REWRITER=false
USE_BEDROCK_CACHE=false
USE_BEDROCK_CLASSIFIER=false
USE_BEDROCK_RAG=false

# Cache
CACHE_SIMILARITY_THRESHOLD=0.88   # 0.75 for EmbeddingSimulator, 0.88 for Titan
EMBEDDING_DIMENSIONS=1536

# Conversation
REWRITER_CONTEXT_TURNS=4          # recent turns sent to the LLM rewriter
```

For live AWS access, also provide:

```bash
AWS_ACCESS_KEY_ID=...
AWS_SECRET_ACCESS_KEY=...
AWS_SESSION_TOKEN=...             # if using temporary credentials
```

---

## Getting Started

```bash
git clone https://github.com/david-redano/cff-routing-layer
cd cff-routing-layer/CffRoutingLayerDemo

cp .env.template .env
# edit .env — defaults work for local mode with no AWS credentials

dotnet run
```

The startup sequence:
1. Loads `.env`
2. Reads all `plans/*.yaml` → registers intents, agents, and capabilities
3. Pre-warms the semantic cache from each plan's `sampleQueries`
4. Opens an interactive REPL

Example session:

```
> Generate a cash flow report for last month
  ► Stage 2  Intent rewriter...              ✓ "Generate a cash flow report for last month"
  ► Stage 3  Semantic cache lookup...        ✓ HIT  (similarity: 100%)
  ► Stage 7  Executing plan (from cache)...

> Now do it for Q1
  ► Stage 2  Intent rewriter...              ✓ "Generate a cash flow report for Q1 2026"
  ► Stage 3  Semantic cache lookup...        ✗ MISS
  ► Stage 4  Intent classification...        ✓ GenerateCashFlowReport (95%)
  ► Stage 5  Agent registry lookup...        ✓ BookkeepingAgent
  ► Stage 6  Execution plan generated...     ✓ 4 steps
  ► Stage 7  Executing plan...
```

---

## Running Tests

```bash
dotnet test CffRoutingLayerDemo.Tests/
```

20 tests covering: domain guardrail, intent classification (all 8 agents), semantic cache hit/miss, rewriter normalisation, and end-to-end routing pipeline. All tests run fully offline — no AWS credentials required.

---

## Running Benchmarks

```bash
dotnet run --project CffRoutingLayerDemo.Benchmarks -c Release
```

Benchmarks cover the routing pipeline, cache warmer, and intent rewriter in local (offline) mode.
