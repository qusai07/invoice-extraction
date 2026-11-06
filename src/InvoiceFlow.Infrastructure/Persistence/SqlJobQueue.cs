using InvoiceFlow.Application.Workflow;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlow.Infrastructure.Persistence;

/// <summary>
/// Work queue in SQL Server (ADR-0005). Many workers can claim concurrently: <c>UPDLOCK, READPAST</c> makes each
/// worker skip rows another worker has locked. A job whose lease expired (worker crashed) is claimable again.
/// </summary>
public sealed class SqlJobQueue(InvoiceFlowDbContext db, TimeProvider time) : IJobQueue
{
    public async Task<PipelineJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var until = now + lease;

        var rows = await db.PipelineJobs.FromSqlInterpolated($"""
            WITH next AS (
                SELECT TOP (1) * FROM PipelineJobs WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE (Status = 'Pending' AND NotBefore <= {now})
                   OR (Status = 'Running' AND LockedUntil < {now})
                ORDER BY Id)
            UPDATE next
               SET Status = 'Running', LockedUntil = {until}, LockedBy = {workerId}, Attempts = Attempts + 1
            OUTPUT inserted.*;
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : new PipelineJob(rows[0].Id, rows[0].DocumentId, rows[0].Step, rows[0].Attempts);
    }

    public Task CompleteAsync(long jobId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        return db.PipelineJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobRow.Done)
            .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.CompletedAt, now), cancellationToken);
    }

    public Task FailAsync(long jobId, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken = default)
    {
        var truncated = error.Length > 4000 ? error[..4000] : error;
        return retryAt is { } at
            ? db.PipelineJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobRow.Pending)
                .SetProperty(j => j.NotBefore, at)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, truncated), cancellationToken)
            : db.PipelineJobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobRow.Failed)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, truncated)
                .SetProperty(j => j.CompletedAt, time.GetUtcNow()), cancellationToken);
    }
}
