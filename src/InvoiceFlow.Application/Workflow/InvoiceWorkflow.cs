using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Application.Observability;
using InvoiceFlow.Application.Security;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Domain.Invoices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Application.Workflow;

public sealed record SubmitRequest(string FileName, string ContentType, ReadOnlyMemory<byte> Content, string Source = "upload");

/// <summary>
/// The invoice lifecycle (PLAN.md §6): pipeline steps run by workers, and human actions from the review UI.
/// Business rules for people (4-eyes, reasons) live here; role checks live in the API policies.
/// </summary>
public sealed partial class InvoiceWorkflow(
    IInvoiceStore store,
    IDocumentStore files,
    IInvoiceHistory history,
    ValidationEngine validation,
    IErpPoster erp,
    IMalwareScanner scanner,
    IOptions<ValidationSettings> settings,
    TimeProvider time,
    ILogger<InvoiceWorkflow> logger,
    IInvoiceExtractor? extractor = null)
{
    public const int MinReasonLength = 10;
    private const string Tenant = "default";

    private DateTimeOffset Now => time.GetUtcNow();

    // ======================= intake =======================

    public async Task<InvoiceCase> SubmitAsync(SubmitRequest request, UserContext user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(user);
        using var activity = InvoiceFlowTelemetry.Source.StartActivity("invoice.submit");

        var sha = Convert.ToHexStringLower(SHA256.HashData(request.Content.Span));

        // 1. What the bytes really are. The client's content type and file extension are not trusted.
        var mediaType = FileSignature.Detect(request.Content.Span);
        if (mediaType is null)
        {
            Reject("unsupported-type", user, sha, request.FileName);
            throw new WorkflowException(WorkflowErrorKind.UnsupportedMediaType, "The file is not a PDF, PNG or JPEG.");
        }

        // 2. Malware scan before anything is stored. Fails closed when the scanner is unavailable.
        ScanResult scan;
        try
        {
            scan = await scanner.ScanAsync(request.Content, ct).ConfigureAwait(false);
        }
        catch (MalwareScannerUnavailableException ex)
        {
            Reject("scanner-unavailable", user, sha, request.FileName);
            LogScannerUnavailable(logger, ex, scanner.Name);
            throw new WorkflowException(WorkflowErrorKind.Unavailable, "Virus scanning is temporarily unavailable. Please try again later.");
        }

        if (scan.Verdict == ScanVerdict.Infected)
        {
            Reject("malware", user, sha, request.FileName);
            LogMalware(logger, user.Id, sha, scan.Signature ?? "unknown", scanner.Name);
            throw new WorkflowException(WorkflowErrorKind.BadRequest, "The file was blocked by the virus scanner.");
        }

        var id = Guid.CreateVersion7();
        activity?.SetTag("invoiceflow.document.id", id);
        var path = await files.SaveAsync(id, FileSignature.ExtensionFor(mediaType), request.Content, ct).ConfigureAwait(false);

        var doc = InvoiceDocument.Receive(id, Tenant, SafeFileName(request.FileName, mediaType), mediaType,
            request.Content.Length, sha, path, request.Source, user.Id, Now);
        InvoiceFlowTelemetry.InvoicesReceived.Add(1, new KeyValuePair<string, object?>("source", request.Source));
        var c = new InvoiceCase(doc);

        // DUP-001 early: don't pay for extraction of a file we already have.
        if (await history.ExistsByHashAsync(sha, id, ct).ConfigureAwait(false))
        {
            doc.TransitionTo(InvoiceStatus.Duplicate, InvoiceDocument.SystemActor, Now, "DUP-001: this exact file was already received.");
        }
        else
        {
            c.JobsToEnqueue.Add(PipelineStep.Extract);
        }

        await store.AddAsync(c, ct).ConfigureAwait(false);
        return c;
    }

    private void Reject(string reason, UserContext user, string sha, string fileName)
    {
        InvoiceFlowTelemetry.UploadsRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
        LogUploadRejected(logger, reason, user.Id, sha, fileName);
    }

    /// <summary>Display name only (never a path): no directories, no control characters, sensible length.</summary>
    private static string SafeFileName(string fileName, string mediaType)
    {
        var name = Path.GetFileName(fileName ?? "");
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('<' or '>' or '"' or '|' or '?' or '*')).ToArray()).Trim();
        if (name.Length == 0)
        {
            name = "document" + FileSignature.ExtensionFor(mediaType);
        }

        return name.Length > 200 ? name[..200] : name;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Upload rejected ({Reason}) from {UserId}: {FileName} sha256 {Sha256}")]
    private static partial void LogUploadRejected(ILogger logger, string reason, string userId, string sha256, string fileName);

    [LoggerMessage(Level = LogLevel.Critical, Message = "MALWARE blocked: user {UserId}, sha256 {Sha256}, signature {Signature} ({Scanner})")]
    private static partial void LogMalware(ILogger logger, string userId, string sha256, string signature, string scanner);

    [LoggerMessage(Level = LogLevel.Error, Message = "Malware scanner {Scanner} unavailable; upload refused (fail closed)")]
    private static partial void LogScannerUnavailable(ILogger logger, Exception ex, string scanner);

    // ======================= pipeline steps (worker) =======================

    public Task RunStepAsync(Guid id, PipelineStep step, CancellationToken ct = default) => step switch
    {
        PipelineStep.Extract => ExtractAsync(id, ct),
        PipelineStep.Validate => ValidateStepAsync(id, ct),
        PipelineStep.Post => PostAsync(id, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null),
    };

    private async Task ExtractAsync(Guid id, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct).ConfigureAwait(false);
        var doc = c.Document;

        if (doc.Status == InvoiceStatus.Received)
        {
            doc.TransitionTo(InvoiceStatus.Preprocessing, InvoiceDocument.SystemActor, Now);
            doc.TransitionTo(InvoiceStatus.Extracting, InvoiceDocument.SystemActor, Now);
            await store.SaveAsync(c, ct).ConfigureAwait(false); // visible progress; also the retry point
        }
        else if (doc.Status != InvoiceStatus.Extracting)
        {
            return; // already done (job re-delivered) — idempotent
        }

        if (extractor is null)
        {
            c.Invoice = new InvoiceDto();
            doc.TransitionTo(InvoiceStatus.NeedsReview, InvoiceDocument.SystemActor, Now, "Extraction is not configured; enter data manually.");
            await store.SaveAsync(c, ct).ConfigureAwait(false);
            return;
        }

        var bytes = await files.ReadAsync(doc.StoragePath, ct).ConfigureAwait(false);
        var result = await extractor.ExtractAsync(new DocumentInput(doc.FileName, doc.ContentType, bytes), ct).ConfigureAwait(false);
        c.Extraction = result;
        InvoiceFlowTelemetry.ExtractionCost.Record((double)result.Usage.CostUsd,
            new KeyValuePair<string, object?>("model", result.Usage.Model),
            new KeyValuePair<string, object?>("status", result.Status.ToString()));

        if (result.Status == ExtractionStatus.Failed || result.Invoice is null)
        {
            c.Invoice = new InvoiceDto();
            doc.TransitionTo(InvoiceStatus.NeedsReview, InvoiceDocument.SystemActor, Now, $"Extraction failed: {result.Error}");
        }
        else
        {
            c.Invoice = result.Invoice;
            UpdateSummary(c, null);
            doc.TransitionTo(InvoiceStatus.Extracted, InvoiceDocument.SystemActor, Now,
                $"{result.Usage.Extractor} {result.Usage.Model} prompt {result.Usage.PromptVersion}, {result.Usage.Pages} page(s), ${result.Usage.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}");
            c.JobsToEnqueue.Add(PipelineStep.Validate);
        }

        await store.SaveAsync(c, ct).ConfigureAwait(false);
    }

    private async Task ValidateStepAsync(Guid id, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct).ConfigureAwait(false);
        if (c.Document.Status == InvoiceStatus.Extracted)
        {
            c.Document.TransitionTo(InvoiceStatus.Validating, InvoiceDocument.SystemActor, Now);
        }
        else if (c.Document.Status != InvoiceStatus.Validating)
        {
            return;
        }

        await ValidateAndRouteAsync(c, InvoiceDocument.SystemActor, ct).ConfigureAwait(false);
        await store.SaveAsync(c, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the rules on the current data and moves Validating → outcome. Caller saves.</summary>
    /// <param name="humanReviewed">
    /// After a person has corrected the data, extraction-time findings (EXT-001 unreadable values, EXT-002 document type)
    /// no longer apply: the person has looked at the document and typed the values.
    /// </param>
    private async Task ValidateAndRouteAsync(InvoiceCase c, string actor, CancellationToken ct, bool humanReviewed = false)
    {
        var report = await validation.ValidateAsync(
            new ValidationRequest(c.Invoice ?? new InvoiceDto(), humanReviewed ? null : c.Extraction, c.Document.Sha256, c.Document.Id), ct).ConfigureAwait(false);

        c.Validation = report;
        UpdateSummary(c, report);
        InvoiceFlowTelemetry.Decisions.Add(1,
            new KeyValuePair<string, object?>("outcome", report.Outcome.ToString()),
            new KeyValuePair<string, object?>("by", humanReviewed ? "human" : "system"));

        var codes = string.Join(", ", report.Errors.Select(e => e.RuleCode).Distinct());
        c.Document.TransitionTo(report.NextStatus, actor, Now,
            $"Ruleset {report.Ruleset}: {report.Outcome}{(codes.Length > 0 ? $" ({codes})" : "")}");

        if (report.Outcome == ValidationOutcome.Approved)
        {
            c.JobsToEnqueue.Add(PipelineStep.Post);
        }
    }

    private async Task PostAsync(Guid id, CancellationToken ct)
    {
        var c = await LoadAsync(id, ct).ConfigureAwait(false);
        var doc = c.Document;

        if (doc.Status == InvoiceStatus.Approved)
        {
            doc.TransitionTo(InvoiceStatus.Posting, InvoiceDocument.SystemActor, Now);
            await store.SaveAsync(c, ct).ConfigureAwait(false);
        }
        else if (doc.Status != InvoiceStatus.Posting)
        {
            return;
        }

        var invoice = c.Invoice ?? throw new InvalidOperationException($"Document {id} has no invoice data to post.");
        var supplierId = doc.SupplierId ?? throw new InvalidOperationException($"Document {id} has no resolved supplier.");

        // Same key on every retry → the ERP adapter never creates the invoice twice.
        var key = $"{doc.TenantId}:{supplierId}:{invoice.InvoiceNumber}";
        var reference = await erp.PostInvoiceAsync(new ErpPostRequest(key, id, supplierId, invoice, invoice.PoNumber), ct).ConfigureAwait(false);

        doc.MarkPosted(reference);
        doc.TransitionTo(InvoiceStatus.Posted, InvoiceDocument.SystemActor, Now, $"ERP reference {reference}");
        await store.SaveAsync(c, ct).ConfigureAwait(false);
    }

    /// <summary>Called by the pipeline when a step failed permanently (all retries used).</summary>
    public async Task GiveUpAsync(Guid id, PipelineStep step, string error, CancellationToken ct = default)
    {
        var c = await store.GetAsync(id, ct).ConfigureAwait(false);
        if (c is null)
        {
            return;
        }

        var target = step == PipelineStep.Post ? InvoiceStatus.PostingFailed : InvoiceStatus.NeedsReview;
        if (InvoiceStateMachine.CanTransition(c.Document.Status, target))
        {
            c.Invoice ??= new InvoiceDto();
            c.Document.TransitionTo(target, InvoiceDocument.SystemActor, Now, $"{step} failed after retries: {error}");
            await store.SaveAsync(c, ct).ConfigureAwait(false);
        }
    }

    // ======================= people (review UI) =======================

    /// <summary>Reviewer fixes the data; the rules run again immediately.</summary>
    public async Task<ValidationReport> CorrectAsync(Guid id, InvoiceDto corrected, UserContext user, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(corrected);
        var c = await LoadAsync(id, ct).ConfigureAwait(false);
        RequireStatus(c, InvoiceStatus.NeedsReview);

        var at = Now;
        var changes = Diff(c.Invoice ?? new InvoiceDto(), corrected);
        foreach (var (field, oldValue, newValue) in changes)
        {
            c.NewCorrections.Add(new FieldCorrection(field, oldValue, newValue, user.Id, at));
        }

        c.Invoice = corrected;
        if (changes.Count > 0)
        {
            c.Document.MarkResolvedBy(user.Id);
            c.Document.Record("Corrected", user.Id, at, string.Join(", ", changes.Select(x => x.Field)));
        }

        c.Document.TransitionTo(InvoiceStatus.Validating, user.Id, at);
        await ValidateAndRouteAsync(c, user.Id, ct, humanReviewed: true).ConfigureAwait(false);
        CountAction("correct");
        await store.SaveAsync(c, ct).ConfigureAwait(false);
        return c.Validation!;
    }

    /// <summary>Supervisor accepts the invoice despite failing rules. Above the approval limit it still needs a second approver.</summary>
    public async Task OverrideAsync(Guid id, string reason, UserContext user, CancellationToken ct = default)
    {
        RequireReason(reason);
        var c = await LoadAsync(id, ct).ConfigureAwait(false);
        RequireStatus(c, InvoiceStatus.NeedsReview);

        if (c.Validation?.Errors.Any(e => e.RuleCode.StartsWith("DUP-", StringComparison.Ordinal)) == true)
        {
            throw new WorkflowException(WorkflowErrorKind.InvalidState, "A duplicate cannot be overridden.");
        }

        if (c.Document.SupplierId is null)
        {
            throw new WorkflowException(WorkflowErrorKind.InvalidState, "Supplier is not resolved; correct the supplier first.");
        }

        CountAction("override");
        var codes = string.Join(", ", c.Validation?.Errors.Select(e => e.RuleCode).Distinct() ?? []);
        c.Document.MarkResolvedBy(user.Id);
        c.Document.Record("Overridden", user.Id, Now, $"Rules overridden: {codes}. Reason: {reason}");

        if (NeedsSecondApprover(c))
        {
            c.Document.TransitionTo(InvoiceStatus.PendingApproval, user.Id, Now, "Override above approval limit: second approver required.");
        }
        else
        {
            c.Document.MarkApprovedBy(user.Id);
            c.Document.TransitionTo(InvoiceStatus.Approved, user.Id, Now, "Approved by override.");
            c.JobsToEnqueue.Add(PipelineStep.Post);
        }

        await store.SaveAsync(c, ct).ConfigureAwait(false);
    }

    /// <summary>Second approver (4-eyes) for invoices at or above the approval limit.</summary>
    public async Task ApproveAsync(Guid id, UserContext user, CancellationToken ct = default)
    {
        var c = await LoadAsync(id, ct).ConfigureAwait(false);
        RequireStatus(c, InvoiceStatus.PendingApproval);

        if (string.Equals(c.Document.ResolvedBy, user.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden,
                "Four-eyes rule: you corrected or overrode this invoice, so a different person must approve it.");
        }

        // Delegation of authority: the approver's personal limit must cover the invoice total (ADR-0009).
        var total = c.Invoice?.Total ?? 0m;
        if (user.ApprovalLimit is not { } limit)
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden, "You don't have an approval limit. Ask an administrator to set one.");
        }

        if (total > limit)
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden,
                $"This invoice ({total.ToString("#,##0.00", CultureInfo.InvariantCulture)}) is above your approval limit " +
                $"({limit.ToString("#,##0.00", CultureInfo.InvariantCulture)}). Someone with a higher limit must approve it.");
        }

        CountAction("approve");
        c.Document.MarkApprovedBy(user.Id);
        c.Document.TransitionTo(InvoiceStatus.Approved, user.Id, Now, "Second approval.");
        c.JobsToEnqueue.Add(PipelineStep.Post);
        await store.SaveAsync(c, ct).ConfigureAwait(false);
    }

    public async Task RejectAsync(Guid id, string reason, UserContext user, CancellationToken ct = default)
    {
        RequireReason(reason);
        var c = await LoadAsync(id, ct).ConfigureAwait(false);
        if (c.Document.Status is not (InvoiceStatus.NeedsReview or InvoiceStatus.PendingApproval))
        {
            throw new WorkflowException(WorkflowErrorKind.InvalidState, $"Only invoices in review or pending approval can be rejected (status is {c.Document.Status}).");
        }

        CountAction("reject");
        c.Document.TransitionTo(InvoiceStatus.Rejected, user.Id, Now, reason);
        await store.SaveAsync(c, ct).ConfigureAwait(false);
    }

    public async Task RetryPostingAsync(Guid id, UserContext user, CancellationToken ct = default)
    {
        var c = await LoadAsync(id, ct).ConfigureAwait(false);
        RequireStatus(c, InvoiceStatus.PostingFailed);
        c.Document.TransitionTo(InvoiceStatus.Posting, user.Id, Now, "Posting retried manually.");
        c.JobsToEnqueue.Add(PipelineStep.Post);
        await store.SaveAsync(c, ct).ConfigureAwait(false);
    }

    // ======================= helpers =======================

    private static void CountAction(string action) =>
        InvoiceFlowTelemetry.HumanActions.Add(1, new KeyValuePair<string, object?>("action", action));

    private async Task<InvoiceCase> LoadAsync(Guid id, CancellationToken ct) =>
        await store.GetAsync(id, ct).ConfigureAwait(false)
        ?? throw new WorkflowException(WorkflowErrorKind.NotFound, $"Invoice {id} not found.");

    private bool NeedsSecondApprover(InvoiceCase c) =>
        settings.Value.ApprovalThreshold > 0 && c.Invoice?.Total is { } total && total >= settings.Value.ApprovalThreshold;

    private static void RequireStatus(InvoiceCase c, InvoiceStatus status)
    {
        if (c.Document.Status != status)
        {
            throw new WorkflowException(WorkflowErrorKind.InvalidState, $"Invoice is {c.Document.Status}; this action needs {status}.");
        }
    }

    private static void RequireReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < MinReasonLength)
        {
            throw new WorkflowException(WorkflowErrorKind.BadRequest, $"A reason of at least {MinReasonLength} characters is required.");
        }
    }

    private static void UpdateSummary(InvoiceCase c, ValidationReport? report)
    {
        var i = c.Invoice;
        c.Document.UpdateSummary(report?.SupplierId ?? c.Document.SupplierId, i?.SupplierName, i?.InvoiceNumber, i?.InvoiceDate, i?.Currency, i?.Total, i?.PoNumber);
    }

    private static readonly JsonSerializerOptions DiffJson = new(JsonSerializerDefaults.Web);

    /// <summary>Field-level differences, e.g. ("total", "1155", "1150"), ("lines[1].unitPrice", "20", "21").</summary>
    public static IReadOnlyList<(string Field, string? Old, string? New)> Diff(InvoiceDto before, InvoiceDto after)
    {
        var a = Flatten(JsonSerializer.SerializeToElement(before, DiffJson), "");
        var b = Flatten(JsonSerializer.SerializeToElement(after, DiffJson), "");
        return a.Keys.Union(b.Keys).Order(StringComparer.Ordinal)
            .Select(k => (k, a.GetValueOrDefault(k), b.GetValueOrDefault(k)))
            .Where(x => x.Item2 != x.Item3)
            .ToList();
    }

    private static Dictionary<string, string?> Flatten(JsonElement e, string prefix)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    foreach (var kv in Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}"))
                    {
                        result[kv.Key] = kv.Value;
                    }
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray())
                {
                    foreach (var kv in Flatten(item, $"{prefix}[{i++}]"))
                    {
                        result[kv.Key] = kv.Value;
                    }
                }

                break;
            case JsonValueKind.Null:
                break;
            case JsonValueKind.Number:
                // 1900.0 == 1900.00
                result[prefix] = e.GetDecimal().ToString("0.############", CultureInfo.InvariantCulture);
                break;
            default:
                result[prefix] = e.ToString();
                break;
        }

        return result;
    }
}
