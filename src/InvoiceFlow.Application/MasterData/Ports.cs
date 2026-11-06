using InvoiceFlow.Domain.MasterData;

namespace InvoiceFlow.Application.MasterData;

/// <summary>Read access to ERP master data (suppliers, POs). Implemented by ERP adapters and the mock ERP.</summary>
public interface IMasterDataService
{
    Task<Supplier?> FindSupplierByTrnAsync(string trn, CancellationToken cancellationToken = default);

    /// <summary>Candidate suppliers for fuzzy name matching (may return a broad list; the resolver scores them).</summary>
    Task<IReadOnlyList<Supplier>> SearchSuppliersAsync(string name, CancellationToken cancellationToken = default);

    Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken cancellationToken = default);
}

/// <summary>Invoices already received, for duplicate detection (DUP-001 / DUP-002).</summary>
public interface IInvoiceHistory
{
    /// <param name="excludeDocumentId">The document being validated (it must not count as its own duplicate).</param>
    Task<bool> ExistsByHashAsync(string sha256, Guid? excludeDocumentId = null, CancellationToken cancellationToken = default);

    /// <summary>Earlier, non-rejected invoices with the same supplier and invoice number.</summary>
    Task<IReadOnlyList<PriorInvoice>> FindAsync(string supplierId, string invoiceNumber, Guid? excludeDocumentId = null, CancellationToken cancellationToken = default);
}

public sealed record PriorInvoice(string DocumentId, string SupplierId, string InvoiceNumber, decimal? Total, DateOnly? InvoiceDate);
