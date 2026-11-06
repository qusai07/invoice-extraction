# InvoiceFlow — manual test guide

A hands-on walkthrough of the main features, in the browser. Each test lists **who** signs in, **what** to do and **what you should see**. Tick the box when it behaves as described.

Time needed: about 45 minutes for everything, or 15 minutes for Part A and B only.

---

## 0. Setup (once)

### 0.1 Let the system "read" the sample invoices without Azure

Azure OpenAI isn't connected yet. For testing, switch on the **replay reader**: for the sample files in `evals/data` it returns the correct data instantly, as if the AI had read them. It only works in Development and is refused anywhere else.

In `src/InvoiceFlow.Api/appsettings.Development.json`, add this section (next to `"MockErp"`):

```json
"Extraction": {
  "Provider": "replay"
}
```

Then **restart the API** in Visual Studio and reload http://localhost:3100.

> Without this step, uploads still work, but every invoice goes to "Needs review" with empty fields, which is the correct behaviour when no AI is configured.

### 0.2 Create test users (as admin)

Sign in as `admin@gmail.com` → **Users → Add user**. Create these five. After each one, copy the **temporary password** shown, which appears only once. Then sign in as each user in a **private/incognito window** and set a password of your choice (12+ characters).

| Name | Email | Role | Approval limit |
|---|---|---|---|
| Clara Clerk | `clerk@test.local` | Clerk | Cannot approve |
| Sam Supervisor | `supervisor@test.local` | Supervisor | Up to an amount: **250000** |
| Fatima Finance | `finance@test.local` | FinanceAdmin | Unlimited |
| Sue Submitter | `submitter@test.local` | Submitter | Cannot approve |
| Aaron Auditor | `auditor@test.local` | Auditor | Cannot approve |

Tip: use a different browser or profile per user, so you can switch quickly.

---

## Sample invoices

All files are in `evals/data`. The expected result is fixed because the replay reader returns the true data.

| File | What it is | Total (AED) | Expected result | Rule |
|---|---|---|---|---|
| `syn-0001.png` | Clean invoice, matches its PO | 14,395.50 | **Posted** automatically | — |
| `syn-0036.png` | Clean but large | 102,963.00 | **Pending approval** (second person) | APR-001 |
| `syn-0029.pdf` | Clean, very large, Arabic, multi-page | 867,449.63 | **Pending approval**, above the supervisor's limit | APR-001 |
| `syn-0023.pdf` | Bank account (IBAN) differs from the registered one | 6,872.78 | **Needs review** | SUP-002 |
| `syn-0014.pdf` | Changed IBAN, large | 130,932.90 | **Needs review** | SUP-002 |
| `syn-0016.png` | Price ~8% above the PO price | 231,166.69 | **Needs review** | PO-003 |
| `syn-0031.png` | Bills more quantity than ordered | 10,083.41 | **Needs review** | PO-004 |
| `syn-0005.pdf` | Goods not fully received (3-way match) | 234,646.65 | **Needs review** | GRN-001 |
| `syn-0040.png` | Addressed to another company's TRN | 22,976.10 | **Needs review** | CUS-001 |
| `syn-0028.png` | No purchase order number | 9,214.80 | **Needs review** | PO-001 |
| `syn-0027.pdf` | Upload it **twice** | 6,326.25 | 2nd upload is a **Duplicate** | DUP-001 |

Registered IBAN of *Gulf Star Trading Co. LLC* (needed in test B2): `AE070330212345678901234`

---

## Part A — Sign-in & account

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| A1 | anyone | Open http://localhost:3100 without signing in | Redirected to the sign-in page | ☐ |
| A2 | — | Sign in with a wrong password | "Incorrect email or password." | ☐ |
| A3 | — | Sign in with an email that doesn't exist | **Same** message as A2 (no hint whether the account exists) | ☐ |
| A4 | — | Type the password with the keyboard on Arabic | Warning: "The password contains Arabic letters…" | ☐ |
| A5 | new user | First sign-in with the temporary password | Forced to the "Change password" page; nothing else opens | ☐ |
| A6 | new user | Try a short new password, e.g. `Qqq1234$$` | Save button stays disabled (needs 12+) | ☐ |
| A7 | any | Sign in, then reload the page (F5) | Still signed in | ☐ |
| A8 | any | Menu (top right) → Sign out | Back on the sign-in page; Back button doesn't get you in | ☐ |
| A9 | clerk | Sign in on two browsers; in one choose **Sign out on all devices** | The other browser is signed out on its next click (within ~15 s) | ☐ |
| A10 | — | Enter a wrong password 5 times for `clerk@test.local`, then the right one | Still refused (locked 15 min). Admin → Users shows **Locked**; **Unlock** fixes it | ☐ |

## Part B — Processing invoices

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| B1 | clerk | **Upload invoices** → drop `syn-0001.png` | "Received". Open it: status goes Processing → **Posted** within seconds, with an ERP reference. History tab shows every step | ☐ |
| B2 | clerk | Upload `syn-0023.pdf` → open it | **Needs review**, red check **SUP-002 IBAN changed** (expected vs found). Click the IBAN field: it's highlighted on the PDF. Replace it with the registered IBAN above → **Save & re-check** (or Ctrl+S) | ☐ |
| B2b | — | (same invoice) | Checks pass → **Posted**. History → *Corrections* shows `iban` old → new by Clara | ☐ |
| B3 | clerk | Upload `syn-0031.png` | Needs review, **PO-004 Qty over PO**. The line table shows the matching PO line next to each row | ☐ |
| B4 | clerk | Upload `syn-0028.png` → **Reject…** → type "no" | Confirm stays disabled until the reason has 10+ characters. Enter a real reason → **Rejected** | ☐ |
| B5 | clerk | Upload `syn-0027.pdf`, then the same file again | The second one is a **Duplicate** immediately (not read again) | ☐ |
| B6 | clerk | Upload any `.txt` file renamed to `.pdf` | Refused: not a PDF/PNG/JPEG (the system checks the file content, not the name) | ☐ |
| B7 | clerk | Upload `syn-0040.png` | Needs review, **CUS-001 Wrong customer** | ☐ |
| B8 | clerk | Open an invoice in **Pending approval** | No *Approve* button (Clerk can't approve) | ☐ |

## Part C — Approvals, four-eyes and limits

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| C1 | clerk | Upload `syn-0036.png` (102,963) | **Pending approval** | ☐ |
| C2 | supervisor | Dashboard | Card "Waiting for your approval: 1". Open → **Approve** → **Posted**, "Approved by: Sam" | ☐ |
| C3 | clerk | Upload `syn-0029.pdf` (867,449.63) | **Pending approval** | ☐ |
| C4 | supervisor | Open it → **Approve** | Refused: "above your approval limit (250,000.00)". The dashboard card doesn't count it | ☐ |
| C5 | finance | Open it → **Approve** | Works (unlimited) → **Posted** | ☐ |
| C6 | supervisor | Upload `syn-0014.pdf` → **Override…** with a reason | Goes to **Pending approval** (not straight to approved: it's above 100,000) | ☐ |
| C7 | supervisor | Try to **Approve** the same invoice | Refused: four-eyes, you overrode it, someone else must approve | ☐ |
| C8 | finance | Approve it | Works → **Posted**. Audit log shows *Overridden* by Sam with the reason | ☐ |

## Part D — Roles & visibility

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| D1 | submitter | Look at the sidebar | Only *Dashboard*, *Upload invoices*, *Invoices*. No admin section | ☐ |
| D2 | submitter | Upload `syn-0025.png`, then open **Invoices** | Sees **only own uploads**, not the clerk's | ☐ |
| D3 | submitter | Open the address of an invoice uploaded by the clerk | "Not found" (other people's invoices aren't revealed) | ☐ |
| D4 | auditor | Open any invoice | Can read everything; **no** action buttons; inputs are read-only | ☐ |
| D5 | auditor | Audit log → **Export CSV** | A CSV downloads; opens in Excel | ☐ |
| D6 | auditor | Validation rules | Visible but read-only ("You can view this page but not change it") | ☐ |

## Part E — Administration

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| E1 | admin | Users → edit **yourself** | Roles, limit and active are locked ("You can't change your own…") | ☐ |
| E2 | admin | Roles → **SystemAdmin** → untick *Manage users* → Save | Refused: you can't change a role you have yourself | ☐ |
| E3 | admin | Users → deactivate `clerk@test.local` (while the clerk is signed in elsewhere) | The clerk is thrown out on the next click (within ~15 s) and can't sign in | ☐ |
| E4 | admin | Roles → **New role** "Read only" with *See all invoices* → Users → give it to the submitter | The submitter is **signed out** (role changes end existing sessions). After signing in again they see all invoices | ☐ |
| E5 | admin | Validation rules → **Price tolerance (%) = 10** → Save, reason "test: 10% tolerance" | Saved as version 1 in *Change history* | ☐ |
| E6 | clerk | Upload `syn-0016.png` (price +8%) | Now passes the price check → **Pending approval** (it's 231k). Set tolerance back to 0.5 afterwards (version 2) | ☐ |
| E7 | admin | Validation rules → try to switch off **SUP-002** | Not possible: marked *Always on* (fraud protection) | ☐ |
| E8 | admin | Audit log → *Sign-ins & admin changes* | Every sign-in, failed attempt, lockout, user and role change from this session, with who and when | ☐ |
| E9 | admin | System health | Background job counts; no failed jobs | ☐ |

## Part F — Language

| # | Who | Do | Expect | ✓ |
|---|---|---|---|---|
| F1 | any | Click **عربي** (top right) | Whole layout mirrors right-to-left; menus, checks and messages in Arabic; amounts, TRNs and IBANs stay left-to-right | ☐ |
| F2 | clerk | Open `syn-0029.pdf` in Arabic | Arabic invoice, Arabic-Indic digits on the PDF; fields show normal digits | ☐ |

---

## If something doesn't match

Note the test number, the user, and what you saw. A screenshot helps most. The **Audit log** and **System health** pages usually show why an invoice ended where it did.

To start again with a clean database, ask before deleting anything. Test data lives in your local `InvoiceFlow` database.
