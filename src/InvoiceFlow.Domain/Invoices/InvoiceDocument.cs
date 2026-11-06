namespace InvoiceFlow.Domain.Invoices;

/// <summary>Immutable audit record. Written once, never updated (PLAN.md §12).</summary>
public sealed record AuditEvent(
    Guid DocumentId,
    DateTimeOffset At,
    string Actor,
    string Action,
    InvoiceStatus? FromStatus,
    InvoiceStatus? ToStatus,
    string? Details);

/// <summary>
/// One received document and its lifecycle. Status changes only through <see cref="TransitionTo"/>, which enforces
/// <see cref="InvoiceStateMachine"/> and records an audit event.
/// </summary>
public sealed class InvoiceDocument
{
    public const string SystemActor = "system";

    private readonly List<AuditEvent> _newEvents = [];

    private InvoiceDocument()
    {
    }

    public Guid Id { get; private set; }
    public string TenantId { get; private set; } = "default";
    public string FileName { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
    public string StoragePath { get; private set; } = "";
    public string Source { get; private set; } = "upload";
    public string SubmittedBy { get; private set; } = "";
    public DateTimeOffset ReceivedAt { get; private set; }

    public InvoiceStatus Status { get; private set; }
    public DateTimeOffset StatusChangedAt { get; private set; }

    // Summary of the current invoice data, for lists, search and duplicate checks.
    public string? SupplierId { get; private set; }
    public string? SupplierName { get; private set; }
    public string? InvoiceNumber { get; private set; }
    public DateOnly? InvoiceDate { get; private set; }
    public string? Currency { get; private set; }
    public decimal? Total { get; private set; }
    public string? PoNumber { get; private set; }

    /// <summary>Last person who made the invoice pass (corrected data or overrode a rule). Used for 4-eyes checks.</summary>
    public string? ResolvedBy { get; private set; }

    public string? ApprovedBy { get; private set; }
    public string? ErpReference { get; private set; }

    /// <summary>Events raised since load; persisted by the store and then cleared.</summary>
    public IReadOnlyList<AuditEvent> NewEvents => _newEvents;

    public static InvoiceDocument Receive(
        Guid id, string tenantId, string fileName, string contentType, long sizeBytes, string sha256,
        string storagePath, string source, string submittedBy, DateTimeOffset at)
    {
        var doc = new InvoiceDocument
        {
            Id = id,
            TenantId = tenantId,
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            StoragePath = storagePath,
            Source = source,
            SubmittedBy = submittedBy,
            ReceivedAt = at,
            Status = InvoiceStatus.Received,
            StatusChangedAt = at,
        };
        doc._newEvents.Add(new AuditEvent(id, at, submittedBy, "Received", null, InvoiceStatus.Received, $"{fileName} ({sizeBytes} bytes, {source})"));
        return doc;
    }

    /// <summary>Rebuilds a document from storage. No events are raised.</summary>
    public static InvoiceDocument Restore(InvoiceDocumentState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new InvoiceDocument
        {
            Id = s.Id,
            TenantId = s.TenantId,
            FileName = s.FileName,
            ContentType = s.ContentType,
            SizeBytes = s.SizeBytes,
            Sha256 = s.Sha256,
            StoragePath = s.StoragePath,
            Source = s.Source,
            SubmittedBy = s.SubmittedBy,
            ReceivedAt = s.ReceivedAt,
            Status = s.Status,
            StatusChangedAt = s.StatusChangedAt,
            SupplierId = s.SupplierId,
            SupplierName = s.SupplierName,
            InvoiceNumber = s.InvoiceNumber,
            InvoiceDate = s.InvoiceDate,
            Currency = s.Currency,
            Total = s.Total,
            PoNumber = s.PoNumber,
            ResolvedBy = s.ResolvedBy,
            ApprovedBy = s.ApprovedBy,
            ErpReference = s.ErpReference,
        };
    }

    public InvoiceDocumentState ToState() => new()
    {
        Id = Id,
        TenantId = TenantId,
        FileName = FileName,
        ContentType = ContentType,
        SizeBytes = SizeBytes,
        Sha256 = Sha256,
        StoragePath = StoragePath,
        Source = Source,
        SubmittedBy = SubmittedBy,
        ReceivedAt = ReceivedAt,
        Status = Status,
        StatusChangedAt = StatusChangedAt,
        SupplierId = SupplierId,
        SupplierName = SupplierName,
        InvoiceNumber = InvoiceNumber,
        InvoiceDate = InvoiceDate,
        Currency = Currency,
        Total = Total,
        PoNumber = PoNumber,
        ResolvedBy = ResolvedBy,
        ApprovedBy = ApprovedBy,
        ErpReference = ErpReference,
    };

    /// <exception cref="InvalidOperationException">When the transition is not allowed.</exception>
    public void TransitionTo(InvoiceStatus to, string actor, DateTimeOffset at, string? details = null)
    {
        InvoiceStateMachine.EnsureCanTransition(Status, to);
        _newEvents.Add(new AuditEvent(Id, at, actor, "StatusChanged", Status, to, details));
        Status = to;
        StatusChangedAt = at;
    }

    /// <summary>Audit an action that doesn't change status (e.g. a data correction).</summary>
    public void Record(string action, string actor, DateTimeOffset at, string? details = null) =>
        _newEvents.Add(new AuditEvent(Id, at, actor, action, null, null, details));

    public void UpdateSummary(string? supplierId, string? supplierName, string? invoiceNumber, DateOnly? invoiceDate, string? currency, decimal? total, string? poNumber)
    {
        SupplierId = supplierId;
        SupplierName = supplierName;
        InvoiceNumber = invoiceNumber;
        InvoiceDate = invoiceDate;
        Currency = currency;
        Total = total;
        PoNumber = poNumber;
    }

    public void MarkResolvedBy(string user) => ResolvedBy = user;

    public void MarkApprovedBy(string user) => ApprovedBy = user;

    public void MarkPosted(string erpReference) => ErpReference = erpReference;

    public void ClearNewEvents() => _newEvents.Clear();
}

/// <summary>Flat snapshot of <see cref="InvoiceDocument"/> for persistence.</summary>
public sealed record InvoiceDocumentState
{
    public Guid Id { get; init; }
    public string TenantId { get; init; } = "default";
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public string StoragePath { get; init; } = "";
    public string Source { get; init; } = "upload";
    public string SubmittedBy { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }
    public InvoiceStatus Status { get; init; }
    public DateTimeOffset StatusChangedAt { get; init; }
    public string? SupplierId { get; init; }
    public string? SupplierName { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public string? Currency { get; init; }
    public decimal? Total { get; init; }
    public string? PoNumber { get; init; }
    public string? ResolvedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public string? ErpReference { get; init; }
}
