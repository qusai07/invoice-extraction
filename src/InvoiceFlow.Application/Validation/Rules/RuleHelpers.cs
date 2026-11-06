using System.Globalization;

namespace InvoiceFlow.Application.Validation.Rules;

internal static class Fmt
{
    public static string? Amount(decimal? v) => v?.ToString("#,##0.00", CultureInfo.InvariantCulture);

    public static string? Number(decimal? v) => v?.ToString("0.####", CultureInfo.InvariantCulture);

    public static string? Percent(decimal? v) => v is null ? null : (v.Value * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    public static string? Date(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Line(int index) => $"lines[{index}]";
}

/// <summary>Small base to keep rule classes short.</summary>
public abstract class Rule : IValidationRule
{
    public abstract string Code { get; }

    public abstract IEnumerable<ValidationFinding> Evaluate(ValidationContext context);

    protected ValidationFinding Error(string? field, string? expected, string? actual, string en, string ar) =>
        new(Code, Severity.Error, field, expected, actual, en, ar);

    protected ValidationFinding Warning(string? field, string? expected, string? actual, string en, string ar) =>
        new(Code, Severity.Warning, field, expected, actual, en, ar);

    protected ValidationFinding Info(string? field, string en, string ar) =>
        new(Code, Severity.Info, field, null, null, en, ar);
}
