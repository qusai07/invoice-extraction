using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Domain.Invoices;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlow.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IInvoiceStore"/>. Scoped: one instance per request / job.</summary>
public sealed class SqlInvoiceStore(InvoiceFlowDbContext db, TimeProvider time) : IInvoiceStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Dictionary<Guid, DocumentRow> _loaded = [];

    public async Task<InvoiceCase?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        _loaded[id] = row;
        return ToCase(row);
    }

    public async Task AddAsync(InvoiceCase invoiceCase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoiceCase);
        var row = new DocumentRow();
        db.Documents.Add(row);
        _loaded[invoiceCase.Document.Id] = row;
        await SaveAsync(invoiceCase, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(InvoiceCase invoiceCase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoiceCase);
        var c = invoiceCase;
        if (!_loaded.TryGetValue(c.Document.Id, out var row))
        {
            throw new InvalidOperationException("Save a case through the same store instance that loaded or added it.");
        }

        Apply(row, c);
        var now = time.GetUtcNow();

        db.AuditEvents.AddRange(c.Document.NewEvents.Select(e => new AuditRow
        {
            DocumentId = e.DocumentId, At = e.At, Actor = e.Actor, Action = e.Action,
            FromStatus = e.FromStatus, ToStatus = e.ToStatus, Details = e.Details,
        }));
        db.Corrections.AddRange(c.NewCorrections.Select(x => new CorrectionRow
        {
            DocumentId = c.Document.Id, Field = x.Field, OldValue = x.OldValue, NewValue = x.NewValue, UserId = x.UserId, At = x.At,
        }));
        db.PipelineJobs.AddRange(c.JobsToEnqueue.Select(step => new JobRow
        {
            DocumentId = c.Document.Id, Step = step, Status = JobRow.Pending, NotBefore = now, CreatedAt = now,
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new ConcurrencyException("The invoice was changed by someone else. Reload and try again.", ex);
        }

        c.Document.ClearNewEvents();
        c.NewCorrections.Clear();
        c.JobsToEnqueue.Clear();
    }

    public async Task<IReadOnlyList<InvoiceSummary>> QueryAsync(InvoiceQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var q = db.Documents.AsNoTracking();
        if (query.Status is { } status)
        {
            q = q.Where(d => d.Status == status);
        }

        if (query.SubmittedBy is { } by)
        {
            q = q.Where(d => d.SubmittedBy == by);
        }

        var rows = await q.OrderByDescending(d => d.StatusChangedAt)
            .Skip(query.Skip).Take(Math.Clamp(query.Take, 1, 200))
            .Select(d => new { d.Id, d.FileName, d.Status, d.ReceivedAt, d.StatusChangedAt, d.SubmittedBy, d.SupplierName, d.InvoiceNumber, d.InvoiceDate, d.Currency, d.Total, d.ErrorCodes })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(d => new InvoiceSummary(d.Id, d.FileName, d.Status, d.ReceivedAt, d.StatusChangedAt, d.SubmittedBy,
                d.SupplierName, d.InvoiceNumber, d.InvoiceDate, d.Currency, d.Total,
                string.IsNullOrEmpty(d.ErrorCodes) ? [] : d.ErrorCodes.Split(',')))
            .ToList();
    }

    public async Task<InvoiceDetails?> GetDetailsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var corrections = await db.Corrections.AsNoTracking().Where(x => x.DocumentId == id).OrderBy(x => x.Id)
            .Select(x => new FieldCorrection(x.Field, x.OldValue, x.NewValue, x.UserId, x.At))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var audit = await db.AuditEvents.AsNoTracking().Where(x => x.DocumentId == id).OrderBy(x => x.Id)
            .Select(x => new AuditEvent(x.DocumentId, x.At, x.Actor, x.Action, x.FromStatus, x.ToStatus, x.Details))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var c = ToCase(row);
        return new InvoiceDetails(c.Document.ToState(), c.Invoice, c.Extraction, c.Validation, corrections, audit);
    }

    public async IAsyncEnumerable<AuditExportRow> ExportAuditAsync(
        DateTimeOffset from, DateTimeOffset to,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rows = db.AuditEvents.AsNoTracking()
            .Where(a => a.At >= from && a.At < to)
            .OrderBy(a => a.Id)
            .Join(db.Documents.AsNoTracking(), a => a.DocumentId, d => d.Id, (a, d) => new
            {
                a.At, a.DocumentId, d.FileName, d.InvoiceNumber, a.Actor, a.Action, a.FromStatus, a.ToStatus, a.Details,
            })
            .AsAsyncEnumerable();

        await foreach (var r in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new AuditExportRow(r.At, r.DocumentId, r.FileName, r.InvoiceNumber, r.Actor, r.Action, r.FromStatus, r.ToStatus, r.Details);
        }
    }

    // ---------- mapping ----------

    private static InvoiceCase ToCase(DocumentRow r)
    {
        var doc = InvoiceDocument.Restore(new InvoiceDocumentState
        {
            Id = r.Id, TenantId = r.TenantId, FileName = r.FileName, ContentType = r.ContentType, SizeBytes = r.SizeBytes,
            Sha256 = r.Sha256, StoragePath = r.StoragePath, Source = r.Source, SubmittedBy = r.SubmittedBy, ReceivedAt = r.ReceivedAt,
            Status = r.Status, StatusChangedAt = r.StatusChangedAt, SupplierId = r.SupplierId, SupplierName = r.SupplierName,
            InvoiceNumber = r.InvoiceNumber, InvoiceDate = r.InvoiceDate, Currency = r.Currency, Total = r.Total, PoNumber = r.PoNumber,
            ResolvedBy = r.ResolvedBy, ApprovedBy = r.ApprovedBy, ErpReference = r.ErpReference,
        });

        return new InvoiceCase(doc)
        {
            Invoice = Read<InvoiceDto>(r.InvoiceJson),
            Extraction = Read<ExtractionResult>(r.ExtractionJson),
            Validation = Read<ValidationReport>(r.ValidationJson),
        };
    }

    private static void Apply(DocumentRow r, InvoiceCase c)
    {
        var s = c.Document.ToState();
        r.Id = s.Id;
        r.TenantId = s.TenantId;
        r.FileName = s.FileName;
        r.ContentType = s.ContentType;
        r.SizeBytes = s.SizeBytes;
        r.Sha256 = s.Sha256;
        r.StoragePath = s.StoragePath;
        r.Source = s.Source;
        r.SubmittedBy = s.SubmittedBy;
        r.ReceivedAt = s.ReceivedAt;
        r.Status = s.Status;
        r.StatusChangedAt = s.StatusChangedAt;
        r.SupplierId = s.SupplierId;
        r.SupplierName = s.SupplierName;
        r.InvoiceNumber = s.InvoiceNumber;
        r.InvoiceNumberKey = s.InvoiceNumber is null ? null : Key(s.InvoiceNumber);
        r.InvoiceDate = s.InvoiceDate;
        r.Currency = s.Currency;
        r.Total = s.Total;
        r.PoNumber = s.PoNumber;
        r.ResolvedBy = s.ResolvedBy;
        r.ApprovedBy = s.ApprovedBy;
        r.ErpReference = s.ErpReference;
        r.ErrorCodes = c.Validation is null ? null : string.Join(',', c.Validation.Errors.Select(e => e.RuleCode).Distinct());
        r.InvoiceJson = Write(c.Invoice);
        r.ExtractionJson = Write(c.Extraction);
        r.ValidationJson = Write(c.Validation);
    }

    internal static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static T? Read<T>(string? json) where T : class => json is null ? null : JsonSerializer.Deserialize<T>(json, Json);

    private static string? Write<T>(T? value) where T : class => value is null ? null : JsonSerializer.Serialize(value, Json);
}

/// <summary>Duplicate detection over stored documents (DUP-001 / DUP-002).</summary>
public sealed class SqlInvoiceHistory(InvoiceFlowDbContext db) : IInvoiceHistory
{
    public Task<bool> ExistsByHashAsync(string sha256, Guid? excludeDocumentId = null, CancellationToken cancellationToken = default) =>
        db.Documents.AnyAsync(d => d.Sha256 == sha256 && d.Id != excludeDocumentId, cancellationToken);

    public async Task<IReadOnlyList<PriorInvoice>> FindAsync(string supplierId, string invoiceNumber, Guid? excludeDocumentId = null, CancellationToken cancellationToken = default)
    {
        var key = SqlInvoiceStore.Key(invoiceNumber);
        var rows = await db.Documents.AsNoTracking()
            .Where(d => d.SupplierId == supplierId && d.InvoiceNumberKey == key && d.Id != excludeDocumentId
                        && d.Status != InvoiceStatus.Rejected && d.Status != InvoiceStatus.Duplicate)
            .Select(d => new { d.Id, d.InvoiceNumber, d.Total, d.InvoiceDate })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(d => new PriorInvoice(d.Id.ToString(), supplierId, d.InvoiceNumber!, d.Total, d.InvoiceDate)).ToList();
    }
}
