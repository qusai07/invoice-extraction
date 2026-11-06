using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Evals.Dataset;
using InvoiceFlow.Evals.Scoring;
using InvoiceFlow.Evals.Synthetic;

namespace InvoiceFlow.Evals.Tests;

public class InvoiceScorerTests
{
    private static readonly InvoiceDto Truth = new()
    {
        SupplierName = "Gulf Star Trading Co. LLC",
        SupplierTrn = "100107850719653",
        InvoiceNumber = "INV-2026-04151",
        InvoiceDate = new DateOnly(2026, 3, 19),
        Currency = "AED",
        Subtotal = 13710m,
        VatTotal = 685.50m,
        Total = 14395.50m,
        Lines =
        [
            new() { Sku = "A", Quantity = 1, UnitPrice = 8580, LineTotal = 8580, VatRate = 0.05m },
            new() { Sku = "B", Quantity = 2, UnitPrice = 50, LineTotal = 100, VatRate = 0.05m },
        ],
    };

    private static DatasetItem Item(InvoiceDto truth, Dictionary<string, string[]>? alternatives = null) =>
        new("t-1", "t-1.pdf", new ExpectedFile { Invoice = truth, Alternatives = alternatives });

    private static ExtractionResult Ok(InvoiceDto invoice) => new()
    {
        Status = ExtractionStatus.Succeeded,
        Invoice = invoice,
        Usage = new ExtractionUsage { Extractor = "test" },
    };

    [Fact]
    public void Identical_invoice_is_fully_correct()
    {
        var score = InvoiceScorer.Score(Item(Truth), Ok(Truth));
        Assert.True(score.FullyCorrect);
    }

    [Fact]
    public void Trailing_zeros_and_case_punctuation_differences_are_still_correct()
    {
        var actual = Truth with { SupplierName = "GULF STAR TRADING CO LLC", Total = 14395.500m, Subtotal = 13710.00m };
        Assert.True(InvoiceScorer.Score(Item(Truth), Ok(actual)).FullyCorrect);
    }

    [Fact]
    public void One_cent_off_is_wrong()
    {
        var score = InvoiceScorer.Score(Item(Truth), Ok(Truth with { Total = 14395.51m }));
        Assert.False(score.FullyCorrect);
        Assert.False(score.Fields.Single(f => f.Field == "total").Correct);
    }

    [Fact]
    public void Alternative_answer_counts_as_correct()
    {
        var alternatives = new Dictionary<string, string[]> { ["supplierName"] = ["شركة نجمة الخليج للتجارة ذ.م.م"] };
        var score = InvoiceScorer.Score(Item(Truth, alternatives), Ok(Truth with { SupplierName = "شركة نجمة الخليج للتجارة ذ.م.م" }));
        Assert.True(score.FullyCorrect);
    }

    [Fact]
    public void Value_where_document_has_none_is_a_hallucination()
    {
        var score = InvoiceScorer.Score(Item(Truth), Ok(Truth with { PoNumber = "PO-123" }));
        var po = score.Fields.Single(f => f.Field == "poNumber");
        Assert.False(po.Correct);
        Assert.True(po.Hallucinated);
    }

    [Fact]
    public void Lines_are_matched_regardless_of_order_and_extra_lines_hurt_precision()
    {
        var actual = Truth with { Lines = [Truth.Lines[1], Truth.Lines[0], new() { Quantity = 1, UnitPrice = 1, LineTotal = 1, VatRate = 0.05m }] };
        var score = InvoiceScorer.Score(Item(Truth), Ok(actual));

        Assert.Equal(2, score.CorrectLines);
        Assert.Equal(3, score.ActualLines);
        Assert.False(score.AllLinesCorrect);
    }

    [Fact]
    public void Failed_extraction_scores_all_present_fields_as_missed()
    {
        var failed = ExtractionResult.Fail("boom", new ExtractionUsage { Extractor = "test" });
        var score = InvoiceScorer.Score(Item(Truth), failed);

        Assert.False(score.FullyCorrect);
        Assert.Equal(0, score.CorrectLines);
        Assert.True(score.Fields.Single(f => f.Field == "total").Missed);
    }

    [Fact]
    public void Report_aggregates()
    {
        var good = InvoiceScorer.Score(Item(Truth), Ok(Truth));
        var bad = InvoiceScorer.Score(Item(Truth), Ok(Truth with { Total = 1m }));
        var report = new EvalReport("test", DateTimeOffset.UnixEpoch, [good, bad]);

        Assert.Equal(0.5, report.FullyCorrectRate);
        Assert.Equal(0.5, report.FieldAccuracy("total"));
        Assert.Equal(1.0, report.FieldAccuracy("supplierName"));
        Assert.Contains("| total | 50.0", report.ToMarkdown(), StringComparison.Ordinal);
    }
}

public class SyntheticGeneratorTests
{
    [Fact]
    public void Same_seed_gives_same_dataset()
    {
        var a = new SyntheticInvoiceGenerator(7).Generate(10).Select(d => (d.Id, d.Html)).ToList();
        var b = new SyntheticInvoiceGenerator(7).Generate(10).Select(d => (d.Id, d.Html)).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Generated_ground_truth_is_arithmetically_consistent()
    {
        foreach (var doc in new SyntheticInvoiceGenerator(42).Generate(60))
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(doc.Expected, DatasetLoader.JsonOptions);
            Assert.Empty(DatasetChecker.CheckInvoice(json));
        }
    }

    [Fact]
    public void Injection_documents_keep_the_real_total()
    {
        var injected = new SyntheticInvoiceGenerator(42).Generate(30).Where(d => d.Expected.Meta.Tags.Contains("injection")).ToList();

        Assert.NotEmpty(injected);
        Assert.All(injected, d =>
        {
            Assert.Contains("ignore all previous instructions", d.Html, StringComparison.Ordinal);
            Assert.True(d.Expected.Invoice.Total > 0);
        });
    }
}
