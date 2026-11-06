namespace InvoiceFlow.Domain.MasterData;

/// <summary>Supplier as known in the ERP. The source of truth for TRN and bank details.</summary>
public sealed record Supplier
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? NameAr { get; init; }
    public string? Trn { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Registered IBAN. An invoice asking for payment to a different IBAN is a fraud signal (SUP-002).</summary>
    public string? Iban { get; init; }

    public string DefaultCurrency { get; init; } = "AED";

    /// <summary>3-way match: invoiced quantities must be covered by goods receipts (GRN-001).</summary>
    public bool RequiresGoodsReceipt { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>Per-supplier tolerance. Null → tenant default.</summary>
    public ToleranceProfile? Tolerance { get; init; }
}

public enum PurchaseOrderStatus
{
    Open,
    Closed,
    Cancelled,
}

public sealed record PurchaseOrder
{
    public required string PoNumber { get; init; }
    public required string SupplierId { get; init; }
    public string Currency { get; init; } = "AED";
    public PurchaseOrderStatus Status { get; init; } = PurchaseOrderStatus.Open;
    public IReadOnlyList<PoLine> Lines { get; init; } = [];
}

public sealed record PoLine
{
    public required int LineNo { get; init; }
    public string? Sku { get; init; }
    public required string Description { get; init; }
    public string? DescriptionAr { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
    public decimal VatRate { get; init; } = 0.05m;

    /// <summary>Quantity already invoiced by earlier invoices.</summary>
    public decimal QuantityInvoiced { get; init; }

    /// <summary>Quantity received on goods receipts (GRNs), summed.</summary>
    public decimal QuantityReceived { get; init; }

    public decimal RemainingToInvoice => Quantity - QuantityInvoiced;
}

/// <summary>How far an invoice may differ from the PO before a person must look. See PLAN.md §9.</summary>
public sealed record ToleranceProfile
{
    public static readonly ToleranceProfile Default = new();

    /// <summary>Allowed unit-price increase as a fraction (0.005 = 0.5%).</summary>
    public decimal UnitPricePct { get; init; } = 0.005m;

    /// <summary>Allowed unit-price increase in currency units (whichever of pct/abs is larger applies).</summary>
    public decimal UnitPriceAbs { get; init; } = 0.01m;

    /// <summary>Allowed quantity over the remaining PO quantity, as a fraction. 0 = never over-invoice.</summary>
    public decimal QuantityOverPct { get; init; }
}
