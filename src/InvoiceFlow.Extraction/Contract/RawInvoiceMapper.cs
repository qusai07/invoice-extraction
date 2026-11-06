using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Extraction.Normalization;

namespace InvoiceFlow.Extraction.Contract;

/// <summary>
/// Turns transcribed text into typed values. Anything that can't be parsed becomes <c>null</c> + an issue,
/// so the validation rules (not this mapper) decide what happens next.
/// </summary>
public static class RawInvoiceMapper
{
    public sealed record Result(InvoiceDto Invoice, DocumentType DocumentType, IReadOnlyList<FieldEvidence> Evidence, IReadOnlyList<string> Issues);

    public static Result Map(RawExtraction raw, bool dayFirstDates = true)
    {
        var evidence = new List<FieldEvidence>();
        var issues = new List<string>();

        string? Text(string field, RawField? f)
        {
            var value = TextNormalizer.CleanOrNull(f?.Value);
            Track(field, f);
            return value;
        }

        decimal? Amount(string field, string? rawValue, int? page)
        {
            if (TextNormalizer.CleanOrNull(rawValue) is null)
            {
                return null;
            }

            evidence.Add(new FieldEvidence(field, page, rawValue!));
            if (AmountParser.TryParse(rawValue, out var v))
            {
                return v;
            }

            issues.Add($"{field}: could not parse amount '{rawValue}'");
            return null;
        }

        DateOnly? Date(string field, RawField? f)
        {
            if (TextNormalizer.CleanOrNull(f?.Value) is null)
            {
                return null;
            }

            Track(field, f);
            if (DateParser.TryParse(f!.Value, out var d, dayFirstDates))
            {
                return d;
            }

            issues.Add($"{field}: could not parse date '{f.Value}'");
            return null;
        }

        void Track(string field, RawField? f)
        {
            if (TextNormalizer.CleanOrNull(f?.Value) is not null)
            {
                evidence.Add(new FieldEvidence(field, f!.Page, f.Value!));
            }
        }

        string? currency = null;
        if (TextNormalizer.CleanOrNull(raw.Currency?.Value) is not null)
        {
            Track("currency", raw.Currency);
            currency = CurrencyNormalizer.Normalize(raw.Currency!.Value);
            if (currency is null)
            {
                issues.Add($"currency: unknown currency '{raw.Currency.Value}'");
            }
        }

        var lines = new List<InvoiceLineDto>(raw.Lines.Count);
        for (var i = 0; i < raw.Lines.Count; i++)
        {
            var l = raw.Lines[i];
            var prefix = $"lines[{i}]";

            decimal? vatRate = null;
            if (TextNormalizer.CleanOrNull(l.VatRate) is not null)
            {
                evidence.Add(new FieldEvidence($"{prefix}.vatRate", l.Page, l.VatRate!));
                if (!PercentParser.TryParse(l.VatRate, out vatRate))
                {
                    issues.Add($"{prefix}.vatRate: could not parse rate '{l.VatRate}'");
                }
            }

            lines.Add(new InvoiceLineDto
            {
                Sku = TextNormalizer.CleanOrNull(l.Sku),
                Description = TextNormalizer.CleanOrNull(l.Description),
                Quantity = Amount($"{prefix}.quantity", l.Quantity, l.Page),
                UnitPrice = Amount($"{prefix}.unitPrice", l.UnitPrice, l.Page),
                LineTotal = Amount($"{prefix}.lineTotal", l.LineTotal, l.Page),
                VatRate = vatRate,
            });
        }

        var trn = Digits("supplierTrn", raw.SupplierTrn);
        var customerTrn = Digits("customerTrn", raw.CustomerTrn);

        var invoice = new InvoiceDto
        {
            SupplierName = Text("supplierName", raw.SupplierName),
            SupplierTrn = trn,
            CustomerTrn = customerTrn,
            InvoiceNumber = Text("invoiceNumber", raw.InvoiceNumber),
            InvoiceDate = Date("invoiceDate", raw.InvoiceDate),
            DueDate = Date("dueDate", raw.DueDate),
            PoNumber = Text("poNumber", raw.PoNumber),
            Currency = currency,
            Lines = lines,
            Subtotal = Amount("subtotal", raw.Subtotal?.Value, raw.Subtotal?.Page),
            VatTotal = Amount("vatTotal", raw.VatTotal?.Value, raw.VatTotal?.Page),
            Total = Amount("total", raw.Total?.Value, raw.Total?.Page),
            Iban = Iban("iban", raw.Iban),
        };

        var documentType = raw.DocumentType switch
        {
            "invoice" => DocumentType.Invoice,
            "credit_note" => DocumentType.CreditNote,
            "other" => DocumentType.Other,
            _ => DocumentType.Unknown,
        };

        return new Result(invoice, documentType, evidence, issues);

        string? Digits(string field, RawField? f)
        {
            if (TextNormalizer.CleanOrNull(f?.Value) is null)
            {
                return null;
            }

            Track(field, f);
            var digits = TextNormalizer.DigitsOnly(f!.Value);
            // Length is checked by validation rule FMT-002, not here.
            return digits.Length == 0 ? null : digits;
        }

        string? Iban(string field, RawField? f)
        {
            if (TextNormalizer.CleanOrNull(f?.Value) is null)
            {
                return null;
            }

            Track(field, f);
            var cleaned = new string(TextNormalizer.Normalize(f!.Value).Where(char.IsAsciiLetterOrDigit).ToArray());
            return cleaned.Length == 0 ? null : cleaned.ToUpperInvariant();
        }
    }
}
