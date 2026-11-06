using System.Net;
using System.Net.Http.Json;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Domain.Invoices;
using InvoiceFlow.Infrastructure.MockErp;
using static InvoiceFlow.Integration.Tests.InvoiceFlowFactory;

namespace InvoiceFlow.Integration.Tests.Api;

/// <summary>End-to-end: upload → queue → extract → validate → review/approve → post, with auth and audit.</summary>
[Collection(ApiCollection.Name)]
public class InvoiceEndpointTests(InvoiceFlowFactory factory)
{
    private sealed record Submitted(Guid Id, InvoiceStatus Status);

    private sealed record Details(InvoiceDocumentState Document, InvoiceDto? Invoice, ValidationReport? Validation,
        IReadOnlyList<FieldCorrection> Corrections, IReadOnlyList<AuditEvent> Audit);

    private static Uri U(string path) => new(path, UriKind.Relative);

    private async Task<Guid> UploadAsync(HttpClient client, string file, HttpStatusCode expected = HttpStatusCode.Accepted)
    {
        using var content = FileContent(file);
        var response = await client.PostAsync(U("/api/invoices"), content);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Submitted>(Json))!.Id;
    }

    private static async Task<Details> GetAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<Details>(U($"/api/invoices/{id}"), Json))!;

    // ---------------- happy paths ----------------

    [Fact]
    public async Task Clean_invoice_goes_straight_through_to_the_erp()
    {
        using var clerk = factory.Client("clara", "Clerk");
        var id = await UploadAsync(clerk, "syn-0001.png");

        await factory.DrainPipelineAsync();

        var d = await GetAsync(clerk, id);
        Assert.Equal(InvoiceStatus.Posted, d.Document.Status);
        Assert.StartsWith("ERP-", d.Document.ErpReference, StringComparison.Ordinal);
        Assert.Equal(ValidationOutcome.Approved, d.Validation!.Outcome);

        // Full audit trail, in order.
        var path = d.Audit.Where(a => a.ToStatus is not null).Select(a => a.ToStatus!.Value).ToList();
        Assert.Equal(
            [InvoiceStatus.Received, InvoiceStatus.Preprocessing, InvoiceStatus.Extracting, InvoiceStatus.Extracted,
             InvoiceStatus.Validating, InvoiceStatus.Approved, InvoiceStatus.Posting, InvoiceStatus.Posted],
            path);
        Assert.Equal("clara", d.Audit[0].Actor);
    }

    [Fact]
    public async Task Original_file_can_be_downloaded()
    {
        using var clerk = factory.Client("clara", "Clerk");
        var id = await UploadAsync(clerk, "syn-0010.pdf");

        var response = await clerk.GetAsync(U($"/api/invoices/{id}/file"));

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(DataDir, "syn-0010.pdf")), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Changed_iban_is_stopped_then_corrected_by_a_clerk_and_posted()
    {
        using var clerk = factory.Client("clara", "Clerk");
        var id = await UploadAsync(clerk, "syn-0023.pdf");
        await factory.DrainPipelineAsync();

        var d = await GetAsync(clerk, id);
        Assert.Equal(InvoiceStatus.NeedsReview, d.Document.Status);
        Assert.Contains(d.Validation!.Errors, f => f.RuleCode == "SUP-002");

        // Clerk confirms the registered IBAN with the supplier and corrects it.
        var registeredIban = MasterDataFile.Load(Path.Combine(DataDir, "master-data.json"))
            .Suppliers.Single(s => s.Id == d.Validation.SupplierId).Iban;
        var put = await clerk.PutAsJsonAsync(U($"/api/invoices/{id}/data"), d.Invoice! with { Iban = registeredIban }, Json);
        put.EnsureSuccessStatusCode();
        Assert.Equal(ValidationOutcome.Approved, (await put.Content.ReadFromJsonAsync<ValidationReport>(Json))!.Outcome);

        await factory.DrainPipelineAsync();
        d = await GetAsync(clerk, id);
        Assert.Equal(InvoiceStatus.Posted, d.Document.Status);
        var correction = Assert.Single(d.Corrections);
        Assert.Equal("iban", correction.Field);
        Assert.Equal("clara", correction.UserId);
    }

    [Fact]
    public async Task Large_invoice_needs_a_second_approver()
    {
        using var clerk = factory.Client("clara", "Clerk");
        using var supervisor = factory.Client("sam", "Supervisor");
        var id = await UploadAsync(clerk, "syn-0036.png");
        await factory.DrainPipelineAsync();
        Assert.Equal(InvoiceStatus.PendingApproval, (await GetAsync(clerk, id)).Document.Status);

        // A clerk can't approve.
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsync(U($"/api/invoices/{id}/approve"), null)).StatusCode);

        (await supervisor.PostAsync(U($"/api/invoices/{id}/approve"), null)).EnsureSuccessStatusCode();
        await factory.DrainPipelineAsync();

        var d = await GetAsync(clerk, id);
        Assert.Equal(InvoiceStatus.Posted, d.Document.Status);
        Assert.Equal("sam", d.Document.ApprovedBy);
    }

    [Fact]
    public async Task Override_of_a_large_invoice_needs_a_different_approver()
    {
        using var alice = factory.Client("alice", "Supervisor");
        using var bob = factory.Client("bob", "FinanceAdmin");
        var id = await UploadAsync(alice, "syn-0014.pdf"); // iban-changed, AED 130,932.90
        await factory.DrainPipelineAsync();

        var overrideResponse = await alice.PostAsJsonAsync(U($"/api/invoices/{id}/override"),
            new { reason = "Supplier confirmed new bank account by phone, ticket #4411." }, Json);
        overrideResponse.EnsureSuccessStatusCode();
        Assert.Equal(InvoiceStatus.PendingApproval, (await GetAsync(alice, id)).Document.Status);

        // Four-eyes: the person who overrode can't also approve.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.PostAsync(U($"/api/invoices/{id}/approve"), null)).StatusCode);

        (await bob.PostAsync(U($"/api/invoices/{id}/approve"), null)).EnsureSuccessStatusCode();
        await factory.DrainPipelineAsync();

        var d = await GetAsync(bob, id);
        Assert.Equal(InvoiceStatus.Posted, d.Document.Status);
        Assert.Contains(d.Audit, a => a.Action == "Overridden" && a.Actor == "alice" && a.Details!.Contains("SUP-002", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_file_twice_is_a_duplicate_without_extraction()
    {
        using var clerk = factory.Client("clara", "Clerk");
        await UploadAsync(clerk, "syn-0027.pdf");

        using var content = FileContent("syn-0027.pdf");
        var second = await (await clerk.PostAsync(U("/api/invoices"), content)).Content.ReadFromJsonAsync<Submitted>(Json);

        Assert.Equal(InvoiceStatus.Duplicate, second!.Status);
    }

    [Fact]
    public async Task Reject_needs_a_reason()
    {
        using var clerk = factory.Client("clara", "Clerk");
        var id = await UploadAsync(clerk, "syn-0033.pdf");
        await factory.DrainPipelineAsync();

        var tooShort = await clerk.PostAsJsonAsync(U($"/api/invoices/{id}/reject"), new { reason = "no" }, Json);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var ok = await clerk.PostAsJsonAsync(U($"/api/invoices/{id}/reject"), new { reason = "No purchase order exists for this delivery." }, Json);
        ok.EnsureSuccessStatusCode();
        Assert.Equal(InvoiceStatus.Rejected, (await GetAsync(clerk, id)).Document.Status);
    }

    [Fact]
    public async Task Review_queue_lists_invoices_needing_review_with_reasons()
    {
        using var clerk = factory.Client("clara", "Clerk");
        var id = await UploadAsync(clerk, "syn-0031.png"); // qty-exceeds
        await factory.DrainPipelineAsync();

        var queue = await clerk.GetFromJsonAsync<List<InvoiceSummary>>(U("/api/invoices?status=NeedsReview"), Json);

        var item = Assert.Single(queue!, q => q.Id == id);
        Assert.Contains("PO-004", item.ErrorCodes);
    }

    // ---------------- security ----------------

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(U("/api/invoices"))).StatusCode);
    }

    [Fact]
    public async Task Submitter_cannot_review_and_sees_only_own_documents()
    {
        using var clerk = factory.Client("clara", "Clerk");
        using var submitter = factory.Client("sue", "Submitter");
        var clerksDoc = await UploadAsync(clerk, "syn-0028.png");
        var ownDoc = await UploadAsync(submitter, "syn-0039.png");
        await factory.DrainPipelineAsync();

        var list = await submitter.GetFromJsonAsync<List<InvoiceSummary>>(U("/api/invoices"), Json);
        Assert.All(list!, s => Assert.Equal("sue", s.SubmittedBy));
        Assert.Contains(list!, s => s.Id == ownDoc);

        Assert.Equal(HttpStatusCode.NotFound, (await submitter.GetAsync(U($"/api/invoices/{clerksDoc}"))).StatusCode);

        var put = await submitter.PutAsJsonAsync(U($"/api/invoices/{ownDoc}/data"), new InvoiceDto(), Json);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task Validate_endpoint_checks_data_without_storing()
    {
        using var clerk = factory.Client("clara", "Clerk");
        var invoice = (await new DatasetExtractor(DataDir).ExtractAsync(new DocumentInput("syn-0001.png", "image/png", Array.Empty<byte>()))).Invoice!;

        var response = await clerk.PostAsJsonAsync(U("/api/invoices/validate"), invoice with { Iban = "AE990000000000000000001" }, Json);

        var report = await response.Content.ReadFromJsonAsync<ValidationReport>(Json);
        Assert.Equal(ValidationOutcome.NeedsReview, report!.Outcome);
        Assert.Contains(report.Findings, f => f.RuleCode == "SUP-002" && f.MessageAr.Length > 0);
    }
}
