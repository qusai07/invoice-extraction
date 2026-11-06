# ADR-0009: Users, sign-in, roles & permissions, administration

- Status: **Proposed** (decisions from 2026-11-05: both sign-in types, built-in + custom roles, full admin area, per-user approval limits)
- Supersedes parts of: ADR-0007 (roles move from Entra app roles to InvoiceFlow's own database)

## Context
Customers differ: some have Microsoft Entra ID (Microsoft 365), some don't. Finance teams need roles they can adjust, approval authority per person, and an admin area. Today roles come from Entra app roles and settings live in config files, so nothing can be managed inside the product.

## Decision

### 1. InvoiceFlow owns users and sessions; Entra ID is one way to prove who you are
- **Users table** in our database. Identity sources: `Local` (email + password), `Entra` (single sign-on), or both linked to one user.
- After **any** successful sign-in, the API issues **its own session**:
  - an access token (JWT, **15 min**, held in memory by the web app, never in localStorage);
  - a refresh token (**httpOnly, Secure, SameSite=Strict cookie**). It rotates on every use, is stored only as a hash, and is revocable. Reuse of an old token revokes the whole session family (theft detection).
- So roles, permissions, approval limits, deactivation and "log out everywhere" work the same for both sign-in types.
- **Every user has a security stamp.** Deactivating a user, changing their roles, resetting their password or logging them out everywhere changes the stamp, and the API rejects access tokens with an old stamp (cached briefly). Revocation takes effect within seconds, without waiting 15 minutes.

### 2. Local accounts
- Passwords use ASP.NET Core Identity's `PasswordHasher` (PBKDF2-SHA512, 100k+ iterations). The minimum is **12 characters**, with no forced complexity rules (NIST 800-63B). Breached-password checking is optional (k-anonymity API).
- **Lockout** for 15 minutes after 5 failed attempts. Login answers never reveal whether an email exists.
- **MFA:** authenticator app (TOTP, RFC 6238) with one-time recovery codes. **Mandatory for anyone with admin or approve permissions**, optional for others. *Implemented 2026-11-06 with one change:* the TOTP secret is encrypted with **AES-256-GCM and a configured key** (`Auth:Session:MfaEncryptionKey`, from Key Vault / user-secrets; the user id is bound as associated data), not ASP.NET Data Protection. Data Protection's key ring is machine-local by default: after a container restart or on a second replica the secrets could no longer be read, locking every admin out. A configured key behaves like the session signing key and needs no extra storage. Sign-in is two steps: the password returns a 5-minute challenge token (own audience, bound to the security stamp, never usable as an access token), then the code (or a recovery code) starts the session. Codes are single-use (last time step stored), wrong codes count toward lockout, recovery codes are stored as SHA-256 hashes. Until a required user turns it on, they get no permissions (like "must change password").
- **Invite and password reset** by email link: a single-use token valid for 24 hours (invite) or 1 hour (reset), stored hashed. Without email configured, an admin can copy the link and send it another way.

### 3. Entra ID single sign-on
- The web app signs in with MSAL and sends the **Entra ID token** to `POST /api/auth/sso/entra`. The API validates it (issuer = configured tenant, audience = our app), finds the user by `oid` (or email on first link), and issues our session.
- **No silent sign-up by default:** an admin invites the person first. Optionally, auto-provisioning for an allowed domain with a default role (off by default).
- MFA for SSO users is enforced by the customer's Entra Conditional Access, not by us.

### 4. Permissions, not role names
- The API authorizes by **permission** (`[RequirePermission("invoice.approve")]`), and roles are named sets of permissions.

| Permission | Meaning |
|---|---|
| `invoice.read.own` / `invoice.read.all` | See own uploads / all invoices |
| `invoice.submit` | Upload |
| `invoice.correct`, `invoice.reject` | Review work |
| `invoice.override` | Accept despite failing rules |
| `invoice.approve` | Second approval (up to the user's approval limit) |
| `invoice.retry-posting` | Retry ERP posting |
| `audit.read`, `audit.export` | Audit viewer / CSV |
| `admin.users`, `admin.roles`, `admin.rules`, `admin.suppliers`, `admin.system` | Administration areas |

- **Built-in roles** (Submitter, Clerk, Supervisor, FinanceAdmin, Auditor, SystemAdmin) are seeded and can be edited but not deleted. Admins can create **custom roles** from the permission checklist.
- **Guard rails:**
  - the last user with `admin.users` cannot be removed or deactivated;
  - admins cannot change their own roles or approval limit;
  - every admin change goes to an **admin audit log** (who, what, before → after, reason).

### 5. Approval authority per user
- Each user with `invoice.approve` has an **approval limit** (AED; empty = cannot approve, "unlimited" allowed). An invoice can only be approved by someone whose limit covers its total, **in addition to** the four-eyes rule. The queue shows each approver only what they're allowed to approve.

### 6. Administration area (web `/admin`)
- **Users & roles:** list and search, invite or create, deactivate, assign roles, approval limit, reset password or MFA, log out everywhere, last login and sign-in method. Roles editor with the permission checklist.
- **Validation rules:**
  - turn rules on or off, and change severity between Error and Warning;
  - approval threshold, tolerances, allowed VAT rates, company TRNs.
  - Stored as **versioned settings** (each change has author, time and reason). Every validation report records the settings version it used.
  - **Locked rules** (can't be disabled): DUP-001/002, SUP-002 (IBAN), SUP-003, CUS-001, ARI-003.
- **Suppliers & tolerances:** master data from the ERP (read-only), plus local overrides: per-supplier tolerance and 3-way match. **IBAN changes need two admins** (one proposes, a different one approves), which closes the classic fraud gap.
- **Audit & system:** audit viewer (invoice and admin events, filters, CSV), AI usage and cost per day, queue health (pending and failed jobs, retry), and active sessions.

## Consequences
- More security-sensitive code (passwords, tokens, MFA) is now ours. Mitigations: proven primitives only (ASP.NET Core Identity's hasher, Data Protection, standard JWT), tests for every attack path (lockout, token reuse, stamp revocation, enumeration), and a security review before the pilot.
- Entra app roles are no longer used. The Entra registration only needs sign-in.
- Email delivery (SMTP or Azure Communication Services) becomes a dependency for invites and resets, with a manual-link fallback.
