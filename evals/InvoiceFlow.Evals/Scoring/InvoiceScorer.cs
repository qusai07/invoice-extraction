using System.Globalization;
using System.Text;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Evals.Dataset;

namespace InvoiceFlow.Evals.Scoring;

public sealed record FieldScore(string Field, string? Expected, string? Actual, bool Correct)
{
    /// <summary>Model produced a value where the document has none.</summary>
    public bool Hallucinated => Expected is null && Actual is not null;

    /// <summary>Model missed a value that is on the document.</summary>
    public bool Missed => Expected is not null && Actual is null;
}

public sealed record DocumentScore(
    string Id,
    ExpectedMeta Meta,
    ExtractionStatus Status,
    string? Error,
    IReadOnlyList<FieldScore> Fields,
    int ExpectedLines,
    int ActualLines,
    int CorrectLines,
    ExtractionUsage Usage)
{
    /// <summary>Fields where a wrong value posted to the ERP costs money or breaks the audit trail.</summary>
    public static readonly string[] CriticalFields =
        ["supplierTrn", "invoiceNumber", "invoiceDate", "poNumber", "currency", "subtotal", "vatTotal", "total", "iban"];

    public bool AllHeaderFieldsCorrect => Fields.All(f => f.Correct);
    public bool AllLinesCorrect => CorrectLines == ExpectedLines && ActualLines == ExpectedLines;
    public bool FullyCorrect => Status == ExtractionStatus.Succeeded && AllHeaderFieldsCorrect && AllLinesCorrect;
    public bool CriticalDataCorrect => Status == ExtractionStatus.Succeeded && AllLinesCorrect &&
                                       Fields.Where(f => CriticalFields.Contains(f.Field)).All(f => f.Correct);

    // ---- business decision (null when the dataset item has no expected validation) ----
    public ExpectedValidation? ExpectedDecision { get; init; }
    public ValidationOutcome? PredictedOutcome { get; init; }
    public IReadOnlyList<string> PredictedErrors { get; init; } = [];

    public bool HasDecision => ExpectedDecision is not null && PredictedOutcome is not null;

    private static bool PassesThrough(ValidationOutcome? o) => o is ValidationOutcome.Approved or ValidationOutcome.PendingApproval;

    /// <summary>System let it through without data entry.</summary>
    public bool StraightThrough => PassesThrough(PredictedOutcome);

    /// <summary>
    /// The costly mistake: the system let it through, but it should have been stopped, or the data it would post is wrong.
    /// </summary>
    public bool FalseAccept => HasDecision && StraightThrough &&
                               (!PassesThrough(ExpectedDecision!.Outcome) || !CriticalDataCorrect);

    /// <summary>Sent to a person although it was fine (costs time, not money).</summary>
    public bool FalseReject => HasDecision && !StraightThrough && PassesThrough(ExpectedDecision!.Outcome);

    public bool DecisionCorrect => HasDecision && PredictedOutcome == ExpectedDecision!.Outcome && !FalseAccept;

    /// <summary>For planted problems: stopped by the rule that was meant to catch it.</summary>
    public bool CaughtForRightReason => HasDecision && ExpectedDecision!.ErrorCodes.All(PredictedErrors.Contains);
}

/// <summary>
/// Exact-match scoring. Strings are compared after light normalization (case, whitespace, punctuation);
/// numbers and dates must be exactly equal. No partial credit, which is what finance cares about.
/// </summary>
public static class InvoiceScorer
{
    public static readonly string[] HeaderFields =
    [
        "supplierName", "supplierTrn", "customerTrn", "invoiceNumber", "invoiceDate", "dueDate",
        "poNumber", "currency", "subtotal", "vatTotal", "total", "iban",
    ];

    public static DocumentScore Score(DatasetItem item, ExtractionResult result, ValidationReport? validation = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(result);

        var expected = item.Expected.Invoice;
        var actual = result.Invoice ?? new InvoiceDto();
        var alternatives = item.Expected.Alternatives;

        var fields = HeaderFields
            .Select(f => ScoreField(f, Get(expected, f), Get(actual, f), alternatives))
            .ToList();

        var correctLines = CountCorrectLines(expected.Lines, actual.Lines);

        return new DocumentScore(
            item.Id, item.Expected.Meta, result.Status, result.Error, fields,
            expected.Lines.Count, actual.Lines.Count, correctLines, result.Usage)
        {
            ExpectedDecision = item.Expected.Validation,
            // Failed extraction never passes through: it goes to a person.
            PredictedOutcome = item.Expected.Validation is null ? null
                : validation?.Outcome ?? ValidationOutcome.NeedsReview,
            PredictedErrors = validation?.Errors.Select(e => e.RuleCode).Distinct().Order(StringComparer.Ordinal).ToList() ?? [],
        };
    }

    private static FieldScore ScoreField(string field, string? expected, string? actual, IReadOnlyDictionary<string, string[]>? alternatives)
    {
        var candidates = new List<string?> { expected };
        if (alternatives is not null && alternatives.TryGetValue(field, out var alts))
        {
            candidates.AddRange(alts);
        }

        var correct = candidates.Any(c => Key(c) == Key(actual));
        return new FieldScore(field, expected, actual, correct);
    }

    /// <summary>Greedy one-to-one match: an expected line counts if some unused actual line equals it.</summary>
    public static int CountCorrectLines(IReadOnlyList<InvoiceLineDto> expected, IReadOnlyList<InvoiceLineDto> actual)
    {
        var used = new bool[actual.Count];
        var correct = 0;
        foreach (var e in expected)
        {
            for (var i = 0; i < actual.Count; i++)
            {
                if (!used[i] && LineEquals(e, actual[i]))
                {
                    used[i] = true;
                    correct++;
                    break;
                }
            }
        }

        return correct;
    }

    private static bool LineEquals(InvoiceLineDto e, InvoiceLineDto a) =>
        e.Quantity == a.Quantity &&
        e.UnitPrice == a.UnitPrice &&
        e.LineTotal == a.LineTotal &&
        e.VatRate == a.VatRate &&
        (e.Sku is null || Key(e.Sku) == Key(a.Sku));

    private static string? Get(InvoiceDto i, string field) => field switch
    {
        "supplierName" => i.SupplierName,
        "supplierTrn" => i.SupplierTrn,
        "customerTrn" => i.CustomerTrn,
        "invoiceNumber" => i.InvoiceNumber,
        "invoiceDate" => i.InvoiceDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "dueDate" => i.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "poNumber" => i.PoNumber,
        "currency" => i.Currency,
        "subtotal" => Num(i.Subtotal),
        "vatTotal" => Num(i.VatTotal),
        "total" => Num(i.Total),
        "iban" => i.Iban,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    // 1900.0 and 1900.00 must compare equal.
    private static string? Num(decimal? d) => d?.ToString("0.############", CultureInfo.InvariantCulture);

    /// <summary>Comparison key: upper-case letters/digits only. "Gulf Star Trading Co." == "GULF STAR TRADING CO".</summary>
    public static string? Key(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }
}
