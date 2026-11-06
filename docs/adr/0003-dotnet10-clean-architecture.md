# ADR-0003: .NET 10 LTS, Clean Architecture, central package management

- Status: Accepted
- Date: 2026-11-05

## Context
PLAN.md originally said .NET 8. .NET 8 support ends in November 2026. .NET 10 is the current LTS release (supported until November 2028).

## Decision
- Target **.NET 10**. The SDK is pinned in `global.json` (`rollForward: latestFeature`).
- Layered solution:
  - `Domain`: entities, value objects, state machine. No dependencies.
  - `Application`: use cases, validation rules, matching, interfaces (`IInvoiceExtractor`, `IErpAdapter`, …).
  - `Infrastructure`: EF Core, Blob, queue, ERP adapters.
  - `Extraction`: AI providers, prompts, normalization. Implements `IInvoiceExtractor`.
  - `Api` and `Worker`: hosts.
- `Directory.Build.props`: nullable on, **warnings are errors**, analyzers at the latest level.
- `Directory.Packages.props`: all NuGet versions live in one place.

## Consequences
- Dependencies point inward only. Domain and Application can be tested without infrastructure.
- Warnings fail the build, so new code has to stay clean from day one.
