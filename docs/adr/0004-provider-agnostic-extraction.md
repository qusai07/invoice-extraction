# ADR-0004: Provider-agnostic extraction behind `IInvoiceExtractor`

- Status: Accepted (2026-11-05: Azure OpenAI confirmed as primary)
- Date: 2026-11-05

## Context
Model quality and prices change every few months. Customers may require data to stay in the UAE. We want to compare providers on *our* invoices, not on benchmarks.

## Decision
- All extraction goes through `IInvoiceExtractor` (Application layer). Its output is `InvoiceDto` + field evidence + usage (tokens, cost, latency, model, prompt version).
- **Proposed primary**: Azure OpenAI / Foundry vision model with structured outputs, deployed in UAE North.
- **Baseline / fallback**: Azure AI Document Intelligence `prebuilt-invoice`.
- Other providers (e.g. Claude API) can be added as more implementations and compared on the eval set.
- The choice of provider is configuration per tenant, not code.

### Implementation notes (v1)
- The model **transcribes values as printed** (strings + page number). C# normalizes them: Arabic-Indic digits, separators, dates, currency (`RawInvoiceMapper`). A value that can't be parsed becomes `null` plus an issue. It is never guessed.
- Strict JSON schema (`invoice-extraction.v1.schema.json`) + prompt (`extract-invoice.v1.md`) are embedded in the assembly and versioned together (`ExtractionContract.Version`).
- PDF pages are rendered to PNG with PDFium (`PDFtoImage`, MIT) and sent as images. This works the same for digital PDFs, scans and photos.

## Consequences
- The eval runner can A/B test any extractor on the same dataset (accuracy, cost, latency).
- Each extraction stores model and prompt version for audit and reproducibility.
- Small extra work to map each provider's output into `InvoiceDto`.
