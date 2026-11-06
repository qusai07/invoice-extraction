namespace InvoiceFlow.Application.Validation.Rules;

internal static class Rounding
{
    /// <summary>One cent per line: line amounts are rounded individually on most invoices.</summary>
    public static decimal Tolerance(int lines) => 0.01m * Math.Max(1, lines);
}

/// <summary>ARI-001: Σ line totals = subtotal.</summary>
public sealed class LinesSumRule : Rule
{
    public override string Code => "ARI-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var inv = context.Invoice;
        if (inv.Subtotal is not { } subtotal || inv.Lines.Count == 0 || inv.Lines.Any(l => l.LineTotal is null))
        {
            yield break;
        }

        var sum = inv.Lines.Sum(l => l.LineTotal!.Value);
        if (Math.Abs(sum - subtotal) > Rounding.Tolerance(inv.Lines.Count))
        {
            yield return Error("subtotal", Fmt.Amount(sum), Fmt.Amount(subtotal),
                $"Line totals add up to {Fmt.Amount(sum)}, but subtotal is {Fmt.Amount(subtotal)}.",
                $"مجموع البنود {Fmt.Amount(sum)} لكن المجموع الفرعي {Fmt.Amount(subtotal)}.");
        }
    }
}

/// <summary>ARI-002: quantity × unit price = line total.</summary>
public sealed class LineArithmeticRule : Rule
{
    public override string Code => "ARI-002";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var lines = context.Invoice.Lines;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Quantity is not { } q || l.UnitPrice is not { } p || l.LineTotal is not { } t)
            {
                continue;
            }

            var expected = Math.Round(q * p, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(expected - t) > 0.01m)
            {
                yield return Error($"{Fmt.Line(i)}.lineTotal", Fmt.Amount(expected), Fmt.Amount(t),
                    $"Line {i + 1}: {Fmt.Number(q)} × {Fmt.Amount(p)} = {Fmt.Amount(expected)}, but line total is {Fmt.Amount(t)}.",
                    $"البند {i + 1}: {Fmt.Number(q)} × {Fmt.Amount(p)} = {Fmt.Amount(expected)} لكن مبلغ البند {Fmt.Amount(t)}.");
            }
        }
    }
}

/// <summary>ARI-003: subtotal + VAT = total.</summary>
public sealed class TotalRule : Rule
{
    public override string Code => "ARI-003";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var inv = context.Invoice;
        if (inv.Subtotal is not { } subtotal || inv.VatTotal is not { } vat || inv.Total is not { } total)
        {
            yield break;
        }

        if (Math.Abs(subtotal + vat - total) > 0.01m)
        {
            yield return Error("total", Fmt.Amount(subtotal + vat), Fmt.Amount(total),
                $"Subtotal + VAT = {Fmt.Amount(subtotal + vat)}, but total is {Fmt.Amount(total)}.",
                $"المجموع الفرعي + الضريبة = {Fmt.Amount(subtotal + vat)} لكن الإجمالي {Fmt.Amount(total)}.");
        }
    }
}

/// <summary>VAT-001: VAT amount = Σ(line total × rate). VAT-001 also flags rates not allowed in this jurisdiction (warning).</summary>
public sealed class VatAmountRule : Rule
{
    public override string Code => "VAT-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var inv = context.Invoice;

        for (var i = 0; i < inv.Lines.Count; i++)
        {
            if (inv.Lines[i].VatRate is { } rate && !context.Settings.AllowedVatRates.Contains(rate))
            {
                yield return Warning($"{Fmt.Line(i)}.vatRate", string.Join(" / ", context.Settings.AllowedVatRates.Select(r => Fmt.Percent(r))), Fmt.Percent(rate),
                    $"Line {i + 1}: unusual VAT rate {Fmt.Percent(rate)}.",
                    $"البند {i + 1}: نسبة ضريبة غير معتادة {Fmt.Percent(rate)}.");
            }
        }

        if (inv.VatTotal is not { } vat || inv.Lines.Count == 0 || inv.Lines.Any(l => l.LineTotal is null))
        {
            yield break;
        }

        // Exempt lines (rate null) contribute nothing. If no line shows a rate at all, we can't check.
        if (inv.Lines.All(l => l.VatRate is null) && vat != 0)
        {
            yield break;
        }

        var expected = Math.Round(inv.Lines.Sum(l => l.LineTotal!.Value * (l.VatRate ?? 0m)), 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(expected - vat) > Rounding.Tolerance(inv.Lines.Count))
        {
            yield return Error("vatTotal", Fmt.Amount(expected), Fmt.Amount(vat),
                $"VAT should be {Fmt.Amount(expected)} based on line rates, but invoice shows {Fmt.Amount(vat)}.",
                $"يجب أن تكون الضريبة {Fmt.Amount(expected)} حسب نسب البنود، لكن الفاتورة تُظهر {Fmt.Amount(vat)}.");
        }
    }
}

/// <summary>VAT-002: VAT may only be charged by a VAT-registered supplier (TRN printed).</summary>
public sealed class VatRequiresTrnRule : Rule
{
    public override string Code => "VAT-002";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        if (context.Invoice.VatTotal is > 0 && string.IsNullOrWhiteSpace(context.Invoice.SupplierTrn))
        {
            yield return Error("supplierTrn", "TRN", null,
                "VAT is charged but the supplier TRN is not on the invoice.",
                "تم احتساب ضريبة القيمة المضافة دون ذكر الرقم الضريبي للمورد.");
        }
    }
}
