using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Domain.MasterData;

namespace InvoiceFlow.Infrastructure.MockErp;

/// <summary>Mock ERP: serves master data from a <see cref="MasterDataFile"/>. Read-only, thread-safe.</summary>
public sealed class InMemoryMasterDataService(MasterDataFile data) : IMasterDataService
{
    private readonly Dictionary<string, Supplier> _byTrn = data.Suppliers
        .Where(s => s.Trn is not null)
        .ToDictionary(s => s.Trn!, StringComparer.Ordinal);

    private readonly Dictionary<string, PurchaseOrder> _pos = data.PurchaseOrders
        .ToDictionary(p => Key(p.PoNumber), StringComparer.Ordinal);

    public MasterDataFile Data { get; } = data;

    public Task<Supplier?> FindSupplierByTrnAsync(string trn, CancellationToken cancellationToken = default) =>
        Task.FromResult(_byTrn.GetValueOrDefault(trn));

    // Small master data → return everything; SupplierResolver scores. A real ERP adapter would pre-filter.
    public Task<IReadOnlyList<Supplier>> SearchSuppliersAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(Data.Suppliers);

    public Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(_pos.GetValueOrDefault(Key(poNumber)));

    // PO numbers are compared ignoring case, spaces and punctuation: "LPO/2026/0331" == "lpo 2026 0331".
    private static string Key(string poNumber) =>
        new(poNumber.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>Invoice history kept in memory. Phase 3 replaces this with the database.</summary>
public sealed class InMemoryInvoiceHistory : IInvoiceHistory
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PriorInvoice> _invoices = [];

    public void Add(string? sha256, PriorInvoice? invoice)
    {
        lock (_lock)
        {
            if (sha256 is not null)
            {
                _hashes.Add(sha256);
            }

            if (invoice is not null)
            {
                _invoices.Add(invoice);
            }
        }
    }

    public Task<bool> ExistsByHashAsync(string sha256, Guid? excludeDocumentId = null, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_hashes.Contains(sha256));
        }
    }

    public Task<IReadOnlyList<PriorInvoice>> FindAsync(string supplierId, string invoiceNumber, Guid? excludeDocumentId = null, CancellationToken cancellationToken = default)
    {
        var key = Normalize(invoiceNumber);
        var exclude = excludeDocumentId?.ToString();
        lock (_lock)
        {
            IReadOnlyList<PriorInvoice> found = _invoices
                .Where(i => i.SupplierId == supplierId && Normalize(i.InvoiceNumber) == key && i.DocumentId != exclude)
                .ToList();
            return Task.FromResult(found);
        }
    }

    private static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
