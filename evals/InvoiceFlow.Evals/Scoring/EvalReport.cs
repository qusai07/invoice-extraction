using System.Globalization;
using System.Text;

namespace InvoiceFlow.Evals.Scoring;

/// <summary>Aggregates document scores into the numbers we track (PLAN.md §1 and §13).</summary>
public sealed class EvalReport(string extractor, DateTimeOffset startedAt, IReadOnlyList<DocumentScore> documents)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Extractor { get; } = extractor;
    public DateTimeOffset StartedAt { get; } = startedAt;
    public IReadOnlyList<DocumentScore> Documents { get; } = documents;

    public int Count => Documents.Count;
    public int Succeeded => Documents.Count(d => d.Status == Application.Extraction.ExtractionStatus.Succeeded);
    public double FullyCorrectRate => Rate(Documents.Count(d => d.FullyCorrect), Count);
    public double HeaderAccuracy => Rate(Documents.Sum(d => d.Fields.Count(f => f.Correct)), Documents.Sum(d => d.Fields.Count));
    public int Hallucinations => Documents.Sum(d => d.Fields.Count(f => f.Hallucinated));
    public double LinePrecision => Rate(Documents.Sum(d => d.CorrectLines), Documents.Sum(d => d.ActualLines));
    public double LineRecall => Rate(Documents.Sum(d => d.CorrectLines), Documents.Sum(d => d.ExpectedLines));
    public decimal TotalCostUsd => Documents.Sum(d => d.Usage.CostUsd);

    public double FieldAccuracy(string field) =>
        Rate(Documents.Count(d => d.Fields.Single(f => f.Field == field).Correct), Count);

    // ---- business metrics (PLAN.md §1) over documents with an expected decision ----
    private List<DocumentScore> Decided => Documents.Where(d => d.HasDecision).ToList();

    public int DecisionCount => Decided.Count;
    public double DecisionAccuracy => Rate(Decided.Count(d => d.DecisionCorrect), Decided.Count);
    public double StraightThroughRate => Rate(Decided.Count(d => d.StraightThrough), Decided.Count);
    public double ExpectedStraightThroughRate => Rate(Decided.Count(d => d.ExpectedDecision!.Outcome is Application.Validation.ValidationOutcome.Approved or Application.Validation.ValidationOutcome.PendingApproval), Decided.Count);
    public int FalseAccepts => Decided.Count(d => d.FalseAccept);
    public double FalseAcceptRate => Rate(FalseAccepts, Decided.Count, empty: 0);
    public int FalseRejects => Decided.Count(d => d.FalseReject);

    private List<DocumentScore> Planted => Decided.Where(d => d.ExpectedDecision!.ErrorCodes.Count > 0).ToList();

    public double CaughtForRightReasonRate => Rate(Planted.Count(d => d.CaughtForRightReason), Planted.Count);

    /// <summary>For CI self-test: everything must be perfect.</summary>
    public bool IsPerfect => FullyCorrectRate >= 1 && (DecisionCount == 0 || (DecisionAccuracy >= 1 && CaughtForRightReasonRate >= 1));

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        Line(sb, $"# Eval report — `{Extractor}`");
        Line(sb);
        Line(sb, $"- Run: {StartedAt:yyyy-MM-dd HH:mm} UTC · Documents: **{Count}** · Extracted: {Succeeded}");
        var model = Documents.Select(d => d.Usage.Model).FirstOrDefault(m => m is not null);
        var prompt = Documents.Select(d => d.Usage.PromptVersion).FirstOrDefault(p => p is not null);
        Line(sb, $"- Model: `{model ?? "n/a"}` · Prompt: `{prompt ?? "n/a"}`");
        Line(sb);

        if (DecisionCount > 0)
        {
            Line(sb, "## Business outcome (extraction + validation vs expected decision)");
            Line(sb);
            Line(sb, "| Metric | Value |");
            Line(sb, "|---|---|");
            Line(sb, $"| **False accepts** (passed through but should have stopped, or wrong data) | **{FalseAccepts}** ({Pct(FalseAcceptRate)}) |");
            Line(sb, $"| Straight-through rate (no human data entry) | {Pct(StraightThroughRate)} (ideal for this set: {Pct(ExpectedStraightThroughRate)}) |");
            Line(sb, $"| Decision accuracy | {Pct(DecisionAccuracy)} |");
            Line(sb, $"| False rejects (sent to review unnecessarily) | {FalseRejects} |");
            Line(sb, $"| Planted problems caught by the intended rule | {Pct(CaughtForRightReasonRate)} |");
            Line(sb);

            var wrongDecisions = Decided.Where(d => !d.DecisionCorrect || !d.CaughtForRightReason).ToList();
            if (wrongDecisions.Count > 0)
            {
                Line(sb, "| Document | Scenario | Expected | Got | Rules fired | |");
                Line(sb, "|---|---|---|---|---|---|");
                foreach (var d in wrongDecisions)
                {
                    var flag = d.FalseAccept ? "**FALSE ACCEPT**" : d.FalseReject ? "false reject" : "wrong reason";
                    Line(sb, $"| `{d.Id}` | {d.ExpectedDecision!.Scenario} | {d.ExpectedDecision.Outcome} {string.Join(",", d.ExpectedDecision.ErrorCodes)} | " +
                             $"{d.PredictedOutcome} | {string.Join(", ", d.PredictedErrors)} | {flag} |");
                }

                Line(sb);
            }
        }

        Line(sb, "## Extraction summary");
        Line(sb);
        Line(sb, "| Metric | Value |");
        Line(sb, "|---|---|");
        Line(sb, $"| Documents fully correct (all header fields + all lines) | **{Pct(FullyCorrectRate)}** |");
        Line(sb, $"| Header field accuracy | {Pct(HeaderAccuracy)} |");
        Line(sb, $"| Line precision / recall | {Pct(LinePrecision)} / {Pct(LineRecall)} |");
        Line(sb, $"| Hallucinated fields (value where document has none) | {Hallucinations} |");
        Line(sb, $"| Total cost | ${TotalCostUsd.ToString("0.0000", Inv)} (avg ${Avg(Documents.Select(d => d.Usage.CostUsd)).ToString("0.0000", Inv)}/doc) |");
        Line(sb, $"| Latency p50 / p95 | {Seconds(Percentile(0.50))} / {Seconds(Percentile(0.95))} |");
        Line(sb, $"| Tokens in / out | {Documents.Sum(d => d.Usage.InputTokens).ToString("N0", Inv)} / {Documents.Sum(d => d.Usage.OutputTokens).ToString("N0", Inv)} |");
        Line(sb);

        Line(sb, "## Per field (exact match)");
        Line(sb);
        Line(sb, "| Field | Accuracy | Missed | Hallucinated |");
        Line(sb, "|---|---|---|---|");
        foreach (var field in InvoiceScorer.HeaderFields)
        {
            var scores = Documents.Select(d => d.Fields.Single(f => f.Field == field)).ToList();
            Line(sb, $"| {field} | {Pct(FieldAccuracy(field))} | {scores.Count(s => s.Missed)} | {scores.Count(s => s.Hallucinated)} |");
        }

        Line(sb);
        Breakdown(sb, "language", d => d.Meta.Language);
        Breakdown(sb, "quality", d => d.Meta.Quality);

        var failed = Documents.Where(d => d.Status != Application.Extraction.ExtractionStatus.Succeeded).ToList();
        if (failed.Count > 0)
        {
            Line(sb, "## Extraction failures");
            Line(sb);
            foreach (var d in failed)
            {
                Line(sb, $"- `{d.Id}` — {d.Status}: {d.Error}");
            }

            Line(sb);
        }

        Line(sb, "## Mismatches");
        Line(sb);
        var any = false;
        foreach (var d in Documents.Where(d => d.Status == Application.Extraction.ExtractionStatus.Succeeded && !d.FullyCorrect))
        {
            any = true;
            Line(sb, $"### `{d.Id}` ({d.Meta.Language}, {d.Meta.Quality}{(d.Meta.Tags.Count > 0 ? ", " + string.Join(", ", d.Meta.Tags) : "")})");
            foreach (var f in d.Fields.Where(f => !f.Correct))
            {
                Line(sb, $"- **{f.Field}**: expected `{f.Expected ?? "null"}`, got `{f.Actual ?? "null"}`");
            }

            if (!d.AllLinesCorrect)
            {
                Line(sb, $"- **lines**: {d.CorrectLines}/{d.ExpectedLines} correct, {d.ActualLines} returned");
            }

            Line(sb);
        }

        if (!any)
        {
            Line(sb, "None.");
        }

        return sb.ToString();
    }

    private void Breakdown(StringBuilder sb, string name, Func<DocumentScore, string> key)
    {
        Line(sb, $"## By {name}");
        Line(sb);
        Line(sb, $"| {name} | Docs | Fully correct | Header accuracy |");
        Line(sb, "|---|---|---|---|");
        foreach (var g in Documents.GroupBy(key).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var docs = g.ToList();
            Line(sb, $"| {g.Key} | {docs.Count} | {Pct(Rate(docs.Count(d => d.FullyCorrect), docs.Count))} | " +
                     $"{Pct(Rate(docs.Sum(d => d.Fields.Count(f => f.Correct)), docs.Sum(d => d.Fields.Count)))} |");
        }

        Line(sb);
    }

    private TimeSpan Percentile(double p)
    {
        var sorted = Documents.Select(d => d.Usage.Duration).Order().ToList();
        return sorted.Count == 0 ? TimeSpan.Zero : sorted[(int)Math.Ceiling(p * sorted.Count) - 1];
    }

    private static decimal Avg(IEnumerable<decimal> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0 : list.Average();
    }

    private static double Rate(int part, int whole, double empty = 1) => whole == 0 ? empty : (double)part / whole;
    private static string Pct(double r) => r.ToString("P1", Inv);
    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.0", Inv) + " s";
    private static void Line(StringBuilder sb, string text = "") => sb.Append(text).Append('\n');
}
