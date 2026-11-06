# ADR-0006: Validation engine — facts first, pure rules, conservative matching

- Status: Accepted
- Date: 2026-11-05

## Context
ADR-0002 says C# decides. The rules need to be easy to test, easy to explain to finance and auditors, and safe when the AI misreads something. Some mistakes cost money (paying the wrong amount or to the wrong account). Others only cost time (sending a good invoice to a person).

## Decision
1. **Facts first.** `ValidationEngine` does all lookups once (supplier, PO, line matches, duplicate history) and builds an immutable `ValidationContext`. **Rules are pure functions** of that context: no I/O, no clock, no randomness. Each rule is one class with a stable code (`PO-003`) and messages in English and Arabic.
2. **Decision order:** any `DUP-*` error → `Duplicate`. Any other error → `NeedsReview`. Otherwise, `APR-001` → `PendingApproval`. Otherwise → `Approved`. Warnings and info never block.
3. **Supplier identity:** TRN exact match first. A name match (EN/AR, aliases, legal forms ignored) is used only if it is clear (score ≥ 0.6 and ≥ 0.15 ahead of the next). If the name matches but the TRN differs, that is an error (`SUP-003`).
4. **Line matching is conservative.** One-to-one, greedy by score:
   - SKU equal → strong match.
   - Otherwise the PO description must appear in the invoice line (containment, EN or AR). **Price and quantity alone can never reach the threshold.**
   - Quantity fit breaks ties between repeated items.
   - An unmatched line is an error (`PO-002`) with suggestions for the reviewer.
5. **Asymmetric tolerances.** Price above PO beyond tolerance → error. Below PO → warning. Over-invoicing quantity → error.
6. **Ruleset is versioned** (`ValidationReport.RulesetVersion`) and stored with every decision.

## Consequences
- Every rule is unit-tested in isolation by breaking one thing in a clean invoice (`ValidationRuleTests`).
- The synthetic generator plants business scenarios with the rule expected to catch each one. Tests check 150 generated invoices, and CI runs `--expect-perfect`. If a rule change shifts a decision, the build fails.
- **Known limit:** rules cannot detect a misread **invoice number or date**. No other field cross-checks them. These rely on extraction accuracy, which the eval measures, and on duplicate detection. All other critical fields are cross-checked by arithmetic, PO data or master data.
- Greedy matching is not globally optimal. If real data shows mismatches, replace it with an optimal assignment (Hungarian algorithm). The interface stays the same.
