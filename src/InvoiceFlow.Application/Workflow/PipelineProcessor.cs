using System.Diagnostics;
using InvoiceFlow.Application.Observability;
using Microsoft.Extensions.Logging;

namespace InvoiceFlow.Application.Workflow;

/// <summary>Takes one job from the queue and runs it, with retries and backoff (ADR-0005).</summary>
public sealed partial class PipelineProcessor(IJobQueue queue, InvoiceWorkflow workflow, TimeProvider time, ILogger<PipelineProcessor> logger)
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <returns>True if a job was processed (successfully or not); false if the queue was empty.</returns>
    public async Task<bool> ProcessNextAsync(string workerId, CancellationToken ct = default)
    {
        var job = await queue.ClaimAsync(workerId, Lease, ct).ConfigureAwait(false);
        if (job is null)
        {
            return false;
        }

        // One trace per job; the document id links it to the API request that created it.
        using var activity = InvoiceFlowTelemetry.Source.StartActivity($"pipeline.{job.Step.ToString().ToLowerInvariant()}", ActivityKind.Consumer);
        activity?.SetTag("invoiceflow.document.id", job.DocumentId);
        activity?.SetTag("invoiceflow.job.id", job.Id);
        activity?.SetTag("invoiceflow.job.attempt", job.Attempts);
        var started = time.GetTimestamp();
        var result = "ok";

        try
        {
            await workflow.RunStepAsync(job.DocumentId, job.Step, ct).ConfigureAwait(false);
            await queue.CompleteAsync(job.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            activity?.SetStatus(ActivityStatusCode.Error, error);
            activity?.AddException(ex);
            var final = job.Attempts >= MaxAttempts;
            result = final ? "gave-up" : "retry";
            InvoiceFlowTelemetry.StepFailures.Add(1,
                new KeyValuePair<string, object?>("step", job.Step.ToString()),
                new KeyValuePair<string, object?>("final", final));

            if (!final)
            {
                // 10s, 20s, 40s, 80s …
                var retryAt = time.GetUtcNow() + TimeSpan.FromSeconds(10 * Math.Pow(2, job.Attempts - 1));
                LogRetry(logger, ex, job.Step, job.DocumentId, job.Attempts, retryAt);
                await queue.FailAsync(job.Id, error, retryAt, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                LogGaveUp(logger, ex, job.Step, job.DocumentId, job.Attempts);
                await queue.FailAsync(job.Id, error, null, CancellationToken.None).ConfigureAwait(false);
                await workflow.GiveUpAsync(job.DocumentId, job.Step, error, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            InvoiceFlowTelemetry.StepDuration.Record(time.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("step", job.Step.ToString()),
                new KeyValuePair<string, object?>("result", result));
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Step} failed for {DocumentId} (attempt {Attempt}); retry at {RetryAt}")]
    private static partial void LogRetry(ILogger logger, Exception ex, PipelineStep step, Guid documentId, int attempt, DateTimeOffset retryAt);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Step} failed for {DocumentId} after {Attempts} attempts; giving up")]
    private static partial void LogGaveUp(ILogger logger, Exception ex, PipelineStep step, Guid documentId, int attempts);
}
