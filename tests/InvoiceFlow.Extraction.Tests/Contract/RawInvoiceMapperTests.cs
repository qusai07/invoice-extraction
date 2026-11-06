using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Extraction.Contract;

namespace InvoiceFlow.Extraction.Tests.Contract;

public class RawInvoiceMapperTests
{
    private const string ArabicModelOutput = """
    {
      "document_type": "invoice",
      "supplier_name": { "value": "مؤسسة النور للقرطاسية", "page": 1 },
      "supplier_trn": { "value": "١٠٠١٢٣٤٥٦٧٠٠٠٠٣", "page": 1 },
      "customer_trn": { "value": null, "page": null },
      "invoice_number": { "value": "INV-2026-00412", "page": 1 },
      "invoice_date": { "value": "١٤ أكتوبر ٢٠٢٦", "page": 1 },
      "due_date": { "value": "13/11/2026", "page": 1 },
      "po_number": { "value": "PO-45-001178", "page": 1 },
      "currency": { "value": "د.إ", "page": 1 },
      "subtotal": { "value": "٣٬٥٥٠٫٠٠", "page": 1 },
      "vat_total": { "value": "١٧٧٫٥٠", "page": 1 },
      "total": { "value": "٣٬٧٢٧٫٥٠", "page": 2 },
      "iban": { "value": "AE07 0331 2345 6789 0123 456", "page": 2 },
      "lines": [
        { "sku": "PAP-A4-80", "description": "ورق A4", "quantity": "٢٠", "unit_price": "٩٥٫٠٠", "line_total": "١٬٩٠٠٫٠٠", "vat_rate": "٥٪", "page": 1 },
        { "sku": null, "description": "حبر طابعة", "quantity": "4", "unit_price": "412.50", "line_total": "1,650.00", "vat_rate": "5%", "page": 1 }
      ]
    }
    """;

    [Fact]
    public void Contract_resources_are_embedded()
    {
        Assert.Contains("transcribe", ExtractionContract.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"lines\"", ExtractionContract.Schema, StringComparison.Ordinal);
    }

    [Fact]
    public void Maps_arabic_invoice_to_typed_values()
    {
        var result = RawInvoiceMapper.Map(ExtractionContract.Deserialize(ArabicModelOutput));
        var inv = result.Invoice;

        Assert.Equal(DocumentType.Invoice, result.DocumentType);
        Assert.Empty(result.Issues);
        Assert.Equal("مؤسسة النور للقرطاسية", inv.SupplierName);
        Assert.Equal("100123456700003", inv.SupplierTrn);
        Assert.Null(inv.CustomerTrn);
        Assert.Equal(new DateOnly(2026, 10, 14), inv.InvoiceDate);
        Assert.Equal(new DateOnly(2026, 11, 13), inv.DueDate);
        Assert.Equal("AED", inv.Currency);
        Assert.Equal(3550.00m, inv.Subtotal);
        Assert.Equal(177.50m, inv.VatTotal);
        Assert.Equal(3727.50m, inv.Total);
        Assert.Equal("AE070331234567890123456", inv.Iban);

        Assert.Equal(2, inv.Lines.Count);
        Assert.Equal(new InvoiceLineDto { Sku = "PAP-A4-80", Description = "ورق A4", Quantity = 20, UnitPrice = 95.00m, LineTotal = 1900.00m, VatRate = 0.05m }, inv.Lines[0]);
        Assert.Null(inv.Lines[1].Sku);
        Assert.Equal(1650.00m, inv.Lines[1].LineTotal);
    }

    [Fact]
    public void Evidence_keeps_text_as_printed_with_page()
    {
        var result = RawInvoiceMapper.Map(ExtractionContract.Deserialize(ArabicModelOutput));

        var total = Assert.Single(result.Evidence, e => e.Field == "total");
        Assert.Equal("٣٬٧٢٧٫٥٠", total.SourceText);
        Assert.Equal(2, total.Page);
        Assert.Contains(result.Evidence, e => e.Field == "lines[1].unitPrice" && e.SourceText == "412.50");
        Assert.DoesNotContain(result.Evidence, e => e.Field == "customerTrn");
    }

    [Fact]
    public void Unparseable_values_become_null_with_an_issue()
    {
        var raw = ExtractionContract.Deserialize(ArabicModelOutput) with
        {
            Total = new RawField("see attached", 1),
            InvoiceDate = new RawField("sometime in October", 1),
            Currency = new RawField("shells", 1),
        };

        var result = RawInvoiceMapper.Map(raw);

        Assert.Null(result.Invoice.Total);
        Assert.Null(result.Invoice.InvoiceDate);
        Assert.Null(result.Invoice.Currency);
        Assert.Equal(3, result.Issues.Count);
        Assert.Contains(result.Issues, i => i.StartsWith("total:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("credit_note", DocumentType.CreditNote)]
    [InlineData("other", DocumentType.Other)]
    [InlineData("something-new", DocumentType.Unknown)]
    public void Maps_document_type(string raw, DocumentType expected) =>
        Assert.Equal(expected, RawInvoiceMapper.Map(new RawExtraction { DocumentType = raw }).DocumentType);
}
