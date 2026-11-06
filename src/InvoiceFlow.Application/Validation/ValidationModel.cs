using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Matching;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Domain.Invoices;
using InvoiceFlow.Domain.MasterData;

namespace InvoiceFlow.Application.Validation;

public enum Severity
{
    /// <summary>Shown to the user; does not change the decision (e.g. approval required).</summary>
    Info,

    /// <summary>Shown to the user; does not block straight-through processing.</summary>
    Warning,

    /// <summary>Blocks straight-through processing → human review.</summary>
    Error,
}

/// <param name="RuleCode">Stable code, e.g. "PO-003". Used in UI, audit, metrics and tests.</param>
/// <param name="Field">Field path the finding is about, e.g. "total" or "lines[2].unitPrice".</param>
public sealed record ValidationFinding(
    string RuleCode,
    Severity Severity,
    string? Field,
    string? Expected,
    string? Actual,
    string MessageEn,
    string MessageAr);

public enum ValidationOutcome
{
    /// <summary>All checks passed → post to ERP without a human (touchless).</summary>
    Approved,

    /// <summary>All checks passed but the amount needs a second approver (4-eyes).</summary>
    PendingApproval,

    /// <summary>At least one error → human review queue.</summary>
    NeedsReview,

    /// <summary>Already received → close without posting.</summary>
    Duplicate,
}

public sealed class ValidationSettings
{
    public const string SectionName = "Validation";

    /// <summary>Invoices at or above this total (in invoice currency) need a second approver. 0 = never.</summary>
    public decimal ApprovalThreshold { get; set; } = 100_000m;

    /// <summary>Invoices older than this get a warning.</summary>
    public int MaxInvoiceAgeDays { get; set; } = 365;

    /// <summary>When true, an invoice without a PO number goes to review.</summary>
    public bool RequirePurchaseOrder { get; set; } = true;

    /// <summary>VAT rates accepted without a warning (UAE: 5%, 0%).</summary>
    public IList<decimal> AllowedVatRates { get; set; } = [0.05m, 0m];

    /// <summary>Our own TRNs. If an invoice names a different customer TRN, it was sent to the wrong company.</summary>
    public IList<string> CompanyTrns { get; set; } = [];

    /// <summary>Tolerance used when the supplier has none.</summary>
    public ToleranceProfile DefaultTolerance { get; set; } = ToleranceProfile.Default;
}

/// <summary>Everything a rule may look at. Built once by the engine; rules are pure functions of it.</summary>
public sealed record ValidationContext
{
    public required InvoiceDto Invoice { get; init; }
    public ExtractionResult? Extraction { get; init; }
    public string? DocumentSha256 { get; init; }
    public required SupplierMatch Supplier { get; init; }
    public PurchaseOrder? PurchaseOrder { get; init; }
    public IReadOnlyList<LineMatch> LineMatches { get; init; } = [];
    public bool HashSeenBefore { get; init; }
    public IReadOnlyList<PriorInvoice> PriorInvoices { get; init; } = [];
    public required ValidationSettings Settings { get; init; }
    public required DateOnly Today { get; init; }

    public ToleranceProfile Tolerance => Supplier.Supplier?.Tolerance ?? Settings.DefaultTolerance;
}

public sealed record ValidationReport
{
    public const string RulesetVersion = "2026.11-1";

    public required ValidationOutcome Outcome { get; init; }
    public required IReadOnlyList<ValidationFinding> Findings { get; init; }
    public string? SupplierId { get; init; }
    public SupplierMatchMethod SupplierMatchMethod { get; init; }
    public string? PoNumber { get; init; }
    public IReadOnlyList<LineMatch> LineMatches { get; init; } = [];
    public string Ruleset { get; init; } = RulesetVersion;

    /// <summary>Admin settings version in force (0 = configuration defaults).</summary>
    public int SettingsVersion { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<ValidationFinding> Errors => Findings.Where(f => f.Severity == Severity.Error);

    /// <summary>Next lifecycle status for this outcome (from <see cref="InvoiceStatus.Validating"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public InvoiceStatus NextStatus => Outcome switch
    {
        ValidationOutcome.Approved => InvoiceStatus.Approved,
        ValidationOutcome.PendingApproval => InvoiceStatus.PendingApproval,
        ValidationOutcome.Duplicate => InvoiceStatus.Duplicate,
        _ => InvoiceStatus.NeedsReview,
    };
}

/// <summary>One check. Implementations are stateless and side-effect free.</summary>
public interface IValidationRule
{
    string Code { get; }

    IEnumerable<ValidationFinding> Evaluate(ValidationContext context);
}
