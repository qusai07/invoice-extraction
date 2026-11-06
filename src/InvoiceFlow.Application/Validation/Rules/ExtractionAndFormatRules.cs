using InvoiceFlow.Application.Extraction;

namespace InvoiceFlow.Application.Validation.Rules;

/// <summary>EXT-001: the extractor read a value it could not normalize (field is null) → a person must check it.</summary>
public sealed class UnreadableValuesRule : Rule
{
    public override string Code => "EXT-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context) =>
        (context.Extraction?.Issues ?? []).Select(issue => Error(
            issue.Split(':', 2)[0], null, null,
            $"Could not read a value: {issue}",
            $"تعذّرت قراءة قيمة: {issue}"));
}

/// <summary>EXT-002: the document is not a regular invoice (credit note, quotation, …).</summary>
public sealed class DocumentTypeRule : Rule
{
    public override string Code => "EXT-002";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var type = context.Extraction?.DocumentType;
        if (type is null or DocumentType.Invoice)
        {
            yield break;
        }

        yield return Error("documentType", "Invoice", type.ToString(),
            $"Document is not an invoice ({type}).",
            $"المستند ليس فاتورة ({type}).");
    }
}

/// <summary>FMT-001: required fields present.</summary>
public sealed class RequiredFieldsRule : Rule
{
    public override string Code => "FMT-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var i = context.Invoice;

        if (string.IsNullOrWhiteSpace(i.SupplierName) && string.IsNullOrWhiteSpace(i.SupplierTrn))
        {
            yield return Missing("supplierName", "supplier name or TRN", "اسم المورد أو الرقم الضريبي");
        }

        if (string.IsNullOrWhiteSpace(i.InvoiceNumber))
        {
            yield return Missing("invoiceNumber", "invoice number", "رقم الفاتورة");
        }

        if (i.InvoiceDate is null)
        {
            yield return Missing("invoiceDate", "invoice date", "تاريخ الفاتورة");
        }

        if (string.IsNullOrWhiteSpace(i.Currency))
        {
            yield return Missing("currency", "currency", "العملة");
        }

        if (i.Total is null)
        {
            yield return Missing("total", "total amount", "المبلغ الإجمالي");
        }

        if (i.Lines.Count == 0)
        {
            yield return Missing("lines", "line items", "بنود الفاتورة");
        }
    }

    private ValidationFinding Missing(string field, string en, string ar) =>
        Error(field, null, null, $"Missing {en}.", $"{ar} غير موجود.");
}

/// <summary>FMT-002: UAE TRNs are 15 digits.</summary>
public sealed class TrnFormatRule : Rule
{
    public override string Code => "FMT-002";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        foreach (var (field, value, en, ar) in new[]
                 {
                     ("supplierTrn", context.Invoice.SupplierTrn, "Supplier TRN", "الرقم الضريبي للمورد"),
                     ("customerTrn", context.Invoice.CustomerTrn, "Customer TRN", "الرقم الضريبي للعميل"),
                 })
        {
            if (value is not null && (value.Length != 15 || !value.All(char.IsAsciiDigit)))
            {
                yield return Error(field, "15 digits", value,
                    $"{en} must be 15 digits.",
                    $"{ar} يجب أن يتكون من 15 رقماً.");
            }
        }
    }
}

/// <summary>FMT-003: invoice date not in the future, not too old; due date not before invoice date.</summary>
public sealed class InvoiceDateRule : Rule
{
    public override string Code => "FMT-003";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        if (context.Invoice.InvoiceDate is not { } date)
        {
            yield break;
        }

        if (date > context.Today)
        {
            yield return Error("invoiceDate", $"≤ {Fmt.Date(context.Today)}", Fmt.Date(date),
                "Invoice date is in the future.", "تاريخ الفاتورة في المستقبل.");
        }
        else if (date < context.Today.AddDays(-context.Settings.MaxInvoiceAgeDays))
        {
            yield return Warning("invoiceDate", $"≥ {Fmt.Date(context.Today.AddDays(-context.Settings.MaxInvoiceAgeDays))}", Fmt.Date(date),
                $"Invoice is older than {context.Settings.MaxInvoiceAgeDays} days.",
                $"عمر الفاتورة أكثر من {context.Settings.MaxInvoiceAgeDays} يوماً.");
        }

        if (context.Invoice.DueDate is { } due && due < date)
        {
            yield return Warning("dueDate", $"≥ {Fmt.Date(date)}", Fmt.Date(due),
                "Due date is before the invoice date.", "تاريخ الاستحقاق قبل تاريخ الفاتورة.");
        }
    }
}

/// <summary>CUS-001: invoice is addressed to one of our TRNs (catches invoices sent to the wrong company).</summary>
public sealed class CustomerTrnRule : Rule
{
    public override string Code => "CUS-001";

    public override IEnumerable<ValidationFinding> Evaluate(ValidationContext context)
    {
        var trn = context.Invoice.CustomerTrn;
        var ours = context.Settings.CompanyTrns;
        if (trn is null || ours.Count == 0 || ours.Contains(trn))
        {
            yield break;
        }

        yield return Error("customerTrn", string.Join(", ", ours), trn,
            "Invoice is addressed to a different company TRN.",
            "الفاتورة موجّهة إلى رقم ضريبي لشركة أخرى.");
    }
}
