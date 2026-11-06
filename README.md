# InvoiceFlow

AI invoice extraction with deterministic PO matching. Invoices that match post to the ERP. Exceptions go to a person.

> **AI only extracts. C# decides.** See [ADR-0002](docs/adr/0002-ai-extracts-csharp-decides.md).

- Product and delivery plan: [PLAN.md](PLAN.md)
- Architecture decisions: [docs/adr/](docs/adr/)
- Eval dataset: [evals/data/README.md](evals/data/README.md)

## Prerequisites
- .NET SDK 10 (pinned in `global.json`)
- Docker Desktop (local SQL Server; integration tests use Testcontainers)
- Edge or Chrome (only to regenerate synthetic invoices)
- Node.js 22+ (review UI, Phase 3b)

## Run locally

**1. Database.** Use an existing SQL Server (set `ConnectionStrings:InvoiceFlow` in `src/InvoiceFlow.Api/appsettings.Development.json`) or the Docker one:

```powershell
copy .env.example .env                 # set a strong MSSQL_SA_PASSWORD
docker compose up -d sql               # SQL Server on localhost:14330
dotnet user-secrets --project src/InvoiceFlow.Api set ConnectionStrings:InvoiceFlow `
  "Server=localhost,14330;Database=InvoiceFlow;User Id=sa;Password=<from .env>;TrustServerCertificate=True"
```

(User-secrets override `appsettings.Development.json`. Remove the secret to use the file's connection string.)

**2. First administrator and session key** (user-secrets, never in a committed file; see [ADR-0009](docs/adr/0009-identity-and-administration.md))

```powershell
dotnet user-secrets --project src/InvoiceFlow.Api set Bootstrap:AdminEmail    "you@company.com"
dotnet user-secrets --project src/InvoiceFlow.Api set Bootstrap:AdminPassword "<temporary password>"
dotnet user-secrets --project src/InvoiceFlow.Api set Auth:Session:SigningKey "<32+ random bytes, base64>"
dotnet user-secrets --project src/InvoiceFlow.Api set Auth:Session:MfaEncryptionKey "<exactly 32 random bytes, base64>"
```

Generate each key with `[Convert]::ToBase64String((1..32 | % { Get-Random -Max 256 }) -as [byte[]])` (PowerShell) or `openssl rand -base64 32`. Keep the MFA key stable: losing it means everyone sets up two-step sign-in again.

On first start the API creates built-in roles and this administrator (as **Super admin**). The administrator must choose a new password (12+ characters) at first sign-in, then set up **two-step sign-in** with an authenticator app (required for anyone who can administer or approve; to try the app locally without it, set `Auth:Session:RequireMfaForPrivileged` to `false` in user-secrets). Remove `Bootstrap:AdminPassword` afterwards.

**3. API**

```powershell
dotnet tool restore
dotnet run --project src/InvoiceFlow.Api --urls http://localhost:5080

# Demo without Azure: "replay" returns the stored answers for evals/data files (Development only)
$env:Extraction__Provider = "replay"; dotnet run --project src/InvoiceFlow.Api --urls http://localhost:5080
```

**4. Review UI** (Next.js, Arabic/English)

```powershell
cd web
copy .env.example .env.local           # NEXT_PUBLIC_AUTH_MODE=dev for local use
npm ci
npm run dev                            # http://localhost:3100. Pick a demo user (Clerk, Supervisor, …)
```

Upload files from `evals/data` (e.g. `syn-0023.pdf` for a changed IBAN, `syn-0036.png` for a second approval) and work them through the queue.

In Development the API migrates the database on start, runs the pipeline in-process, stores files in `.data/documents`, and uses **dev sign-in headers**:

```powershell
curl.exe -X POST http://localhost:5080/api/invoices -H "X-Dev-User: clara" -H "X-Dev-Roles: Clerk" `
  -F "file=@evals/data/syn-0001.png;type=image/png"
curl.exe http://localhost:5080/api/invoices?status=NeedsReview -H "X-Dev-User: clara" -H "X-Dev-Roles: Clerk"
```

Without Azure OpenAI settings, uploads go to review for manual entry. To enable extraction, set `Extraction:AzureOpenAI:Endpoint` and `:Deployment` (and optionally `:ApiKey`) in user-secrets.

## Test

```powershell
dotnet test InvoiceFlow.sln                     # unit + integration (Docker must be running)
# No Docker? Use an existing SQL Server instead (a temporary database is created and dropped):
#   $env:INVOICEFLOW_TEST_SQL = "Server=.;Integrated Security=True;TrustServerCertificate=True"
dotnet run --project evals/InvoiceFlow.Evals -- run --data evals/data --extractor ground-truth --expect-perfect
```

## API (all endpoints require sign-in; see [ADR-0007](docs/adr/0007-security-model.md))

| Method | Path | Policy | Purpose |
|---|---|---|---|
| GET | `/api/me` | signed in | Current user, roles, approval limit (used by the UI) |
| POST | `/api/invoices` | submit | Upload (multipart `file`). 202 + id. Processing is asynchronous |
| GET | `/api/invoices?status=NeedsReview` | read | List and review queue |
| GET | `/api/invoices/{id}` | read | Data, evidence, findings, corrections, audit |
| GET | `/api/invoices/{id}/file` | read | Original document |
| PUT | `/api/invoices/{id}/data` | review | Correct data. Rules run again immediately |
| POST | `/api/invoices/{id}/override` | override | Approve despite failing rules (reason required) |
| POST | `/api/invoices/{id}/approve` | approve | Second approval (four-eyes) |
| POST | `/api/invoices/{id}/reject` | review | Reject (reason required) |
| POST | `/api/invoices/{id}/retry-posting` | override | Retry a failed ERP posting |
| POST | `/api/invoices/validate` | review | Validate data without storing |
| GET | `/api/audit/export` | audit | Audit trail CSV (formula-injection safe) |

## Solution layout

| Project | Purpose |
|---|---|
| `src/InvoiceFlow.Domain` | Invoice document aggregate, state machine, master data types |
| `src/InvoiceFlow.Application` | Workflow, validation rules, matching, ports |
| `src/InvoiceFlow.Infrastructure` | EF Core / SQL Server, SQL job queue, file storage, mock ERP, pipeline host |
| `src/InvoiceFlow.Extraction` | AI extractors, prompts, normalization |
| `src/InvoiceFlow.Api` | REST API, auth, policies |
| `src/InvoiceFlow.Worker` | Pipeline workers (scale out by running more) |
| `web/` | Review UI: Next.js 15, React 19, TanStack Query, Tailwind, pdf.js, MSAL. Arabic/English (RTL) |
| `evals/InvoiceFlow.Evals` | Synthetic data generator, dataset checker, eval runner |
| `tests/*` | Unit and integration tests |

## Run everything in Docker

```powershell
docker compose --profile app up -d --build      # SQL, Azurite, API, worker, web → http://localhost:3100
docker compose --profile security up -d         # + ClamAV; then set MALWARE_SCAN_PROVIDER=ClamAV
```

## Deploy to Azure

See [infra/main.bicep](infra/main.bicep) and [ADR-0008](docs/adr/0008-hardening-and-operations.md). In short:

1. Create a resource group in UAE North, plus an Entra group for SQL admins and the API/SPA app registrations.
2. Push images to ACR. The web image needs build args: `API_BASE_URL=http://<prefix>-api`, `NEXT_PUBLIC_ENTRA_*`.
3. `az deployment group create -g <rg> -f infra/main.bicep -p infra/main.parameters.json`
4. Run [infra/post-deploy.sql](infra/post-deploy.sql) once, to create the database user for the managed identity.

No secrets are needed: SQL, Blob, Azure OpenAI and ACR all use the managed identity.

## Operations

| Concern | How |
|---|---|
| Health | `/health/live` (process), `/health/ready` (database) |
| Telemetry | OpenTelemetry → Application Insights (`APPLICATIONINSIGHTS_CONNECTION_STRING`) or OTLP (`OTEL_EXPORTER_OTLP_ENDPOINT`). Business metrics on meter `InvoiceFlow` |
| Malware | `Security:MalwareScan:Provider` = `ClamAV` (on-prem) · `DefenderForStorage` (Azure) · `None` (dev only). Fails closed |
| Rate limits | `RateLimits:RequestsPerMinute`, `RateLimits:UploadsPerMinute` (per user) |
| Audit | `GET /api/audit/export?from=YYYY-MM-DD&to=YYYY-MM-DD` (CSV, Auditor/FinanceAdmin/SystemAdmin) |

## Database migrations

```powershell
dotnet ef migrations add <Name> --project src/InvoiceFlow.Infrastructure --output-dir Persistence/Migrations
```

## Conventions
- Warnings are errors. Nullable is on. NuGet audit failures block the build.
- NuGet versions only in `Directory.Packages.props` (including security pins for transitive packages).
- Every validation rule gets its own class and its own tests.
- Changes to architecture need an ADR.
