using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Domain.Invoices;

namespace InvoiceFlow.Application.Workflow;

public enum PipelineStep
{
    Extract,
    Validate,
    Post,
}

/// <summary>A data correction made by a reviewer (feeds the eval dataset, PLAN.md §10).</summary>
public sealed record FieldCorrection(string Field, string? OldValue, string? NewValue, string UserId, DateTimeOffset At);

/// <summary>
/// One document with all its data: the aggregate the workflow loads, changes and saves as a unit.
/// Jobs added to <see cref="JobsToEnqueue"/> are written in the same transaction (outbox), so a status change and
/// its follow-up work can never get out of sync.
/// </summary>
public sealed class InvoiceCase(InvoiceDocument document)
{
    public InvoiceDocument Document { get; } = document;

    /// <summary>Current invoice data: extracted, then possibly corrected.</summary>
    public InvoiceDto? Invoice { get; set; }

    public ExtractionResult? Extraction { get; set; }
    public ValidationReport? Validation { get; set; }
    public List<FieldCorrection> NewCorrections { get; } = [];
    public List<PipelineStep> JobsToEnqueue { get; } = [];
}

public sealed record InvoiceSummary(
    Guid Id,
    string FileName,
    InvoiceStatus Status,
    DateTimeOffset ReceivedAt,
    DateTimeOffset StatusChangedAt,
    string SubmittedBy,
    string? SupplierName,
    string? InvoiceNumber,
    DateOnly? InvoiceDate,
    string? Currency,
    decimal? Total,
    IReadOnlyList<string> ErrorCodes,
    string? SubmittedByName = null);

public sealed record InvoiceQuery(InvoiceStatus? Status = null, string? SubmittedBy = null, int Skip = 0, int Take = 50);

public sealed record InvoiceDetails(
    InvoiceDocumentState Document,
    InvoiceDto? Invoice,
    ExtractionResult? Extraction,
    ValidationReport? Validation,
    IReadOnlyList<FieldCorrection> Corrections,
    IReadOnlyList<AuditEvent> Audit,
    DuplicateInfo? DuplicateOf = null,
    IReadOnlyDictionary<string, string>? People = null);

/// <summary>For a duplicate: the earlier document it repeats. Reason "file" = same file (DUP-001); "number" = same supplier + invoice number (DUP-002).</summary>
public sealed record DuplicateInfo(Guid Id, string FileName, DateTimeOffset ReceivedAt, InvoiceStatus Status, string Reason);

/// <summary>Persistence for invoice cases (implemented with EF Core / SQL Server).</summary>
public interface IInvoiceStore
{
    Task<InvoiceCase?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(InvoiceCase invoiceCase, CancellationToken cancellationToken = default);

    /// <summary>Saves the case, its new audit events, corrections and jobs atomically.</summary>
    /// <exception cref="ConcurrencyException">The document was changed by someone else since it was loaded.</exception>
    Task SaveAsync(InvoiceCase invoiceCase, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvoiceSummary>> QueryAsync(InvoiceQuery query, CancellationToken cancellationToken = default);

    Task<InvoiceDetails?> GetDetailsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Audit events in a time range, oldest first, streamed (for auditors; can be large).</summary>
    IAsyncEnumerable<AuditExportRow> ExportAuditAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

public sealed record AuditExportRow(
    DateTimeOffset At, Guid DocumentId, string FileName, string? InvoiceNumber, string Actor, string Action,
    InvoiceStatus? FromStatus, InvoiceStatus? ToStatus, string? Details);

/// <summary>Original files (Blob storage or file system). Files are immutable once stored.</summary>
public interface IDocumentStore
{
    /// <returns>Storage path to keep on the document.</returns>
    Task<string> SaveAsync(Guid documentId, string fileExtension, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    Task<byte[]> ReadAsync(string storagePath, CancellationToken cancellationToken = default);
}

public sealed record PipelineJob(long Id, Guid DocumentId, PipelineStep Step, int Attempts);

/// <summary>Work queue for pipeline steps. Enqueueing happens through <see cref="IInvoiceStore.SaveAsync"/>.</summary>
public interface IJobQueue
{
    /// <summary>Claims the next due job for <paramref name="lease"/>; an expired lease makes the job claimable again.</summary>
    Task<PipelineJob?> ClaimAsync(string workerId, TimeSpan lease, CancellationToken cancellationToken = default);

    Task CompleteAsync(long jobId, CancellationToken cancellationToken = default);

    /// <param name="retryAt">When to try again; null = give up (job is marked failed).</param>
    Task FailAsync(long jobId, string error, DateTimeOffset? retryAt, CancellationToken cancellationToken = default);
}

public sealed record ErpPostRequest(string IdempotencyKey, Guid DocumentId, string SupplierId, InvoiceDto Invoice, string? PoNumber);

/// <summary>Posts an approved invoice to the ERP. Must be idempotent on <see cref="ErpPostRequest.IdempotencyKey"/>.</summary>
public interface IErpPoster
{
    /// <returns>The ERP's document reference.</returns>
    Task<string> PostInvoiceAsync(ErpPostRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The signed-in user, as seen by the workflow.</summary>
/// <param name="ApprovalLimit">Highest invoice total this user may approve; null = may not approve; decimal.MaxValue = unlimited.</param>
public sealed record UserContext(string Id, string Name, IReadOnlySet<string> Roles)
{
    public static readonly UserContext System = new(InvoiceDocument.SystemActor, "System", new HashSet<string>());

    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>();

    public decimal? ApprovalLimit { get; init; }

    public bool IsInRole(string role) => Roles.Contains(role);

    public bool Can(string permission) => Permissions.Contains(permission);
}

public static class Roles
{
    public const string Submitter = "Submitter";
    public const string Clerk = "Clerk";
    public const string Supervisor = "Supervisor";
    public const string FinanceAdmin = "FinanceAdmin";
    public const string Auditor = "Auditor";
    public const string SystemAdmin = "SystemAdmin";

    public static readonly IReadOnlyList<string> All = [Submitter, Clerk, Supervisor, FinanceAdmin, Auditor, SystemAdmin];
}

public enum WorkflowErrorKind
{
    NotFound,
    InvalidState,
    Forbidden,
    BadRequest,
    Conflict,
    UnsupportedMediaType,
    Unavailable,
}

public sealed class WorkflowException(WorkflowErrorKind kind, string message) : Exception(message)
{
    public WorkflowErrorKind Kind { get; } = kind;
}

public sealed class ConcurrencyException(string message, Exception? inner = null) : Exception(message, inner);
