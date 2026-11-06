# ADR-0007: Authentication, roles and four-eyes rules

- Status: Accepted
- Date: 2026-11-05

## Decision

### Authentication
- **Production:** Microsoft Entra ID. The API accepts JWT bearer tokens (`Auth:EntraId:TenantId`, `:Audience`). Roles come from the token's `roles` claim, defined as **app roles** on the API app registration.
- **Development and tests:** `Auth:Mode=Dev`. Headers `X-Dev-User` and `X-Dev-Roles` sign you in. The API **refuses to start** in this mode outside the `Development` and `Testing` environments.
- **Default deny:** every endpoint requires a signed-in user unless explicitly marked anonymous. Today only `/health` and the dev OpenAPI document are anonymous.
- Bearer tokens only, no cookies, so the API isn't exposed to CSRF. Antiforgery is disabled on the upload endpoint for that reason.

### Roles (app roles) → API policies

| Policy | Roles | Endpoints |
|---|---|---|
| `invoices.read` | all roles | list, details, file |
| `invoices.submit` | Submitter, Clerk, Supervisor | upload |
| `invoices.review` | Clerk, Supervisor | correct data, reject, validate |
| `invoices.override` | Supervisor | override failing rules, retry posting |
| `invoices.approve` | Supervisor, FinanceAdmin | second approval |

- Users who only have **Submitter** see only their own uploads. Other documents return **404**, not 403, so the API doesn't reveal that they exist.

### Business rules for people (enforced in `InvoiceWorkflow`, not only in the UI)
- **Four eyes:** the person who corrected or overrode an invoice (`ResolvedBy`) cannot be its second approver.
- An override **above the approval limit** goes to `PendingApproval`, never straight to `Approved`.
- **Duplicates cannot be overridden.**
- Override and reject require a **reason** of at least 10 characters. It's stored in the audit trail.
- Every status change, correction and override is an **immutable audit event** with actor, time and details. Field-level corrections are stored separately and feed the eval dataset.

## Consequences
- Roles are managed in Entra ID, not in our database. Assigning users to roles is a tenant admin task.
- Integration tests cover 401 for anonymous users, 403 for wrong roles, 404 for other people's documents, and the four-eyes violation.
- Still to do (Phase 4 hardening): per-tenant data isolation, rate limiting, malware scan on upload, and audit export.
