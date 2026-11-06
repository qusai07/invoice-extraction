# ADR-0008: Upload safety, abuse limits, observability and deployment

- Status: Accepted
- Date: 2026-11-05

## Upload safety
- **The file type comes from the bytes, never the header or extension** (`FileSignature`). PDF, PNG and JPEG signatures must be at offset 0, which rejects polyglot files. The stored file name is display-only, sanitized and never part of a storage path.
- **Malware scanning before storage**, through `IMalwareScanner`, chosen by `Security:MalwareScan:Provider`:
  - `ClamAV`: inline scan through clamd's `INSTREAM`. For on-prem installs.
  - `DefenderForStorage` (Azure): Microsoft Defender for Storage scans each blob on upload and writes a verdict tag. `AzureBlobDocumentStore` **refuses to read a blob until the verdict is "No threats found"**. A pending verdict is a transient error, so the pipeline retries with backoff. A malicious verdict blocks the file permanently, for the pipeline and for download alike.
  - `None`: development only, and must be set explicitly anywhere else. Startup fails if the provider isn't set outside Development.
- **Fail closed:** if the scanner is unreachable, times out or gives an unexpected reply, the upload is refused (503). It is never accepted unscanned.
- Size limits are enforced at Kestrel (25 MB per request), at the multipart parser (20 MB) and at the endpoint.

## Abuse limits
- A rate limit per signed-in user (per IP when anonymous), on all endpoints (`RateLimits:RequestsPerMinute`, default 600). Uploads have their own token bucket (`RateLimits:UploadsPerMinute`, default 60). Responses are 429 with `Retry-After`.
- The API sends `nosniff`, `X-Frame-Options: DENY`, `default-src 'none'`, `no-store` and no `Server` header, plus HSTS outside Development. Behind Container Apps ingress, forwarded headers are trusted (`ForwardedHeaders:Enabled`).

## Audit export
- `GET /api/audit/export?from&to`: Auditor, FinanceAdmin and SystemAdmin only. Streams CSV, with the range capped at 366 days.
- **Protection against CSV formula injection:** cells starting with `= + - @` (file names, reasons and invoice numbers all come from outside) are prefixed with `'`, so Excel shows them as text instead of running them.

## Observability (OpenTelemetry)
- Traces cover the API requests, one span per pipeline job (with the document id), and outbound HTTP calls (Azure OpenAI, ERP).
- **Business metrics** (`InvoiceFlow` meter): received, rejected uploads (by reason), decisions (outcome, made by system or human), reviewer actions, AI cost, step duration, step failures. The PLAN §1 KPIs come straight from these. For example, touchless rate = `decisions{outcome=Approved,by=system} / received`.
- Exporters: `APPLICATIONINSIGHTS_CONNECTION_STRING` sends to Azure Monitor; `OTEL_EXPORTER_OTLP_ENDPOINT` sends to any OTLP backend; with neither, nothing is exported.
- Health: `/health/live` (process running) and `/health/ready` (database reachable), used by the container probes.
- SQL client tracing is not included yet: its OpenTelemetry instrumentation is still prerelease.

## Deployment
- Docker images run as non-root on official Microsoft and Node base images. `appsettings.Development.json` is removed from images.
- **Azure (Bicep, `infra/main.bicep`)**, UAE North by default:
  - Container Apps for web (external), API (internal only) and worker.
  - Azure SQL with Entra-only authentication (no passwords).
  - Storage with RBAC only (shared keys disabled), soft delete and versioning.
  - Defender for Storage malware scanning.
  - Azure OpenAI with local auth disabled.
  - Container Registry without an admin user.
  - Log Analytics and Application Insights.
  - **One user-assigned managed identity, so there are no secrets in configuration.**
- Run `infra/post-deploy.sql` once to create the database user for the identity.

## Not yet (tracked)
- Private endpoints and VNet integration (SQL, Storage, OpenAI) instead of public endpoints with RBAC and firewall.
- Per-tenant data isolation (single tenant today).
- Running migrations from the release pipeline instead of at API start-up, and removing `db_ddladmin` afterwards.
- Least-privilege custom role for blob tags, instead of Storage Blob Data Owner on the container.
