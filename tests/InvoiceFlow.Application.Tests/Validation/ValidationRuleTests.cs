using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Matching;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Domain.Invoices;
using static InvoiceFlow.Application.Tests.Validation.Fixtures;
using PurchaseOrderStatus = InvoiceFlow.Domain.MasterData.PurchaseOrderStatus;

namespace InvoiceFlow.Application.Tests.Validation;

public class ValidationRuleTests
{
    private static IEnumerable<string> ErrorCodes(ValidationReport r) => r.Errors.Select(f => f.RuleCode).Distinct().Order();

    private static async Task AssertOnlyError(InvoiceDto invoice, string code, ValidationEngine? engine = null, ExtractionResult? extraction = null)
    {
        var report = await ValidateAsync(invoice, engine, extraction);
        Assert.Equal([code], ErrorCodes(report));
        Assert.Equal(ValidationOutcome.NeedsReview, report.Outcome);
    }

    // ---------- baseline ----------

    [Fact]
    public async Task Clean_invoice_is_approved_touchless()
    {
        var report = await ValidateAsync(Invoice);

        Assert.Equal(ValidationOutcome.Approved, report.Outcome);
        Assert.Empty(report.Findings);
        Assert.Equal("SUP-1", report.SupplierId);
        Assert.Equal(InvoiceStatus.Approved, report.NextStatus);
        Assert.All(report.LineMatches, m => Assert.NotNull(m.PoLine));
    }

    [Fact]
    public async Task Engine_given_an_empty_rule_list_still_runs_all_rules()
    {
        // Regression: DI passes an empty IEnumerable<IValidationRule> when none are registered.
        var engine = new ValidationEngine(
            new FakeMasterData([Fixtures.Supplier], [Po]), new FakeHistory(),
            Microsoft.Extensions.Options.Options.Create(new ValidationSettings { CompanyTrns = [OurTrn] }),
            new FixedTime(Today), rules: []);

        var report = await engine.ValidateAsync(new ValidationRequest(Invoice with { Total = 1m }));

        Assert.Equal(ValidationOutcome.NeedsReview, report.Outcome);
    }

    [Fact]
    public void Every_rule_has_a_unique_code()
    {
        var codes = ValidationEngine.DefaultRules.Select(r => r.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    // ---------- extraction ----------

    [Fact]
    public async Task EXT_001_unreadable_value() =>
        await AssertOnlyError(Invoice, "EXT-001", extraction: new ExtractionResult
        {
            Status = ExtractionStatus.Succeeded,
            DocumentType = DocumentType.Invoice,
            Issues = ["dueDate: could not parse date 'soon'"],
            Usage = new ExtractionUsage { Extractor = "t" },
        });

    [Fact]
    public async Task EXT_002_credit_note() =>
        await AssertOnlyError(Invoice, "EXT-002", extraction: new ExtractionResult
        {
            Status = ExtractionStatus.Succeeded,
            DocumentType = DocumentType.CreditNote,
            Usage = new ExtractionUsage { Extractor = "t" },
        });

    // ---------- format ----------

    [Fact]
    public async Task FMT_001_missing_invoice_number() =>
        await AssertOnlyError(Invoice with { InvoiceNumber = null }, "FMT-001");

    [Fact]
    public async Task FMT_001_reports_each_missing_field()
    {
        var report = await ValidateAsync(Invoice with { InvoiceDate = null, Currency = null });
        Assert.Equal(["currency", "invoiceDate"], report.Errors.Where(f => f.RuleCode == "FMT-001").Select(f => f.Field).Order());
    }

    [Fact]
    public async Task FMT_002_customer_trn_not_15_digits()
    {
        var report = await ValidateAsync(Invoice with { CustomerTrn = "12345" });
        Assert.Contains(report.Errors, f => f.RuleCode == "FMT-002" && f.Field == "customerTrn");
    }

    [Fact]
    public async Task FMT_003_future_date_is_error() =>
        await AssertOnlyError(Invoice with { InvoiceDate = Today.AddDays(3), DueDate = Today.AddDays(30) }, "FMT-003");

    [Fact]
    public async Task FMT_003_old_invoice_is_only_a_warning()
    {
        var report = await ValidateAsync(Invoice with { InvoiceDate = Today.AddDays(-400), DueDate = null });
        Assert.Equal(ValidationOutcome.Approved, report.Outcome);
        Assert.Contains(report.Findings, f => f.RuleCode == "FMT-003" && f.Severity == Severity.Warning);
    }

    [Fact]
    public async Task CUS_001_invoice_for_another_company() =>
        await AssertOnlyError(Invoice with { CustomerTrn = "100222333444003" }, "CUS-001");

    // ---------- arithmetic / VAT ----------

    [Fact]
    public async Task ARI_001_lines_do_not_add_up_to_subtotal() =>
        // Subtotal and total both off by the same amount; VAT is computed from lines, so it still matches.
        await AssertOnlyError(Invoice with { Subtotal = 1200m, VatTotal = 55m, Total = 1255m }, "ARI-001");

    [Fact]
    public async Task ARI_002_quantity_times_price_wrong()
    {
        var report = await ValidateAsync(Invoice.WithLine(0, l => l with { LineTotal = 1010m }) with { Subtotal = 1110m, VatTotal = 55.50m, Total = 1165.50m });
        Assert.Equal(["ARI-002"], ErrorCodes(report));
    }

    [Fact]
    public async Task ARI_003_total_wrong() =>
        await AssertOnlyError(Invoice with { Total = 1150m }, "ARI-003");

    [Fact]
    public async Task VAT_001_wrong_vat_amount() =>
        await AssertOnlyError(Invoice with { VatTotal = 60m, Total = 1160m }, "VAT-001");

    [Fact]
    public async Task VAT_001_unusual_rate_is_warning()
    {
        var invoice = Invoice.WithLine(1, l => l with { VatRate = 0.10m }) with { VatTotal = 60m, Total = 1160m };
        var report = await ValidateAsync(invoice);
        Assert.Contains(report.Findings, f => f.RuleCode == "VAT-001" && f.Severity == Severity.Warning && f.Field == "lines[1].vatRate");
    }

    [Fact]
    public async Task VAT_001_zero_rated_invoice_is_fine()
    {
        var invoice = Invoice.WithLine(0, l => l with { VatRate = 0m }).WithLine(1, l => l with { VatRate = 0m }) with { VatTotal = 0m, Total = 1100m };
        Assert.Equal(ValidationOutcome.Approved, (await ValidateAsync(invoice)).Outcome);
    }

    [Fact]
    public async Task VAT_002_vat_without_supplier_trn()
    {
        var report = await ValidateAsync(Invoice with { SupplierTrn = null });
        Assert.Equal(["VAT-002"], ErrorCodes(report));
        Assert.Equal(SupplierMatchMethod.Name, report.SupplierMatchMethod); // still found by name
    }

    // ---------- supplier ----------

    [Fact]
    public async Task SUP_001_unknown_supplier()
    {
        var report = await ValidateAsync(Invoice with { SupplierName = "Totally Unknown Trading", SupplierTrn = "100000000000003" });
        Assert.Contains("SUP-001", ErrorCodes(report));
        Assert.Null(report.SupplierId);
    }

    [Fact]
    public async Task SUP_001_inactive_supplier()
    {
        var engine = Engine(suppliers: [Fixtures.Supplier with { IsActive = false }]);
        await AssertOnlyError(Invoice, "SUP-001", engine);
    }

    [Fact]
    public async Task SUP_002_changed_iban_is_error() =>
        await AssertOnlyError(Invoice with { Iban = "AE990000000000000000001" }, "SUP-002");

    [Fact]
    public async Task SUP_002_iban_with_spaces_is_same_iban()
    {
        var report = await ValidateAsync(Invoice with { Iban = "AE05 0873 1007 6127 2610 650" });
        Assert.Equal(ValidationOutcome.Approved, report.Outcome);
    }

    [Fact]
    public async Task SUP_003_name_matches_but_trn_differs()
    {
        var report = await ValidateAsync(Invoice with { SupplierTrn = "100000000000003" });
        Assert.Equal(["SUP-003"], ErrorCodes(report));
        Assert.Equal("SUP-1", report.SupplierId);
    }

    [Fact]
    public async Task Arabic_supplier_name_resolves_without_trn()
    {
        // No TRN → only zero-rated invoices pass VAT-002, so make it zero-rated.
        var invoice = (Invoice with { SupplierName = "شركة نجمة الخليج للتجارة", SupplierTrn = null, VatTotal = 0m, Total = 1100m })
            .WithLine(0, l => l with { VatRate = 0m }).WithLine(1, l => l with { VatRate = 0m });
        var report = await ValidateAsync(invoice);
        Assert.Equal("SUP-1", report.SupplierId);
        Assert.Equal(ValidationOutcome.Approved, report.Outcome);
    }

    // ---------- duplicates ----------

    [Fact]
    public async Task DUP_001_same_file()
    {
        var history = new FakeHistory();
        history.Hashes.Add("abc");
        var report = await ValidateAsync(Invoice, Engine(history: history), sha: "abc");
        Assert.Equal(ValidationOutcome.Duplicate, report.Outcome);
        Assert.Equal(InvoiceStatus.Duplicate, report.NextStatus);
    }

    [Fact]
    public async Task DUP_002_same_supplier_and_number()
    {
        var history = new FakeHistory();
        history.Invoices.Add(new PriorInvoice("doc-1", "SUP-1", Invoice.InvoiceNumber!, 1155m, Invoice.InvoiceDate));
        var report = await ValidateAsync(Invoice, Engine(history: history));
        Assert.Equal(ValidationOutcome.Duplicate, report.Outcome);
        Assert.Contains(report.Errors, f => f.RuleCode == "DUP-002");
    }

    // ---------- purchase order ----------

    [Fact]
    public async Task PO_001_missing_po_number() =>
        await AssertOnlyError(Invoice with { PoNumber = null }, "PO-001");

    [Fact]
    public async Task PO_001_missing_po_allowed_when_not_required()
    {
        var report = await ValidateAsync(Invoice with { PoNumber = null }, Engine(configure: s => s.RequirePurchaseOrder = false));
        Assert.Equal(ValidationOutcome.Approved, report.Outcome);
    }

    [Fact]
    public async Task PO_001_unknown_po() =>
        await AssertOnlyError(Invoice with { PoNumber = "PO-00-000000" }, "PO-001");

    [Fact]
    public async Task PO_001_closed_po() =>
        await AssertOnlyError(Invoice, "PO-001", Engine(pos: [Po with { Status = PurchaseOrderStatus.Closed }]));

    [Fact]
    public async Task PO_001_po_of_another_supplier() =>
        await AssertOnlyError(Invoice, "PO-001", Engine(pos: [Po with { SupplierId = "SUP-2" }]));

    [Fact]
    public async Task PO_002_line_not_on_po()
    {
        var extra = new InvoiceLineDto { Sku = "XYZ", Description = "Gold plated stapler", Quantity = 1, UnitPrice = 500m, LineTotal = 500m, VatRate = 0.05m };
        var invoice = Invoice with { Lines = [.. Invoice.Lines, extra], Subtotal = 1600m, VatTotal = 80m, Total = 1680m };
        var report = await ValidateAsync(invoice);
        Assert.Equal(["PO-002"], ErrorCodes(report));
        Assert.Equal("lines[2]", Assert.Single(report.Errors).Field);
    }

    [Fact]
    public async Task PO_003_price_above_po()
    {
        var invoice = Invoice.WithLine(0, l => l with { UnitPrice = 108m, LineTotal = 1080m }) with { Subtotal = 1180m, VatTotal = 59m, Total = 1239m };
        await AssertOnlyError(invoice, "PO-003");
    }

    [Fact]
    public async Task PO_003_price_within_tolerance_is_ok()
    {
        // 0.5% of 100 = 0.50
        var invoice = Invoice.WithLine(0, l => l with { UnitPrice = 100.40m, LineTotal = 1004m }) with { Subtotal = 1104m, VatTotal = 55.20m, Total = 1159.20m };
        Assert.Equal(ValidationOutcome.Approved, (await ValidateAsync(invoice)).Outcome);
    }

    [Fact]
    public async Task PO_003_price_below_po_is_warning()
    {
        var invoice = Invoice.WithLine(0, l => l with { UnitPrice = 90m, LineTotal = 900m }) with { Subtotal = 1000m, VatTotal = 50m, Total = 1050m };
        var report = await ValidateAsync(invoice);
        Assert.Equal(ValidationOutcome.Approved, report.Outcome);
        Assert.Contains(report.Findings, f => f.RuleCode == "PO-003" && f.Severity == Severity.Warning);
    }

    [Fact]
    public async Task PO_004_quantity_above_remaining()
    {
        var po = Po with { Lines = [Po.Lines[0] with { QuantityInvoiced = 4 }, Po.Lines[1]] };
        await AssertOnlyError(Invoice, "PO-004", Engine(pos: [po]));
    }

    [Fact]
    public async Task PO_005_currency_differs() =>
        await AssertOnlyError(Invoice, "PO-005", Engine(pos: [Po with { Currency = "USD" }]));

    // ---------- goods receipt / approval ----------

    [Fact]
    public async Task GRN_001_not_received_when_three_way_match_required()
    {
        var po = Po with { Lines = [Po.Lines[0] with { QuantityReceived = 6 }, Po.Lines[1]] };
        await AssertOnlyError(Invoice, "GRN-001", Engine(suppliers: [Fixtures.Supplier with { RequiresGoodsReceipt = true }], pos: [po]));
    }

    [Fact]
    public async Task GRN_001_ignored_without_three_way_match()
    {
        var po = Po with { Lines = [Po.Lines[0] with { QuantityReceived = 0 }, Po.Lines[1]] };
        Assert.Equal(ValidationOutcome.Approved, (await ValidateAsync(Invoice, Engine(pos: [po]))).Outcome);
    }

    [Fact]
    public async Task APR_001_large_invoice_needs_second_approver()
    {
        var report = await ValidateAsync(Invoice, Engine(configure: s => s.ApprovalThreshold = 1000m));
        Assert.Equal(ValidationOutcome.PendingApproval, report.Outcome);
        Assert.Equal(InvoiceStatus.PendingApproval, report.NextStatus);
        Assert.Contains(report.Findings, f => f.RuleCode == "APR-001" && f.Severity == Severity.Info);
    }

    [Fact]
    public async Task Errors_win_over_approval()
    {
        var report = await ValidateAsync(Invoice with { Total = 1.00m }, Engine(configure: s => s.ApprovalThreshold = 0.5m));
        Assert.Equal(ValidationOutcome.NeedsReview, report.Outcome);
    }

    [Fact]
    public async Task Every_finding_has_english_and_arabic_message()
    {
        var invoice = Invoice with { InvoiceNumber = null, Total = 1m, Iban = "AE00", CustomerTrn = "1", PoNumber = "nope" };
        var report = await ValidateAsync(invoice);
        Assert.NotEmpty(report.Findings);
        Assert.All(report.Findings, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.MessageEn));
            Assert.Contains(f.MessageAr, c => c is >= '؀' and <= 'ۿ');
        });
    }
}
