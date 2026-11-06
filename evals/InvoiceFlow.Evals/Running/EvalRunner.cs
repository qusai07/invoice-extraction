using System.Text.Json;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Evals.Dataset;
using InvoiceFlow.Evals.Scoring;

namespace InvoiceFlow.Evals.Running;

public static class EvalRunner
{
    public static async Task<EvalReport> RunAsync(
        IInvoiceExtractor extractor,
        IReadOnlyList<DatasetItem> items,
        int parallelism,
        TextWriter progress,
        CancellationToken cancellationToken,
        ValidationEngine? validation = null)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var scores = new DocumentScore[items.Count];
        var done = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, items.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var item = items[i];
                ExtractionResult result;
                try
                {
                    result = await extractor.ExtractAsync(DocumentInput.FromFile(item.DocumentPath), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Extractors shouldn't throw, but one bad document must not kill the whole run.
                    result = ExtractionResult.Fail($"{ex.GetType().Name}: {ex.Message}", new ExtractionUsage { Extractor = extractor.Name });
                }

                // Same path as production: extracted data (not the truth) goes through the rules.
                ValidationReport? report = null;
                if (validation is not null && result.Status != ExtractionStatus.Failed && result.Invoice is not null)
                {
                    report = await validation.ValidateAsync(new ValidationRequest(result.Invoice, result), ct).ConfigureAwait(false);
                }

                scores[i] = InvoiceScorer.Score(item, result, report);
                var n = Interlocked.Increment(ref done);
                var mark = scores[i].FullyCorrect ? "✓" : scores[i].Status == ExtractionStatus.Succeeded ? "~" : "✗";
                var decision = scores[i].PredictedOutcome is { } o ? $"  → {o}{(scores[i].FalseAccept ? "  FALSE ACCEPT" : "")}" : "";
                lock (progress)
                {
                    progress.WriteLine($"[{n,3}/{items.Count}] {mark} {item.Id}  ({scores[i].Usage.Duration.TotalSeconds:0.0}s){decision}");
                }
            }).ConfigureAwait(false);

        return new EvalReport(extractor.Name, startedAt, scores);
    }

    /// <summary>Writes &lt;timestamp&gt;-&lt;extractor&gt;.md (human) and .json (machine, for trend tracking).</summary>
    public static (string Markdown, string Json) Save(EvalReport report, string reportsDir)
    {
        Directory.CreateDirectory(reportsDir);
        var stem = Path.Combine(reportsDir, $"{report.StartedAt:yyyyMMdd-HHmmss}-{report.Extractor}");

        File.WriteAllText(stem + ".md", report.ToMarkdown());

        var summary = new
        {
            report.Extractor,
            report.StartedAt,
            report.Count,
            report.Succeeded,
            report.FullyCorrectRate,
            report.HeaderAccuracy,
            report.LinePrecision,
            report.LineRecall,
            report.Hallucinations,
            report.TotalCostUsd,
            report.DecisionCount,
            report.DecisionAccuracy,
            report.StraightThroughRate,
            report.FalseAccepts,
            report.FalseAcceptRate,
            report.FalseRejects,
            report.CaughtForRightReasonRate,
            Fields = InvoiceScorer.HeaderFields.ToDictionary(f => f, report.FieldAccuracy),
            report.Documents,
        };
        File.WriteAllText(stem + ".json", JsonSerializer.Serialize(summary, DatasetLoader.JsonOptions));

        return (stem + ".md", stem + ".json");
    }
}
