namespace InvoiceFlow.Application.Extraction;

/// <summary>
/// Normalized invoice data. This is the contract between extraction (AI) and validation (C# rules).
/// Every field is nullable: missing data stays <c>null</c>, never guessed. See ADR-0002.
/// JSON shape (camelCase) is also the golden-dataset format, see evals/data/README.md.
/// </summary>
public sealed record InvoiceDto
{
    public string? SupplierName { get; init; }
    public string? SupplierTrn { get; init; }
    public string? CustomerTrn { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? PoNumber { get; init; }

    /// <summary>ISO 4217 code, e.g. AED.</summary>
    public string? Currency { get; init; }

    public IReadOnlyList<InvoiceLineDto> Lines { get; init; } = [];
    public decimal? Subtotal { get; init; }
    public decimal? VatTotal { get; init; }
    public decimal? Total { get; init; }
    public string? Iban { get; init; }
}

public sealed record InvoiceLineDto
{
    public string? Sku { get; init; }
    public string? Description { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? LineTotal { get; init; }

    /// <summary>Decimal fraction: 0.05 for 5%. <c>null</c> when exempt or not shown.</summary>
    public decimal? VatRate { get; init; }
}
