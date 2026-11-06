# Golden dataset

Each invoice document has a hand-checked answer file next to it:

```
evals/data/
  inv-0001.pdf
  inv-0001.expected.json
  inv-0002.jpg
  inv-0002.expected.json
```

Check the dataset at any time (CI runs this too):

```
dotnet run --project evals/InvoiceFlow.Evals -- check --data evals/data
```

The checker fails if a document has no answer file (or the reverse), the JSON is invalid, a required field is missing, or the numbers you typed don't add up (qty × price, Σ lines = subtotal, subtotal + VAT = total).

## Synthetic invoices (`syn-*`) and `master-data.json`

`syn-0001` … `syn-0040` are generated, not real. They cover English, bilingual and Arabic layouts, Arabic-Indic digits, multi-page tables, zero-rated VAT, PNG images, 3-way-matched suppliers, and prompt-injection text.

`master-data.json` is the matching ERP snapshot: suppliers with TRN and IBAN, and one PO per invoice. Each answer file has a `validation` block with the **planted business scenario** and the decision the rules must reach:

| Scenario | What's planted | Expected |
|---|---|---|
| `clean` | invoice matches its PO | `Approved` (or `PendingApproval` ≥ AED 100,000) |
| `no-po` | no PO number printed | `NeedsReview` · PO-001 |
| `unknown-po` | PO number not in the ERP | `NeedsReview` · PO-001 |
| `price-increase` | one line ~8% above PO price | `NeedsReview` · PO-003 |
| `qty-exceeds` | one line bills more than ordered | `NeedsReview` · PO-004 (+ GRN-001 for 3-way) |
| `not-received` | 3-way supplier, goods not fully received | `NeedsReview` · GRN-001 |
| `iban-changed` | IBAN on invoice ≠ registered IBAN | `NeedsReview` · SUP-002 |
| `wrong-customer` | invoice addressed to another company's TRN | `NeedsReview` · CUS-001 |

Regenerate everything (documents, answers, master data) with:

```
dotnet run --project evals/InvoiceFlow.Evals -- generate --out evals/data --count 40 --seed 42
```

This needs Edge or Chrome installed. The same seed produces the same answer files. Don't hand-edit `syn-*` files: change the generator instead.

## Running an eval

```
dotnet run --project evals/InvoiceFlow.Evals -- run --data evals/data                         # Azure OpenAI
dotnet run --project evals/InvoiceFlow.Evals -- run --data evals/data --extractor ground-truth --expect-perfect  # self-test
```

The report has two parts:
- **Business outcome:** false accepts (the number that matters: let through but should have stopped, or posting wrong data), straight-through rate, decision accuracy, and whether each planted problem was caught by the intended rule.
- **Extraction:** per-field accuracy, line precision/recall, invented values, cost, latency.

Reports are written to `evals/reports/<timestamp>-<extractor>.md|.json`.

## Rules for adding invoices

1. **Anonymize real invoices** before committing: replace company names, TRNs, IBANs, addresses and personal names. Never commit raw customer documents.
2. Name real files `inv-NNNN.<ext>`. Use the next free number. (`syn-` is reserved for generated files.)
3. Write the values **exactly as the correct business answer**, not as the model would read them: dates as `yyyy-MM-dd`, amounts as JSON numbers, Latin digits (convert Arabic-Indic digits).
4. If a field is not on the document, use `null`. Don't guess.
5. Fill in `meta` so the eval report can break down accuracy by language, quality and layout.

## Format

See [`../schema/example.expected.json`](../schema/example.expected.json).

| Field | Notes |
|---|---|
| `meta.source` | `real-anonymized` · `synthetic` |
| `meta.language` | `en` · `ar` · `ar-en` |
| `meta.quality` | `digital-pdf` · `image` (clean rendered image) · `scanned-pdf` · `photo` · `poor-photo` |
| `meta.tags` | free list, e.g. `multi-page`, `handwritten`, `stamp`, `multi-invoice`, `credit-note` |
| `alternatives` | optional. Other answers that also count as correct, e.g. `{ "supplierName": ["<Arabic name>"] }` when both names are printed |
| `invoice.*` | required: `supplierName`, `invoiceNumber`, `invoiceDate`, `currency`, `total`, `lines` |
| `invoice.supplierTrn` / `customerTrn` | 15 digits (UAE), as string |
| `invoice.lines[].vatRate` | decimal fraction: `0.05`, `0`, or `null` for exempt |

## Coverage targets (Phase 0 → Phase 1)

| Bucket | Target count |
|---|---|
| English, digital PDF | 30 |
| Arabic or bilingual | 25 |
| Scanned PDF | 20 |
| Phone photos (incl. poor quality) | 15 |
| Multi-page / multi-invoice / edge cases | 10 |
| **Total** | **100** |
