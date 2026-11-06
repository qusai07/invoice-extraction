using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Domain.MasterData;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Application.Tests.Validation;

/// <summary>A supplier, a PO and an invoice that passes every rule. Tests break one thing at a time.</summary>
internal static class Fixtures
{
    public static readonly DateOnly Today = new(2026, 11, 5);
    public const string OurTrn = "100555000111003";

    public static Supplier Supplier { get; } = new()
    {
        Id = "SUP-1",
        Name = "Gulf Star Trading Co. LLC",
        NameAr = "شركة نجمة الخليج للتجارة ذ.م.م",
        Trn = "100107850719653",
        Iban = "AE050873100761272610650",
        Aliases = ["Gulf Star"],
    };

    public static Supplier OtherSupplier { get; } = new()
    {
        Id = "SUP-2",
        Name = "Blue Wave Cleaning Services LLC",
        Trn = "100999888777003",
    };

    public static PurchaseOrder Po { get; } = new()
    {
        PoNumber = "PO-45-001178",
        SupplierId = "SUP-1",
        Lines =
        [
            new PoLine { LineNo = 10, Sku = "PAP-A4-80", Description = "A4 copy paper 80gsm (box of 5 reams)", DescriptionAr = "ورق تصوير A4 80 غم (كرتونة 5 رزم)", Quantity = 10, UnitPrice = 100m, QuantityReceived = 10 },
            new PoLine { LineNo = 20, Sku = "TON-26A", Description = "Laser toner cartridge 26A", DescriptionAr = "خرطوشة حبر ليزر 26A", Quantity = 5, UnitPrice = 20m, QuantityReceived = 5 },
        ],
    };

    public static InvoiceDto Invoice { get; } = new()
    {
        SupplierName = "Gulf Star Trading Co. LLC",
        SupplierTrn = "100107850719653",
        CustomerTrn = OurTrn,
        InvoiceNumber = "INV-2026-04151",
        InvoiceDate = Today.AddDays(-10),
        DueDate = Today.AddDays(20),
        PoNumber = "PO-45-001178",
        Currency = "AED",
        Lines =
        [
            new InvoiceLineDto { Sku = "PAP-A4-80", Description = "A4 copy paper 80gsm (box of 5 reams)", Quantity = 10, UnitPrice = 100m, LineTotal = 1000m, VatRate = 0.05m },
            new InvoiceLineDto { Sku = "TON-26A", Description = "Laser toner cartridge 26A", Quantity = 5, UnitPrice = 20m, LineTotal = 100m, VatRate = 0.05m },
        ],
        Subtotal = 1100m,
        VatTotal = 55m,
        Total = 1155m,
        Iban = "AE050873100761272610650",
    };

    public static InvoiceDto WithLine(this InvoiceDto invoice, int index, Func<InvoiceLineDto, InvoiceLineDto> change) =>
        invoice with { Lines = invoice.Lines.Select((l, i) => i == index ? change(l) : l).ToList() };

    public static ValidationEngine Engine(
        IEnumerable<Supplier>? suppliers = null,
        IEnumerable<PurchaseOrder>? pos = null,
        FakeHistory? history = null,
        Action<ValidationSettings>? configure = null)
    {
        var settings = new ValidationSettings { CompanyTrns = [OurTrn] };
        configure?.Invoke(settings);
        return new ValidationEngine(
            new FakeMasterData(suppliers ?? [Supplier, OtherSupplier], pos ?? [Po]),
            history ?? new FakeHistory(),
            Options.Create(settings),
            new FixedTime(Today));
    }

    public static Task<ValidationReport> ValidateAsync(InvoiceDto invoice, ValidationEngine? engine = null, ExtractionResult? extraction = null, string? sha = null) =>
        (engine ?? Engine()).ValidateAsync(new ValidationRequest(invoice, extraction, sha));
}

internal sealed class FakeMasterData(IEnumerable<Supplier> suppliers, IEnumerable<PurchaseOrder> pos) : IMasterDataService
{
    private readonly List<Supplier> _suppliers = suppliers.ToList();
    private readonly List<PurchaseOrder> _pos = pos.ToList();

    public Task<Supplier?> FindSupplierByTrnAsync(string trn, CancellationToken cancellationToken = default) =>
        Task.FromResult(_suppliers.FirstOrDefault(s => s.Trn == trn));

    public Task<IReadOnlyList<Supplier>> SearchSuppliersAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Supplier>>(_suppliers);

    public Task<PurchaseOrder?> GetPurchaseOrderAsync(string poNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(_pos.FirstOrDefault(p => p.PoNumber == poNumber));

    public Task<IReadOnlyList<PurchaseOrder>> ListOpenPurchaseOrdersAsync(string supplierId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PurchaseOrder>>(_pos.Where(p => p.SupplierId == supplierId && p.Status == PurchaseOrderStatus.Open).ToList());
}

internal sealed class FakeHistory : IInvoiceHistory
{
    public HashSet<string> Hashes { get; } = [];
    public List<PriorInvoice> Invoices { get; } = [];

    public Task<bool> ExistsByHashAsync(string sha256, Guid? excludeDocumentId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(Hashes.Contains(sha256));

    public Task<IReadOnlyList<PriorInvoice>> FindAsync(string supplierId, string invoiceNumber, Guid? excludeDocumentId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PriorInvoice>>(Invoices.Where(i => i.SupplierId == supplierId && i.InvoiceNumber == invoiceNumber).ToList());
}

internal sealed class FixedTime(DateOnly today) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
}
