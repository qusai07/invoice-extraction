using InvoiceFlow.Application.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Infrastructure.Pipeline;

public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>Run the pipeline inside the API process (development / small installs). Production uses InvoiceFlow.Worker.</summary>
    public bool RunInProcess { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Jobs processed in parallel by this process.</summary>
    public int Concurrency { get; set; } = 2;
}

/// <summary>Polls the job queue and runs pipeline steps. Each job gets its own DI scope (own DbContext).</summary>
public sealed partial class PipelineHostedService(
    IServiceScopeFactory scopes,
    IOptions<PipelineOptions> options,
    ILogger<PipelineHostedService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(1, Math.Max(1, options.Value.Concurrency))
            .Select(i => RunLoopAsync($"{Environment.MachineName}:{Environment.ProcessId}:{i}", stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task RunLoopAsync(string workerId, CancellationToken stoppingToken)
    {
        LogStarted(logger, workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<PipelineProcessor>();
                processed = await processor.ProcessNextAsync(workerId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database unavailable etc. Keep the loop alive and back off.
                LogLoopError(logger, ex, workerId);
                processed = false;
            }

            if (!processed)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pipeline worker {WorkerId} started")]
    private static partial void LogStarted(ILogger logger, string workerId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pipeline worker {WorkerId} loop error")]
    private static partial void LogLoopError(ILogger logger, Exception ex, string workerId);
}
