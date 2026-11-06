using System.Security.Claims;
using InvoiceFlow.Api.Security;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Domain.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace InvoiceFlow.Api;

/// <summary>Invoice API (Phase 3): async intake, review queue, corrections, approvals, audit.</summary>
public static class InvoiceEndpoints
{
    public const long MaxUploadBytes = 20 * 1024 * 1024;

    public sealed record ReasonRequest(string Reason);

    public sealed record SubmitResponse(Guid Id, InvoiceStatus Status);

    public static void MapInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/invoices").WithTags("Invoices");

        g.MapPost("/", (IFormFile file, ClaimsPrincipal user, InvoiceWorkflow wf, CancellationToken ct) => Run(() => SubmitAsync(file, user, wf, ct)))
            .RequireAuthorization(Policies.Submit)
            .RequireRateLimiting(Hardening.UploadPolicy)
            .DisableAntiforgery() // bearer tokens only (no cookies) → not exposed to CSRF
            .WithSummary("Upload an invoice. Returns 202; extraction and validation run in the background.");

        g.MapGet("/", ListAsync).RequireAuthorization(Policies.Read)
            .WithSummary("List invoices, newest status change first. Filter by status (e.g. NeedsReview for the review queue).");

        g.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Policies.Read)
            .WithSummary("Invoice with extracted data, evidence, validation findings, corrections and audit trail.");

        g.MapGet("/{id:guid}/file", GetFileAsync).RequireAuthorization(Policies.Read)
            .WithSummary("The original document.");

        g.MapPut("/{id:guid}/data", (Guid id, InvoiceDto data, ClaimsPrincipal user, InvoiceWorkflow wf, CancellationToken ct) =>
                Run(async () => TypedResults.Ok(await wf.CorrectAsync(id, data, user.ToUserContext(), ct))))
            .RequireAuthorization(Policies.Correct)
            .WithSummary("Correct the invoice data; rules run again immediately.");

        g.MapPost("/{id:guid}/override", (Guid id, ReasonRequest body, ClaimsPrincipal user, InvoiceWorkflow wf, CancellationToken ct) =>
                Run(async () => { await wf.OverrideAsync(id, body.Reason, user.ToUserContext(), ct); return TypedResults.NoContent(); }))
            .RequireAuthorization(Policies.Override)
            .WithSummary("Approve despite failing rules (reason required). Above the approval limit a second approver is still needed.");

        g.MapPost("/{id:guid}/approve", (Guid id, ClaimsPrincipal user, InvoiceWorkflow wf, CancellationToken ct) =>
                Run(async () => { await wf.ApproveAsync(id, user.ToUserContext(), ct); return TypedResults.NoContent(); }))
            .RequireAuthorization(Policies.Approve)
            .WithSummary("Second approval (4-eyes) for invoices pending approval.");

        g.MapPost("/{id:guid}/reject", (Guid id, ReasonRequest body, ClaimsPrincipal user, InvoiceWorkflow wf, CancellationToken ct) =>
                Run(async () => { await wf.RejectAsync(id, body.Reason, user.ToUserContext(), ct); return TypedResults.NoContent(); }))
            .RequireAuthorization(Policies.Reject)
            .WithSummary("Reject (reason required).");

        g.MapPost("/{id:guid}/retry-posting", (Guid id, ClaimsPrincipal user, InvoiceWorkflow wf, CancellationToken ct) =>
                Run(async () => { await wf.RetryPostingAsync(id, user.ToUserContext(), ct); return TypedResults.Accepted($"/api/invoices/{id}"); }))
            .RequireAuthorization(Policies.RetryPosting)
            .WithSummary("Retry ERP posting after a failure.");

        // Validate structured data without storing it (ERP import, e-invoice XML, what-if checks).
        g.MapPost("/validate", async (InvoiceDto invoice, ValidationEngine engine, CancellationToken ct) =>
                TypedResults.Ok(await engine.ValidateAsync(new ValidationRequest(invoice), ct)))
            .RequireAuthorization(Policies.Correct)
            .WithSummary("Validate invoice data against master data and business rules (nothing is stored).");
    }

    private static async Task<IResult> SubmitAsync(IFormFile file, ClaimsPrincipal user, InvoiceWorkflow wf, CancellationToken ct)
    {
        if (file.Length == 0 || file.Length > MaxUploadBytes)
        {
            return TypedResults.Problem($"File must be between 1 byte and {MaxUploadBytes / 1024 / 1024} MB.", statusCode: StatusCodes.Status400BadRequest);
        }

        // The declared Content-Type is only a hint; the workflow decides from the file's bytes (FileSignature).
        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        using (var buffer = new MemoryStream((int)file.Length))
        {
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        var c = await wf.SubmitAsync(new SubmitRequest(file.FileName, file.ContentType, bytes), user.ToUserContext(), ct);
        return TypedResults.Accepted($"/api/invoices/{c.Document.Id}", new SubmitResponse(c.Document.Id, c.Document.Status));
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct,
        [FromQuery] InvoiceStatus? status = null, [FromQuery] int skip = 0, [FromQuery] int take = 50)
    {
        var u = user.ToUserContext();
        var query = new InvoiceQuery(status, u.SeesOnlyOwnDocuments() ? u.Id : null, Math.Max(0, skip), take);
        return TypedResults.Ok(await store.QueryAsync(query, ct));
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, IInvoiceStore store, CancellationToken ct)
    {
        var details = await store.GetDetailsAsync(id, ct);
        return details is null || !CanSee(user.ToUserContext(), details.Document) ? TypedResults.NotFound() : TypedResults.Ok(details);
    }

    private static async Task<IResult> GetFileAsync(Guid id, ClaimsPrincipal user, IInvoiceStore store, IDocumentStore files, CancellationToken ct)
    {
        var details = await store.GetDetailsAsync(id, ct);
        if (details is null || !CanSee(user.ToUserContext(), details.Document))
        {
            return TypedResults.NotFound();
        }

        var bytes = await files.ReadAsync(details.Document.StoragePath, ct);
        return TypedResults.File(bytes, details.Document.ContentType); // inline, for the side-by-side viewer
    }

    // 404 (not 403) for other people's documents: don't reveal that they exist.
    private static bool CanSee(UserContext user, InvoiceDocumentState doc) =>
        !user.SeesOnlyOwnDocuments() || string.Equals(doc.SubmittedBy, user.Id, StringComparison.Ordinal);

    internal static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (WorkflowException ex)
        {
            var status = ex.Kind switch
            {
                WorkflowErrorKind.NotFound => StatusCodes.Status404NotFound,
                WorkflowErrorKind.Forbidden => StatusCodes.Status403Forbidden,
                WorkflowErrorKind.BadRequest => StatusCodes.Status400BadRequest,
                WorkflowErrorKind.UnsupportedMediaType => StatusCodes.Status415UnsupportedMediaType,
                WorkflowErrorKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status409Conflict,
            };
            return TypedResults.Problem(ex.Message, statusCode: status);
        }
        catch (ConcurrencyException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
