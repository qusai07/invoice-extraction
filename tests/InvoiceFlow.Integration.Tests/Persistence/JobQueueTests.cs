using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlow.Integration.Tests.Persistence;

[Collection(ApiCollection.Name)]
public class JobQueueTests(InvoiceFlowFactory factory)
{
    // Unique step marker per test run so tests don't consume each other's jobs: use document ids we create here.
    private async Task<HashSet<Guid>> EnqueueAsync(int count)
    {
        // Other tests in the collection may leave real jobs queued; process them first so the queue holds only ours.
        await factory.DrainPipelineAsync();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>();
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToHashSet();
        db.PipelineJobs.AddRange(ids.Select(id => new JobRow
        {
            DocumentId = id, Step = PipelineStep.Validate, Status = JobRow.Pending,
            NotBefore = DateTimeOffset.UtcNow.AddSeconds(-1), CreatedAt = DateTimeOffset.UtcNow,
        }));
        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<PipelineJob?> ClaimOneOfAsync(HashSet<Guid> ours, string worker, TimeSpan lease)
    {
        // Other tests may have jobs in the queue; skip (and release) those that aren't ours.
        for (var i = 0; i < 500; i++)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var job = await queue.ClaimAsync(worker, lease);
            if (job is null)
            {
                return null;
            }

            if (ours.Contains(job.DocumentId))
            {
                return job;
            }

            await queue.FailAsync(job.Id, "not this test's job", DateTimeOffset.UtcNow);
        }

        return null;
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_job()
    {
        var ours = await EnqueueAsync(20);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async w =>
        {
            var mine = new List<PipelineJob>();
            while (await ClaimOneOfAsync(ours, $"w{w}", TimeSpan.FromMinutes(5)) is { } job)
            {
                mine.Add(job);
            }

            return mine;
        }));

        var all = claims.SelectMany(c => c).ToList();
        Assert.Equal(20, all.Count);
        Assert.Equal(20, all.Select(j => j.Id).Distinct().Count());
        Assert.Equal(ours, all.Select(j => j.DocumentId).ToHashSet());
    }

    [Fact]
    public async Task Job_with_expired_lease_is_claimed_again()
    {
        var ours = await EnqueueAsync(1);

        // Worker A claims and "crashes": its lease is already expired.
        var first = await ClaimOneOfAsync(ours, "crashed-worker", TimeSpan.FromSeconds(-1));
        Assert.NotNull(first);

        var second = await ClaimOneOfAsync(ours, "healthy-worker", TimeSpan.FromMinutes(5));
        Assert.NotNull(second);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Attempts);

        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IJobQueue>().CompleteAsync(second.Id);
        var row = await scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>().PipelineJobs.AsNoTracking().SingleAsync(j => j.Id == second.Id);
        Assert.Equal(JobRow.Done, row.Status);
    }
}
