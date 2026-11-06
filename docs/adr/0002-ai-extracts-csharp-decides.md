# ADR-0002: AI only extracts, C# decides

- Status: Accepted
- Date: 2026-11-05

## Context
Language models read invoices well but can produce confident wrong values. Wrong data posted to an ERP means paying the wrong amount, paying twice, or paying a fraudster. Finance and auditors need decisions that can be explained and repeated.

## Decision
- The model's **only** job is to turn a document into an `InvoiceDto` that matches a strict JSON schema, plus evidence (page and source text for each field).
- The model has **no tools and no actions**. Document content is treated as data, never as instructions.
- **Every** accept, route or reject decision is made by deterministic C# rules (`IValidationRule`) that are unit-tested and versioned.
- Business logic (totals, VAT, PO match) is **not** put in prompts.
- The model's self-reported confidence is not used on its own to auto-approve.

## Consequences
- Prompt injection inside a document cannot cause an action. The worst case is wrong extracted values, which the rules catch or send to review.
- Rules can be tested without calling the model. The eval set measures extraction separately from the decisions.
- More C# code to write and maintain. That's acceptable, because this is where the product's value is.
