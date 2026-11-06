using InvoiceFlow.Domain.MasterData;

namespace InvoiceFlow.Application.Validation.Rules;

/// <summary>PO-001: PO number present, exists, is open, and belongs to this supplier.</summary>
public sealed class PurchaseOrderExistsRule : Rule
{
    public override string Code => "PO-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var poNumber = context.Invoice.PoNumber;
        var po = context.PurchaseOrder;

        if (poNumber is null)
        {
            if (context.Settings.RequirePurchaseOrder)
            {
                yield return Error("poNumber", null, null,
                    "No PO number on the invoice.", "لا يوجد رقم أمر شراء على الفاتورة.");
            }

            yield break;
        }

        if (po is null)
        {
            yield return Error("poNumber", null, poNumber,
                $"PO {poNumber} was not found.", $"أمر الشراء {poNumber} غير موجود.");
            yield break;
        }

        if (po.Status != PurchaseOrderStatus.Open)
        {
            yield return Error("poNumber", "Open", po.Status.ToString(),
                $"PO {po.PoNumber} is {po.Status}.", $"أمر الشراء {po.PoNumber} حالته {po.Status}.");
        }

        var supplier = context.Supplier.Supplier;
        if (supplier is not null && !string.Equals(po.SupplierId, supplier.Id, StringComparison.Ordinal))
        {
            yield return Error("poNumber", supplier.Id, po.SupplierId,
                $"PO {po.PoNumber} belongs to a different supplier.",
                $"أمر الشراء {po.PoNumber} يخص مورداً آخر.");
        }
    }
}

/// <summary>PO-002: every invoice line matches a PO line.</summary>
public sealed class LinesMatchedRule : Rule
{
    public override string Code => "PO-002";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        if (context.PurchaseOrder is null)
        {
            yield break;
        }

        foreach (var m in context.LineMatches.Where(m => m.PoLine is null))
        {
            var description = context.Invoice.Lines[m.InvoiceLineIndex].Description;
            var hint = m.Suggestions.Count > 0 ? $" Closest PO line: {m.Suggestions[0].LineNo} ({m.Suggestions[0].Description})." : string.Empty;
            var hintAr = m.Suggestions.Count > 0 ? $" أقرب بند: {m.Suggestions[0].LineNo}." : string.Empty;
            yield return Error(Fmt.Line(m.InvoiceLineIndex), null, description,
                $"Line {m.InvoiceLineIndex + 1} does not match any PO line.{hint}",
                $"البند {m.InvoiceLineIndex + 1} لا يطابق أي بند في أمر الشراء.{hintAr}");
        }
    }
}

/// <summary>PO-003: unit price within tolerance of the PO price. Higher → error; lower → warning.</summary>
public sealed class UnitPriceToleranceRule : Rule
{
    public override string Code => "PO-003";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var tolerance = context.Tolerance;
        foreach (var m in context.LineMatches)
        {
            if (m.PoLine is not { } po || context.Invoice.Lines[m.InvoiceLineIndex].UnitPrice is not { } price)
            {
                continue;
            }

            var allowed = Math.Max(po.UnitPrice * tolerance.UnitPricePct, tolerance.UnitPriceAbs);
            var field = $"{Fmt.Line(m.InvoiceLineIndex)}.unitPrice";
            var n = m.InvoiceLineIndex + 1;

            if (price - po.UnitPrice > allowed)
            {
                yield return Error(field, Fmt.Amount(po.UnitPrice), Fmt.Amount(price),
                    $"Line {n}: unit price {Fmt.Amount(price)} is above PO price {Fmt.Amount(po.UnitPrice)}.",
                    $"البند {n}: سعر الوحدة {Fmt.Amount(price)} أعلى من سعر أمر الشراء {Fmt.Amount(po.UnitPrice)}.");
            }
            else if (po.UnitPrice - price > allowed)
            {
                yield return Warning(field, Fmt.Amount(po.UnitPrice), Fmt.Amount(price),
                    $"Line {n}: unit price {Fmt.Amount(price)} is below PO price {Fmt.Amount(po.UnitPrice)}.",
                    $"البند {n}: سعر الوحدة {Fmt.Amount(price)} أقل من سعر أمر الشراء {Fmt.Amount(po.UnitPrice)}.");
            }
        }
    }
}

/// <summary>PO-004: invoiced quantity ≤ quantity still open on the PO line.</summary>
public sealed class QuantityRemainingRule : Rule
{
    public override string Code => "PO-004";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        foreach (var m in context.LineMatches)
        {
            if (m.PoLine is not { } po || context.Invoice.Lines[m.InvoiceLineIndex].Quantity is not { } qty)
            {
                continue;
            }

            var allowed = po.RemainingToInvoice * (1 + context.Tolerance.QuantityOverPct);
            if (qty > allowed)
            {
                var n = m.InvoiceLineIndex + 1;
                yield return Error($"{Fmt.Line(m.InvoiceLineIndex)}.quantity", Fmt.Number(po.RemainingToInvoice), Fmt.Number(qty),
                    $"Line {n}: quantity {Fmt.Number(qty)} exceeds the {Fmt.Number(po.RemainingToInvoice)} still open on the PO.",
                    $"البند {n}: الكمية {Fmt.Number(qty)} تتجاوز الكمية المتبقية في أمر الشراء {Fmt.Number(po.RemainingToInvoice)}.");
            }
        }
    }
}

/// <summary>PO-005: invoice currency = PO currency.</summary>
public sealed class CurrencyMatchesPoRule : Rule
{
    public override string Code => "PO-005";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var po = context.PurchaseOrder;
        var currency = context.Invoice.Currency;
        if (po is null || currency is null || string.Equals(po.Currency, currency, StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        yield return Error("currency", po.Currency, currency,
            $"Invoice currency {currency} differs from PO currency {po.Currency}.",
            $"عملة الفاتورة {currency} تختلف عن عملة أمر الشراء {po.Currency}.");
    }
}

/// <summary>GRN-001: (3-way match) invoiced quantity is covered by goods received and not yet invoiced.</summary>
public sealed class GoodsReceivedRule : Rule
{
    public override string Code => "GRN-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        if (context.Supplier.Supplier is not { RequiresGoodsReceipt: true })
        {
            yield break;
        }

        foreach (var m in context.LineMatches)
        {
            if (m.PoLine is not { } po || context.Invoice.Lines[m.InvoiceLineIndex].Quantity is not { } qty)
            {
                continue;
            }

            var receivedNotInvoiced = po.QuantityReceived - po.QuantityInvoiced;
            if (qty > receivedNotInvoiced)
            {
                var n = m.InvoiceLineIndex + 1;
                yield return Error($"{Fmt.Line(m.InvoiceLineIndex)}.quantity", Fmt.Number(receivedNotInvoiced), Fmt.Number(qty),
                    $"Line {n}: invoiced {Fmt.Number(qty)} but only {Fmt.Number(receivedNotInvoiced)} received and not yet invoiced.",
                    $"البند {n}: الكمية المفوترة {Fmt.Number(qty)} لكن المستلم وغير المفوتر {Fmt.Number(receivedNotInvoiced)} فقط.");
            }
        }
    }
}

/// <summary>APR-001: at or above the approval threshold → second approver (routing, not an error).</summary>
public sealed class ApprovalThresholdRule : Rule
{
    public override string Code => "APR-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var threshold = context.Settings.ApprovalThreshold;
        if (threshold > 0 && context.Invoice.Total is { } total && total >= threshold)
        {
            yield return Info("total",
                $"Total {Fmt.Amount(total)} is at or above the approval limit {Fmt.Amount(threshold)}: a second approver is required.",
                $"الإجمالي {Fmt.Amount(total)} يساوي أو يتجاوز حد الاعتماد {Fmt.Amount(threshold)}: يلزم اعتماد ثانٍ.");
        }
    }
}
