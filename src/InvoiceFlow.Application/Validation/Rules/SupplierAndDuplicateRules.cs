using InvoiceFlow.Application.Matching;

namespace InvoiceFlow.Application.Validation.Rules;

/// <summary>SUP-001: supplier found in master data and active.</summary>
public sealed class SupplierKnownRule : Rule
{
    public override string Code => "SUP-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var supplier = context.Supplier.Supplier;
        if (supplier is null)
        {
            yield return Error("supplierName", null, context.Invoice.SupplierName ?? context.Invoice.SupplierTrn,
                "Supplier not found in master data (or the name matches more than one supplier).",
                "المورد غير موجود في البيانات الرئيسية (أو الاسم يطابق أكثر من مورد).");
        }
        else if (!supplier.IsActive)
        {
            yield return Error("supplierName", null, supplier.Name,
                $"Supplier {supplier.Name} is inactive.", $"المورد {supplier.Name} غير نشط.");
        }
    }
}

/// <summary>SUP-002: payment IBAN on the invoice matches the registered IBAN (fraud check).</summary>
public sealed class SupplierIbanRule : Rule
{
    public override string Code => "SUP-002";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var printed = context.Invoice.Iban;
        var supplier = context.Supplier.Supplier;
        if (printed is null || supplier is null)
        {
            yield break;
        }

        if (supplier.Iban is null)
        {
            yield return Warning("iban", null, printed,
                "Invoice shows an IBAN but none is registered for this supplier.",
                "تظهر الفاتورة رقم آيبان ولا يوجد آيبان مسجل لهذا المورد.");
        }
        else if (!string.Equals(Clean(printed), Clean(supplier.Iban), StringComparison.Ordinal))
        {
            yield return Error("iban", supplier.Iban, printed,
                "Bank account (IBAN) differs from the registered one — possible fraud. Verify with the supplier by phone.",
                "الحساب البنكي (الآيبان) مختلف عن المسجل — احتمال احتيال. تحقّق من المورد هاتفياً.");
        }
    }

    private static string Clean(string iban) => new(iban.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

/// <summary>SUP-003: supplier matched by name, but the printed TRN differs from the registered TRN.</summary>
public sealed class SupplierTrnMismatchRule : Rule
{
    public override string Code => "SUP-003";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var supplier = context.Supplier.Supplier;
        var printed = context.Invoice.SupplierTrn;
        if (supplier?.Trn is null || printed is null || context.Supplier.Method == SupplierMatchMethod.Trn)
        {
            yield break;
        }

        if (!string.Equals(printed, supplier.Trn, StringComparison.Ordinal))
        {
            yield return Error("supplierTrn", supplier.Trn, printed,
                "Supplier name matches, but the TRN on the invoice is different from the registered TRN.",
                "اسم المورد مطابق، لكن الرقم الضريبي على الفاتورة يختلف عن المسجل.");
        }
    }
}

/// <summary>DUP-001: the same file was received before.</summary>
public sealed class DuplicateFileRule : Rule
{
    public override string Code => "DUP-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        if (context.HashSeenBefore)
        {
            yield return Error(null, null, context.DocumentSha256,
                "This exact file was already received.", "تم استلام هذا الملف نفسه من قبل.");
        }
    }
}

/// <summary>DUP-002: same supplier + invoice number already received.</summary>
public sealed class DuplicateInvoiceNumberRule : Rule
{
    public override string Code => "DUP-002";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        foreach (var prior in context.PriorInvoices)
        {
            yield return Error("invoiceNumber", null, prior.InvoiceNumber,
                $"Invoice {prior.InvoiceNumber} from this supplier was already received (document {prior.DocumentId}, total {Fmt.Amount(prior.Total)}).",
                $"الفاتورة {prior.InvoiceNumber} من هذا المورد مستلمة مسبقاً (المستند {prior.DocumentId}، الإجمالي {Fmt.Amount(prior.Total)}).");
        }
    }
}
