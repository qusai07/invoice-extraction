# Invoice Extraction & PO Matching — Project Plan

> Working name: **InvoiceFlow**
> Status: Planning · Created 2026-11-05
> Goal: a real product, not a demo. It must run in production for a finance team, with audit, security and measurable results.

---

## 1. Problem & Goal

Accounts-payable (AP) teams still type supplier invoices (PDFs, scans, phone photos) into the ERP by hand. This is slow (3–10 min per invoice), error-prone, and the matching against purchase orders (POs) is done by eye.

**InvoiceFlow** takes an invoice in any format, extracts the data with AI, **validates it with deterministic C# rules** against the PO (and goods receipt), and:

- **posts matching invoices straight to the ERP** (touchless), and
- **sends mismatches to a person** with a clear reason, side-by-side with the original document.

### Core principle
> **The AI only reads. C# decides.**
> The model never approves a payment. Every decision to post comes from rules that are explainable, unit-tested and auditable.

### Success metrics (targets for the first production customer)

| KPI | Target | How measured |
|---|---|---|
| Touchless rate (no human edit) | ≥ 70% at go-live, ≥ 80% after 3 months | `posted_auto / total_received` |
| False-accept rate (wrong data posted to ERP) | **< 0.5%** | Sampled audit + ERP reversals |
| Field accuracy (header fields) | ≥ 97% | Eval set, exact match |
| Line-item accuracy | ≥ 92% | Eval set, per-line match |
| Median processing time (upload → decision) | < 60 s | Telemetry |
| Reviewer time per exception | < 90 s | Review UI timing |
| AI cost per invoice | < $0.05 | Token/usage logs |

---

## 2. Users & Roles (RBAC)

| Role | Can do |
|---|---|
| **Submitter** | Upload invoices, see status of own uploads |
| **AP Clerk (Reviewer)** | Work the review queue, correct fields, approve/reject, add notes |
| **AP Supervisor** | Everything a clerk can + override tolerance failures, reassign, approve high-value invoices (4-eyes) |
| **Finance Admin** | Manage suppliers, tolerances, approval thresholds, ERP mappings |
| **Auditor** | Read-only access to everything incl. full audit trail |
| **System Admin** | Tenants, users, integrations, API keys, retention settings |

Rules:
- **Segregation of duties**: the person who corrected an invoice cannot be the only approver if the amount is above the threshold.
- All roles are per **tenant** (multi-company from day one, even if we start with one).

---

## 3. Scope

### In scope (v1)
- Input channels: web upload (drag & drop, multi-file), **email inbox** (e.g. `ap@company.com`), REST API.
- Formats: PDF (text and scanned), JPG/PNG/HEIC photos, multi-page, multiple invoices in one PDF (split).
- Languages: **Arabic + English** invoices, Arabic-Indic digits, RTL UI.
- Extraction: header fields, line items, VAT breakdown, bank details.
- Validation: format, arithmetic, VAT (UAE 5%, zero-rated, exempt), supplier master, PO 2-way match, GRN 3-way match.
- Human review queue with side-by-side viewer and field highlighting.
- ERP posting via adapter (first: mock ERP + one real ERP).
- Audit log, dashboards, eval pipeline.

### Out of scope (v1) — later
- Payment execution (we stop at "approved & posted").
- Supplier self-service portal.
- Credit notes / debit notes (v1.1).
- Non-PO invoices (utilities, subscriptions) with GL coding suggestions (v1.2).
- E-invoicing (UAE e-invoicing mandate / Peppol) — **track it**, design the model to fit (see §12).

---

## 4. High-Level Architecture

```
            ┌────────────┐   ┌─────────────┐   ┌──────────┐
 Channels → │ Web Upload │   │ Email (IMAP/│   │ REST API │
            └─────┬──────┘   │  Graph API) │   └────┬─────┘
                  │          └──────┬──────┘        │
                  └────────────┬────┴───────────────┘
                               ▼
                    ┌──────────────────────┐
                    │ Intake Service        │  store original (Blob), SHA-256,
                    │                       │  virus scan, duplicate check
                    └──────────┬───────────┘
                               ▼  (queue: invoice.received)
                    ┌──────────────────────┐
                    │ Pre-processing        │  page split, rotate/deskew,
                    │                       │  invoice boundary detection
                    └──────────┬───────────┘
                               ▼  (queue: invoice.ready)
                    ┌──────────────────────┐
                    │ Extraction (AI)       │  LLM + JSON schema → InvoiceDto
                    │  IInvoiceExtractor    │  (+ bounding boxes / page refs)
                    └──────────┬───────────┘
                               ▼  (queue: invoice.extracted)
                    ┌──────────────────────┐
                    │ Normalization (C#)    │  digits, dates, currency, TRN,
                    │                       │  supplier resolution
                    └──────────┬───────────┘
                               ▼
                    ┌──────────────────────┐
                    │ Validation Engine (C#)│  rule pipeline → ValidationReport
                    │  + PO / GRN Matching  │
                    └──────────┬───────────┘
                     pass ┌────┴────┐ fail / low confidence
                          ▼         ▼
               ┌──────────────┐  ┌──────────────────┐
               │ Approval      │  │ Review Queue (UI) │ ← clerk corrects
               │ (thresholds)  │  └────────┬─────────┘
               └──────┬───────┘           │ re-validate
                      ▼                   ▼
               ┌──────────────────────────────┐
               │ ERP Adapter (outbox pattern)  │ → ERP
               └──────────────────────────────┘
                      │
                      ▼
               Audit log · Metrics · Corrections → Eval dataset
```

### Key design decisions
- **Asynchronous pipeline** with a message queue. Every step is idempotent and retryable.
- **State machine** per invoice (see §6). No hidden state.
- **Outbox pattern** for ERP posting, so we never post twice and never lose a post.
- **Extractor behind an interface** (`IInvoiceExtractor`), so we can swap models/providers and A/B test them with the eval set.
- **Original document is immutable**. Every extraction, correction and decision is a new versioned record.

---

## 5. Tech Stack

| Layer | Choice | Why |
|---|---|---|
| Backend | **.NET 10 (LTS) / ASP.NET Core**, Clean Architecture | Team strength, strong typing for validation. .NET 8 support ends Nov 2026 (ADR-0003) |
| Background jobs | .NET Worker Services + **Azure Service Bus** (or RabbitMQ on-prem) | Retry, dead-letter, scale |
| Database | **SQL Server / Azure SQL** + EF Core | Fits ERP world, temporal tables for audit |
| File storage | Azure Blob Storage (or MinIO on-prem) | Originals + page images |
| AI extraction | **Azure OpenAI / Foundry** (GPT-class vision model with structured outputs) as primary; **Azure AI Document Intelligence** (prebuilt-invoice) as baseline/fallback | Data residency (UAE North), enterprise contracts; also covers AI-103 topics. Provider-agnostic interface keeps Claude/others as options. |
| Frontend | **Next.js + TypeScript**, Tailwind, AR/EN with RTL | Same stack as support-ticket-assistant |
| PDF viewer | PDF.js with overlay highlights | Click a field → highlight on document |
| Auth | **Microsoft Entra ID** (OIDC) + app roles; local accounts for dev | Enterprise SSO |
| Observability | OpenTelemetry → Application Insights | Traces across pipeline steps |
| CI/CD | GitHub Actions / Azure DevOps, Docker | |
| Infra | Bicep or Terraform | Repeatable environments |

---

## 6. Invoice Lifecycle (State Machine)

```
Received → Preprocessing → Extracting → Extracted → Validating
   │                                                    │
   │                                ┌───────────────────┼─────────────────┐
   ▼                                ▼                   ▼                 ▼
Rejected (virus /             NeedsReview         PendingApproval      Duplicate
 unreadable / not invoice)          │             (above threshold)        │
                                    ▼                   │                 ▼
                              (corrected) → Validating  │              Closed
                                    │                   ▼
                                    └──────────→  Approved → Posting → Posted
                                                                 │
                                                                 ▼
                                                            PostingFailed (retry / manual)
```

Each transition writes an audit event: `who`, `when`, `from`, `to`, `reason`, `payload diff`.

---

## 7. Domain Model (core)

```csharp
Tenant, User, Role

Supplier        { Id, TenantId, Name, NameAr, Trn, Aliases[], Iban, DefaultCurrency, ToleranceProfileId, IsActive }
PurchaseOrder   { Id, PoNumber, SupplierId, Currency, Status, Lines[] }
PoLine          { LineNo, Sku, Description, Qty, UnitPrice, VatRate, QtyInvoiced, QtyReceived }
GoodsReceipt    { Id, PoId, Date, Lines[] { PoLineNo, QtyReceived } }

InvoiceDocument { Id, TenantId, Source, FileName, BlobUri, Sha256, PageCount, ReceivedAt, Status }
Extraction      { Id, DocumentId, Version, Model, PromptVersion, RawJson, Invoice (InvoiceDto),
                  FieldEvidence[] { Field, Page, BBox, Confidence }, TokensIn, TokensOut, CostUsd, DurationMs }
InvoiceDto      { SupplierName, SupplierTrn, CustomerTrn, InvoiceNumber, InvoiceDate, DueDate,
                  PoNumber, Currency, Lines[], Subtotal, VatTotal, Total, VatBreakdown[], Iban }
ValidationReport{ Id, ExtractionVersion, Results[] { RuleCode, Severity, Field, Expected, Actual, Message }, Outcome }
LineMatch       { InvoiceLineNo, PoLineNo?, MatchMethod (Sku|Fuzzy|Manual), Score }
ReviewTask      { Id, DocumentId, AssignedTo, Reasons[], OpenedAt, ClosedAt, Decision }
Correction      { Field, OldValue, NewValue, UserId, At }   // feeds eval dataset
ErpPosting      { Id, DocumentId, Payload, ErpReference, Attempts, Status }  // outbox
AuditEvent      { Id, EntityType, EntityId, Action, UserId, At, Data }
ToleranceProfile{ PricePct, PriceAbs, QtyPct, TotalAbs, AllowPartialInvoice }
```

---

## 8. Extraction Design (AI)

- **Input**: page images (and text layer if present) → vision LLM.
- **Output**: strict **JSON schema** (structured outputs), not free text. Every field is nullable. The model must return `null` instead of guessing.
- **Evidence**: for each field return the page number and the exact source text. Bounding boxes come from Document Intelligence/OCR when available. Used for UI highlighting and for checking that the source text really exists on the page (anti-hallucination check).
- **Prompt**: versioned file in repo (`prompts/extract-invoice.v3.md`), hashed and stored with every extraction.
- **No business logic in the prompt**. No "check if totals match" — that is C#'s job.
- **Normalization in C#**, not in the prompt: Arabic-Indic digits → Latin, date formats (dd/MM/yyyy vs MM/dd), thousand separators, currency symbols (AED, د.إ, SAR, USD).
- **Large / multi-invoice PDFs**: boundary detection step first, then extract per invoice.
- **Fallback chain**: primary model → if schema invalid or critical fields missing, retry once with higher-detail images → else Document Intelligence → else human review.
- **Confidence**: the model's self-reported confidence is **not** trusted alone. Real confidence = schema valid + evidence found on page + all C# rules pass.

---

## 9. Validation Rules (C#)

Each rule is a class implementing `IValidationRule`, unit-tested, with a code, severity (`Error` → review, `Warning` → shown but not blocking) and message in AR/EN.

| Code | Rule | Severity |
|---|---|---|
| `FMT-001` | Required fields present (supplier, number, date, total, currency) | Error |
| `FMT-002` | TRN is 15 digits (UAE format) | Error |
| `FMT-003` | Invoice date not in future, not older than N days | Warning |
| `ARI-001` | Σ line totals = subtotal (± 0.01 per line rounding) | Error |
| `ARI-002` | qty × unit price = line total | Error |
| `ARI-003` | subtotal + VAT = total | Error |
| `VAT-001` | VAT amount = subtotal × rate (5% standard / 0% zero-rated / exempt) | Error |
| `VAT-002` | Supplier TRN present when VAT charged | Error |
| `SUP-001` | Supplier resolved to master data (TRN first, then name/alias fuzzy) | Error |
| `SUP-002` | Bank account (IBAN) matches supplier master — **fraud check** | Error (Supervisor only) |
| `DUP-001` | Same file hash already received | Error → Duplicate |
| `DUP-002` | Same supplier + invoice number (+ total) already exists | Error |
| `PO-001` | PO number exists, is open, belongs to this supplier | Error |
| `PO-002` | Each invoice line matched to a PO line | Error |
| `PO-003` | Unit price within tolerance of PO price | Error |
| `PO-004` | Invoiced qty ≤ ordered qty − already invoiced | Error |
| `PO-005` | Currency matches PO | Error |
| `GRN-001` | (3-way) Invoiced qty ≤ received qty | Error |
| `APR-001` | Total above approval threshold → PendingApproval (4-eyes) | Routing |

### Line matching strategy
1. Exact SKU / item code match.
2. Fuzzy description match (normalized tokens, AR/EN) + price proximity + qty — scored.
3. If best score < threshold or ambiguous → review, with top-3 suggestions.
4. Reviewer's manual match is saved as a **supplier alias** for next time (the system learns from reviews, with no model retraining needed).

---

## 10. Human Review UX

- **Queue**: filter by reason, supplier, amount, age; SLA indicators; assign/claim.
- **Review screen**: document viewer (left) · extracted fields (right) · failing rules at top in red with plain-language reasons.
- Click a field → highlight its source on the page. Click on the page → pick a value.
- Line-item grid: invoice lines vs PO lines side by side, with match status.
- Actions: **Correct & re-validate**, **Approve with override** (requires reason, Supervisor role), **Reject** (reason → optional email to supplier), **Mark not an invoice**.
- Keyboard-first (clerks process hundreds per day).
- Every correction is stored as a `Correction` → goes to the eval dataset.

---

## 11. ERP Integration

- `IErpAdapter` interface: `ValidateSupplier`, `GetPurchaseOrder`, `GetGoodsReceipts`, `PostInvoice`, `GetPostingStatus`.
- Adapters: **MockErp** (dev/demo), then the first real target (decide: **Dynamics 365 F&O / Business Central, SAP B1, Oracle**, or a customer's custom system).
- Master data sync (suppliers, POs, GRNs): scheduled pull + on-demand lookup, cached locally.
- Posting through **outbox** table, retry with backoff, dead-letter → `PostingFailed` visible in UI.
- Idempotency key = tenant + supplier + invoice number.

---

## 12. Security, Compliance & Governance

- **Data residency**: host in UAE region (Azure UAE North) and use in-region AI deployments. Configurable per tenant.
- **UAE PDPL** and customer policies: data minimization, retention policy per tenant (e.g. keep originals 5+ years for **FTA VAT record-keeping**).
- **Encryption**: TLS everywhere, encryption at rest, Key Vault for secrets, managed identities (no keys in config).
- **AI data handling**: no training on customer data (enterprise endpoints only), prompts/responses logged with PII-aware retention.
- **Prompt-injection defence**: document text is data, not instructions. The extractor has no tools and no actions. Output is schema-validated, so injected text can't trigger actions. All decisions are in C#.
- **Fraud controls**: IBAN change detection, new-supplier hold, duplicate detection, amount thresholds, 4-eyes.
- **Audit trail**: immutable `AuditEvent` + SQL temporal tables, exportable for auditors.
- **E-invoicing readiness**: UAE is rolling out a mandatory e-invoicing model (Peppol-based). Keep `InvoiceDto` aligned to structured invoice fields so XML e-invoices can bypass AI extraction entirely and go straight to validation.
- **Responsible AI**: clear indication in UI of AI-extracted vs human-entered values; human in the loop for all exceptions; documented limitations.

---

## 13. Evals & Quality

- **Golden dataset**: start with 100 invoices (real anonymized + synthetic), covering EN, AR, bilingual, scans, phone photos, multi-page, multi-invoice PDFs, handwritten notes, stamps.
- Ground truth stored as JSON next to each file (`evals/data/{id}.pdf` + `{id}.expected.json`).
- **Eval runner** (console app / CI job) reports:
  - per-field exact-match accuracy
  - document-level "all fields correct" rate
  - line-item precision/recall
  - simulated touchless rate & **false-accept rate** (full pipeline incl. C# rules)
  - cost & latency per document
- **Runs on every PR** that touches prompts, schema, extractor or rules. Regression → build fails.
- **Production feedback loop**: reviewer corrections are added to the dataset weekly (after anonymization).
- **Model comparison**: same dataset across providers/models → choose by accuracy *and* cost.

---

## 14. Observability & Operations

- Distributed trace per invoice across all pipeline steps.
- Dashboards: volume, touchless rate, exceptions by reason, queue age, AI cost/day, model latency, ERP failures.
- Alerts: queue backlog, error spikes, AI provider failures, false-accept reported, cost above budget.
- Health checks, dead-letter monitoring, replay tool for failed messages.

---

## 15. Repository Structure

```
invoice-extraction/
├─ PLAN.md
├─ docs/                     architecture, ADRs, runbooks
├─ src/
│  ├─ InvoiceFlow.Domain/          entities, value objects, state machine
│  ├─ InvoiceFlow.Application/     use cases, validation rules, matching
│  ├─ InvoiceFlow.Infrastructure/  EF Core, Blob, Service Bus, ERP adapters
│  ├─ InvoiceFlow.Extraction/      IInvoiceExtractor, providers, prompts, normalization
│  ├─ InvoiceFlow.Api/             REST API, auth, RBAC
│  └─ InvoiceFlow.Worker/          pipeline background workers
├─ web/                      Next.js review UI (AR/EN, RTL)
├─ evals/
│  ├─ data/                  golden invoices + expected JSON
│  └─ InvoiceFlow.Evals/     eval runner
├─ tests/
│  ├─ InvoiceFlow.Domain.Tests/
│  ├─ InvoiceFlow.Application.Tests/   one test class per rule
│  └─ InvoiceFlow.Integration.Tests/   Testcontainers (SQL, Azurite)
├─ infra/                    Bicep/Terraform
└─ docker-compose.yml        SQL Server, Azurite, RabbitMQ/Service Bus emulator, MockErp
```

---

## 16. Phased Delivery

Each phase ends with something that runs end-to-end and is measured.

### Phase 0 — Foundations (1 week) · *in progress*
- [x] Solution structure (.NET 10, Clean Architecture), central package management, warnings-as-errors
- [x] docker-compose (SQL Server, Azurite), `.env.example`
- [x] CI workflow (build, test, dataset check)
- [x] ADRs 0001–0005 (0004 and 0005 still *Proposed*)
- [x] Invoice state machine + tests, API `/health` + integration test
- [x] Golden dataset format + checker (`evals/data/README.md`)
- [ ] `git init` + push to remote (done by owner)
- [x] 30 **synthetic** invoices + expected JSON (generator, seed 42)
- [ ] Collect **real anonymized invoices** (target 30 before pilot, 100 total. See evals/data/README.md)
- [x] AI provider: Azure OpenAI (ADR-0004 accepted)
- [x] Queue: SQL Server job table (ADR-0005 accepted)
- [ ] Decide ERP target, hosting (see §18)
- **Exit**: CI green, sample dataset in repo, ADRs written.

### Phase 1 — Extraction core + evals (2 weeks) · *in progress*
- [x] `InvoiceDto` contract + `IInvoiceExtractor` (Application)
- [x] C# normalization: Arabic-Indic digits, amounts, dates (incl. Arabic months), VAT %, currency, TRN, IBAN
- [x] Versioned prompt + strict JSON schema; model transcribes, C# parses (`RawInvoiceMapper`)
- [x] PDF → PNG page rendering (PDFium)
- [x] `AzureOpenAiInvoiceExtractor` (structured outputs, API key or Entra ID, cost/latency tracking)
- [x] Synthetic invoice generator (EN / bilingual / AR, multi-page, PNG, injection cases)
- [x] Eval runner: per-field exact match, line P/R, hallucinations, cost, latency, breakdown by language/quality; ground-truth self-test in CI
- [ ] **First real baseline run** against Azure OpenAI (needs endpoint + deployment)
- [ ] Document Intelligence `prebuilt-invoice` extractor as baseline/fallback
- [ ] More Arabic-only and degraded-image (scan/photo) synthetic cases
- **Exit**: eval report on 30+ invoices; header accuracy baseline known.

### Phase 2 — Validation & matching engine (2 weeks) · *done (pending first Azure run)*
- [x] Rule engine (ADR-0006): facts gathered once, pure rules, decision order DUP → review → approval → approved
- [x] 23 rules with EN/AR messages: EXT-001/002, FMT-001/002/003, CUS-001, ARI-001/002/003, VAT-001/002, SUP-001/002/003, DUP-001/002, PO-001…005, GRN-001, APR-001
- [x] Supplier resolver (TRN, then EN/AR name + aliases); line matcher (SKU, description containment EN/AR, price, quantity fit)
- [x] Mock ERP (JSON master data) + in-memory invoice history
- [x] Synthetic master data + 8 planted business scenarios; 150-invoice scenario test; CI `--expect-perfect`
- [x] Eval report: false accepts, straight-through rate, decision accuracy, caught-for-right-reason
- [x] API: `POST /api/invoices/validate` (InvoiceDto), `POST /api/invoices` (upload → extract → validate)
- **Exit**: full pipeline (no UI) via API: upload → decision; simulated touchless & false-accept rates in eval report. *(Code complete; real false-accept numbers need the Azure baseline run.)*

### Phase 3 — Workflow, persistence & review UI (3 weeks) · *done (Entra ID sign-in not yet tested against a real tenant)*
**3a — backend**
- [x] `InvoiceDocument` aggregate: state machine enforced, immutable audit events
- [x] SQL Server persistence (EF Core 10, migrations), optimistic concurrency (rowversion → 409)
- [x] SQL job queue with transactional enqueue (outbox), leases, retries/backoff, give-up → review (ADR-0005)
- [x] Pipeline: extract → validate → post (idempotent steps); worker host + in-process option
- [x] File storage: Azure Blob or file system; immutable originals; safe paths
- [x] Review actions: correct (re-validate), override (reason), four-eyes approve, reject (reason), retry posting
- [x] Field-level corrections stored (feed eval dataset)
- [x] Entra ID JWT + app roles; dev sign-in for Development/Testing only; default-deny; Submitter sees own docs (ADR-0007)
- [x] Integration tests on real SQL Server (Testcontainers): full flows, four-eyes, duplicates, RBAC, queue concurrency

**3b — review UI** (`web/`)
- [x] Review queue: tabs (needs review / pending approval / posting failed / all), rule chips, auto-refresh, drag-and-drop upload
- [x] Review screen: pdf.js viewer with zoom; focusing a field highlights its source text on the page (Arabic-Indic digits normalized)
- [x] Findings (errors first, click → jump to field), editable header + lines, PO-line match per line, Ctrl+S, unsaved-changes guard
- [x] Actions per role and status: save & re-check, override (reason), approve (four-eyes hint), reject (reason), retry posting
- [x] History: field corrections (old → new) and full audit trail
- [x] Arabic / English with RTL; Latin digits for amounts and codes
- [x] Sign-in: dev users (local) or Entra ID via MSAL (needs app registrations); identity and roles from `GET /api/me`
- [x] Security: CSP, no token in storage beyond MSAL's sessionStorage cache, API proxied (no CORS), same-site redirects only
- [x] CI job: npm audit, typecheck, lint, build
- [x] Verified with Playwright + Edge: 8 screens, 4 interactive flows (correct → posted, approve, reject reason, auditor read-only)
- [ ] Entra ID: create app registrations (API with app roles + scope, SPA) and test sign-in against the real tenant
- **Exit**: a clerk can process exceptions end-to-end; corrections feed dataset. ✔ (locally, dev sign-in)

### Phase 3c — Users, sign-in, roles & administration (≈ 3 weeks) · *design: ADR-0009 (proposed)*
| Slice | Content | Est. |
|---|---|---|
| 1. Identity core ✅ | Users/roles/permissions tables + seed; own session (JWT 15 min + rotating refresh cookie); local login, logout, logout-everywhere; security stamp; permission-based API authorization replacing role policies; first-admin bootstrap; login page + logout in UI. *Done 2026-11-05: browser-verified against local SQL; 9 security integration tests written, run pending Docker.* | 4–5 d |
| 2. Account security | MFA (authenticator app + recovery codes, mandatory for admins/approvers); invite + password reset by email (manual-link fallback); lockout; Entra SSO token exchange | 3–4 d |
| 3. Admin: users & roles | Users list/invite/deactivate/roles/approval limit/reset/force logout; roles editor with permission checklist; guard rails; admin audit log; approval limit enforced in approve + queue | 3 d |
| 4. Admin: validation rules | Versioned settings (author/time/reason); enable/disable + severity per rule (locked rules); thresholds, tolerances, VAT rates, company TRNs; reports record settings version | 2–3 d |
| 5. Admin: suppliers | Master-data view; per-supplier tolerance / 3-way; IBAN change with 4-eyes (propose → approve by another admin) | 2 d |
| 6. Admin: audit & system | Audit viewer (invoice + admin events, filters, CSV); AI usage/cost; queue health + retry; active sessions | 2 d |
- **Exit**: an admin manages users, roles, limits and rules from the UI; both sign-in types work; every admin change is audited; security tests for lockout, token reuse, revocation, enumeration pass.

### Phase 4 — ERP integration & hardening (3 weeks) · *hardening done; ERP adapter waits on ERP choice*
**Hardening (ADR-0008)**
- [x] Upload safety: type from file bytes (no polyglots), sanitized names, size limits at 3 layers
- [x] Malware scanning, fail closed: ClamAV (on-prem) or Defender for Storage (Azure; files unreadable until a clean verdict)
- [x] Per-user rate limits (global + uploads), API security headers, HSTS, forwarded headers behind ingress
- [x] OpenTelemetry traces + business metrics (received, decisions by system/human, AI cost, step duration/failures) → App Insights or OTLP
- [x] Liveness/readiness health checks (database)
- [x] Audit CSV export for auditors (formula-injection safe, bounded range)
- [x] Dockerfiles (non-root, no dev settings in images); full local stack in docker-compose (+ ClamAV profile)
- [x] Azure Bicep: Container Apps, Azure SQL (Entra-only), Storage (RBAC-only, soft delete, versioning), Defender, Azure OpenAI (keyless), ACR, Log Analytics/App Insights, one managed identity; compiles clean
- [x] CI: image builds, "no dev settings in image" check, Bicep build
- [x] Approval thresholds / 4-eyes, IBAN fraud check, 3-way match (done in Phases 2–3)
- [ ] Private endpoints + VNet; migrations from release pipeline; per-tenant isolation (see ADR-0008 "Not yet")
- [ ] First `az deployment group what-if` against a real subscription
- [ ] Load test (5,000 invoices/day), backup/restore drill, runbooks

**Integration**
- [ ] First real ERP adapter (decide: D365 F&O / Business Central / SAP B1 / Oracle / custom), master data sync
- [ ] Email intake channel, multi-invoice PDF splitting
- **Exit**: staging environment connected to ERP test instance; security checklist passed.

### Phase 5 — Pilot (4 weeks)
- One real AP team, **shadow mode first** (system decides, humans still type; compare), then live with low thresholds.
- Weekly eval updates from corrections, tune tolerances per supplier.
- **Exit**: KPIs from §1 measured on real traffic; go/no-go for production.

### Phase 6 — Productize (ongoing)
- Multi-tenant onboarding, credit notes, non-PO invoices with GL suggestions, supplier portal, e-invoicing (Peppol) intake, analytics.

**Total to pilot: ~15 weeks** (one developer full-time; less with a team).

---

## 17. Risks & Mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Model posts wrong amount silently | Financial loss, trust lost | AI never decides; arithmetic + PO rules; false-accept metric; shadow mode in pilot |
| Poor quality photos/scans | Low touchless rate | Pre-processing, rescan request, fallback to Document Intelligence |
| Line matching too weak | Most invoices go to review | Supplier aliases learned from reviews; per-supplier tolerances |
| ERP integration slower than expected | Delays pilot | MockErp + adapter interface from day 1; start ERP access requests in Phase 0 |
| Arabic invoice variety | Accuracy drop | Arabic-heavy eval set from the start; normalization in C# |
| AI provider outage / price change | Downtime / cost | Provider-agnostic interface, fallback provider, cost alerts |
| Invoice fraud (changed IBAN, fake supplier) | Financial loss | IBAN master check, new-supplier hold, 4-eyes |
| Prompt injection inside documents | Wrong data | Extract-only model, schema validation, decisions in C# |
| Not enough real samples | Weak evals | Ask pilot customer for 200 anonymized invoices early; synthetic generator |

---

## 18. Open Decisions (to close in Phase 0)

1. **AI provider**: Azure OpenAI/Foundry (residency, AI-103 aligned) vs Claude API vs both behind the interface? → *Proposed: Azure primary, keep interface open.*
2. **First ERP target**: Dynamics 365 / Business Central / SAP B1 / Oracle / custom?
3. **Hosting**: cloud (Azure UAE North) vs on-prem for government customers?
4. **Queue**: Azure Service Bus vs RabbitMQ (on-prem option)?
5. **Pilot customer & sample invoices**: who and when?
6. **Pricing model** (if sold): per invoice, per seat, or tiered volume?

---

## 19. Definition of Done (per feature)

- Unit tests for all rules/logic; integration tests for adapters.
- Eval suite passes with no regression.
- AR/EN strings for all UI text and rule messages.
- Audit events for every state change.
- RBAC enforced on API (not only UI).
- Telemetry (trace + metrics) added.
- Docs/ADR updated if architecture changed.
