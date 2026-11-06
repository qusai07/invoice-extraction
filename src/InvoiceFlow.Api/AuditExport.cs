using System.Globalization;
using System.Text;
using InvoiceFlow.Api.Security;
using InvoiceFlow.Application.Workflow;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceFlow.Api;

public static class AuditExport
{
    public const int MaxDays = 366;

    public static void MapAuditExport(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/audit/export", ExportAsync)
            .RequireAuthorization(Policies.Audit)
            .WithTags("Audit")
            .WithSummary("Audit trail as CSV (UTF-8, Excel-safe). Range: from (inclusive) .. to (exclusive), max 366 days.");

    private static async Task ExportAsync(
        HttpContext http, IInvoiceStore store, CancellationToken ct,
        [FromQuery] DateOnly from, [FromQuery] DateOnly to)
    {
        if (to <= from || to.DayNumber - from.DayNumber > MaxDays)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { title = $"'to' must be after 'from' and at most {MaxDays} days later." }, ct);
            return;
        }

        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"invoiceflow-audit-{from:yyyyMMdd}-{to:yyyyMMdd}.csv\"";

        await using var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 64 * 1024);
        await writer.WriteLineAsync("at_utc,document_id,file_name,invoice_number,actor,action,from_status,to_status,details");

        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await foreach (var r in store.ExportAuditAsync(start, end, ct))
        {
            await writer.WriteLineAsync(string.Join(',',
                Cell(r.At.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Cell(r.DocumentId.ToString()), Cell(r.FileName), Cell(r.InvoiceNumber), Cell(r.Actor), Cell(r.Action),
                Cell(r.FromStatus?.ToString()), Cell(r.ToStatus?.ToString()), Cell(r.Details)));
        }

        await writer.FlushAsync(ct);
    }

    /// <summary>
    /// RFC 4180 quoting plus protection against CSV/formula injection: values from documents and users
    /// (file names, reasons, invoice numbers) that start with = + - @ or a control char are prefixed with '
    /// so Excel shows them as text instead of executing them.
    /// </summary>
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var v = value;
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }

        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 || v != value
            ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : v;
    }
}
