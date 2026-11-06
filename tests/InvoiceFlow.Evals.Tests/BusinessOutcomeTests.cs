using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Evals.Dataset;
using InvoiceFlow.Evals.Scoring;
using InvoiceFlow.Evals.Synthetic;
using InvoiceFlow.Infrastructure.MockErp;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Evals.Tests;

public class BusinessOutcomeTests
{
    private sealed class FixedTime(DateOnly d) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

    private static (List<SyntheticDocument> Docs, ValidationEngine Engine) Generate(int count)
    {
        var generator = new SyntheticInvoiceGenerator(42);
        var docs = generator.Generate(count).ToList();
        var data = generator.BuildMasterData();
        var engine = new ValidationEngine(
            new InMemoryMasterDataService(data),
            new InMemoryInvoiceHistory(),
            Options.Create(new ValidationSettings { CompanyTrns = data.CompanyTrns.ToList(), ApprovalThreshold = SyntheticInvoiceGenerator.ApprovalThreshold }),
            new FixedTime(SyntheticInvoiceGenerator.AsOf));
        return (docs, engine);
    }

    [Fact]
    public async Task Every_generated_scenario_gets_its_expected_decision()
    {
        var (docs, engine) = Generate(150);

        foreach (var doc in docs)
        {
            var report = await engine.ValidateAsync(new ValidationRequest(doc.Expected.Invoice));
            var errors = report.Errors.Select(e => e.RuleCode).Distinct().Order(StringComparer.Ordinal);

            Assert.True(doc.Expected.Validation!.Outcome == report.Outcome,
                $"{doc.Id} ({doc.Expected.Validation.Scenario}): expected {doc.Expected.Validation.Outcome}, got {report.Outcome} [{string.Join(", ", errors)}]");
            Assert.Equal(doc.Expected.Validation.ErrorCodes, errors);
        }
    }

    [Fact]
    public void All_scenarios_are_represented()
    {
        var scenarios = Generate(150).Docs.Select(d => d.Expected.Validation!.Scenario).ToHashSet();
        Assert.Superset(new HashSet<string> { "clean", "no-po", "price-increase", "qty-exceeds", "unknown-po", "iban-changed", "wrong-customer", "not-received" }, scenarios);
    }

    private static DocumentScore Score(InvoiceDto truth, InvoiceDto extracted, ValidationOutcome expected, ValidationOutcome predicted)
    {
        var item = new DatasetItem("d", "d.pdf", new ExpectedFile { Invoice = truth, Validation = new ExpectedValidation("x", expected, []) });
        var result = new ExtractionResult { Status = ExtractionStatus.Succeeded, Invoice = extracted, Usage = new ExtractionUsage { Extractor = "t" } };
        return InvoiceScorer.Score(item, result, new ValidationReport { Outcome = predicted, Findings = [] });
    }

    private static readonly InvoiceDto Truth = new() { InvoiceNumber = "INV-1", Total = 100m, Lines = [] };

    [Fact]
    public void Approved_with_wrong_total_is_a_false_accept()
    {
        var s = Score(Truth, Truth with { Total = 10m }, ValidationOutcome.Approved, ValidationOutcome.Approved);
        Assert.True(s.FalseAccept);
        Assert.False(s.DecisionCorrect);
    }

    [Fact]
    public void Approved_when_it_should_be_reviewed_is_a_false_accept() =>
        Assert.True(Score(Truth, Truth, ValidationOutcome.NeedsReview, ValidationOutcome.Approved).FalseAccept);

    [Fact]
    public void Review_when_it_could_pass_is_a_false_reject_not_a_false_accept()
    {
        var s = Score(Truth, Truth, ValidationOutcome.Approved, ValidationOutcome.NeedsReview);
        Assert.True(s.FalseReject);
        Assert.False(s.FalseAccept);
    }

    [Fact]
    public void Wrong_supplier_name_spelling_is_not_critical()
    {
        // Supplier identity comes from TRN; a name typo alone doesn't post wrong money.
        var s = Score(Truth with { SupplierName = "Gulf Star" }, Truth with { SupplierName = "Gulf Starr" }, ValidationOutcome.Approved, ValidationOutcome.Approved);
        Assert.False(s.FalseAccept);
    }
}
