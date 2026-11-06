# ADR-0001: Record architecture decisions

- Status: Accepted
- Date: 2026-11-05

## Context
This is meant to become a production product, so the team will need to know later *why* things were built a certain way: auditors, new developers, and future us.

## Decision
We record significant decisions as short ADRs in `docs/adr/`, numbered, using this format: Context → Decision → Consequences. An ADR is never edited after it's accepted. A new ADR supersedes it instead.

Statuses: `Proposed` · `Accepted` · `Superseded by ADR-XXXX` · `Rejected`.

## Consequences
- A small cost for each decision, and a history that can be searched.
- PRs that change architecture must add or update an ADR (see Definition of Done in PLAN.md §19).
