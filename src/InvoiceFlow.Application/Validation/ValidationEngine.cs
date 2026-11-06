using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Matching;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Application.Validation.Rules;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Application.Validation;

/// <param name="DocumentId">When set, this document is excluded from duplicate checks (re-validation after a correction).</param>
public sealed record ValidationRequest(InvoiceDto Invoice, ExtractionResult? Extraction = null, string? DocumentSha256 = null, Guid? DocumentId = null);

/// <summary>
/// Gathers the facts (supplier, PO, line matches, history) once, runs every rule, and decides the outcome.
/// Deterministic: the same request + master data always gives the same report (ADR-0002).
/// </summary>
public sealed class ValidationEngine(
    IMasterDataService masterData,
    IInvoiceHistory history,
    IOptions<ValidationSettings> settings,
    TimeProvider time,
    IEnumerable<IValidationRule>? rules = null,
    int settingsVersion = 0)
{
    /// <summary>All rules, in display order.</summary>
    public static IReadOnlyList<IValidationRule> DefaultRules { get; } =
    [
        new DuplicateFileRule(), new DuplicateInvoiceNumberRule(),
        new DocumentTypeRule(), new UnreadableValuesRule(),
        new RequiredFieldsRule(), new TrnFormatRule(), new InvoiceDateRule(), new CustomerTrnRule(),
        new LinesSumRule(), new LineArithmeticRule(), new TotalRule(), new VatAmountRule(), new VatRequiresTrnRule(),
        new SupplierKnownRule(), new SupplierTrnMismatchRule(), new SupplierIbanRule(),
        new PurchaseOrderExistsRule(), new CurrencyMatchesPoRule(), new LinesMatchedRule(),
        new UnitPriceToleranceRule(), new QuantityRemainingRule(), new GoodsReceivedRule(),
        new ApprovalThresholdRule(),
    ];

    // Never run with zero rules: DI supplies an empty IEnumerable when no rules are registered, and an engine
    // without rules would approve everything. Empty or null → the full default ruleset.
    private readonly IReadOnlyList<IValidationRule> _rules = rules?.ToList() is { Count: > 0 } custom ? custom : DefaultRules;
    private readonly SupplierResolver _supplierResolver = new(masterData);

    public async Task<ValidationReport> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = await BuildContextAsync(request, cancellationToken).ConfigureAwait(false);
        return Evaluate(context, _rules) with { SettingsVersion = settingsVersion };
    }

    public static ValidationReport Evaluate(ValidationContext context, IReadOnlyList<IValidationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);

        var findings = rules.SelectMany(r => r.Evaluate(context)).ToList();
        var errors = findings.Where(f => f.Severity == Severity.Error).ToList();

        var outcome =
            errors.Any(f => f.RuleCode.StartsWith("DUP-", StringComparison.Ordinal)) ? ValidationOutcome.Duplicate
            : errors.Count > 0 ? ValidationOutcome.NeedsReview
            : findings.Any(f => f.RuleCode == "APR-001") ? ValidationOutcome.PendingApproval
            : ValidationOutcome.Approved;

        return new ValidationReport
        {
            Outcome = outcome,
            Findings = findings,
            SupplierId = context.Supplier.Supplier?.Id,
            SupplierMatchMethod = context.Supplier.Method,
            PoNumber = context.PurchaseOrder?.PoNumber,
            LineMatches = context.LineMatches,
        };
    }

    public async Task<ValidationContext> BuildContextAsync(ValidationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invoice = request.Invoice;

        var supplier = await _supplierResolver.ResolveAsync(invoice, cancellationToken).ConfigureAwait(false);

        var po = invoice.PoNumber is null
            ? null
            : await masterData.GetPurchaseOrderAsync(invoice.PoNumber, cancellationToken).ConfigureAwait(false);

        var lineMatches = po is null ? [] : LineMatcher.Match(invoice.Lines, po.Lines);

        var hashSeen = request.DocumentSha256 is not null &&
                       await history.ExistsByHashAsync(request.DocumentSha256, request.DocumentId, cancellationToken).ConfigureAwait(false);

        var priors = supplier.Supplier is not null && invoice.InvoiceNumber is not null
            ? await history.FindAsync(supplier.Supplier.Id, invoice.InvoiceNumber, request.DocumentId, cancellationToken).ConfigureAwait(false)
            : [];

        return new ValidationContext
        {
            Invoice = invoice,
            Extraction = request.Extraction,
            DocumentSha256 = request.DocumentSha256,
            Supplier = supplier,
            PurchaseOrder = po,
            LineMatches = lineMatches,
            HashSeenBefore = hashSeen,
            PriorInvoices = priors,
            Settings = settings.Value,
            Today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime),
        };
    }
}
