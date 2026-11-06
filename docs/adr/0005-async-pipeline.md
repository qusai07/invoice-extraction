# ADR-0005: Asynchronous pipeline with a SQL Server job queue

- Status: Accepted (2026-11-05). Transport: SQL Server table. Replaces the earlier "transport open" draft.
- Date: 2026-11-05

## Context
Extraction takes seconds to minutes, AI providers have rate limits and outages, and ERPs are slow or unavailable at times. A synchronous request cannot hold all of this reliably. Expected volume is thousands of invoices per day, not millions. Customers include government entities that may require on-prem hosting.

## Decision
- Pipeline steps (extract → validate → post) run in background workers (`InvoiceFlow.Worker`, or inside the API when `Pipeline:RunInProcess=true` for development and small installs).
- **Queue = the `PipelineJobs` table in the same SQL Server database.**
  - Jobs are written **in the same transaction** as the status change that creates them (`IInvoiceStore.SaveAsync`). This is the outbox pattern without a separate dispatcher: a status change and its follow-up work can never get out of sync.
  - Workers claim with `UPDATE … WITH (ROWLOCK, UPDLOCK, READPAST) … OUTPUT`. Concurrent workers skip each other's rows, and each job goes to exactly one worker (integration-tested with 8 concurrent workers).
  - A **lease** (`LockedUntil`): a job whose worker crashed becomes claimable again when the lease expires.
  - Retries with exponential backoff (10 s, 20 s, 40 s, 80 s). After 5 attempts the job is marked failed and the document moves to `NeedsReview` (extract/validate) or `PostingFailed` (post), where a person can see it.
- Every step is **idempotent**: it checks the document's status first and does nothing if the step already happened. ERP posting uses a stable idempotency key (`tenant:supplier:invoiceNumber`).
- The queue sits behind `IJobQueue`, so Service Bus or RabbitMQ can replace it without touching the workflow.

## Alternatives considered
- **Azure Service Bus:** best for the cloud, but needs a separate outbox to stay consistent with the database, and isn't available on-prem.
- **RabbitMQ:** works on-prem, but is another server to run and also needs an outbox.
- **MassTransit / similar:** convenient, but recent versions are commercially licensed.

## Consequences
- No extra infrastructure. The same setup works in the cloud and on-prem. Backup and restore covers the queue.
- Polling adds about 1 second of latency per step. That's irrelevant for invoices.
- Throughput is limited by the database. If one day we need more than about 50 jobs per second, move to Service Bus through `IJobQueue`.
