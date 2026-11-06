using System.Net;
using System.Net.Http.Headers;
using InvoiceFlow.Api;
using InvoiceFlow.Application.Security;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static InvoiceFlow.Integration.Tests.InvoiceFlowFactory;

namespace InvoiceFlow.Integration.Tests.Api;

[Collection(ApiCollection.Name)]
public class HardeningTests(InvoiceFlowFactory factory)
{
    private static Uri U(string path) => new(path, UriKind.Relative);

    private static MultipartFormDataContent Bytes(byte[] content, string fileName, string declaredType)
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(declaredType);
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    private sealed class FakeScanner(Func<ScanResult> result) : IMalwareScanner
    {
        public int Calls { get; private set; }
        public string Name => "fake";

        public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result());
        }
    }

    private HttpClient ClientWith(IMalwareScanner scanner, string user = "clara") =>
        SignIn(factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(scanner))).CreateClient(), user, "Clerk");

    // ---------- upload safety ----------

    [Fact]
    public async Task File_that_is_not_pdf_png_or_jpeg_is_rejected_whatever_it_claims()
    {
        using var clerk = factory.Client("clara", "Clerk");
        using var content = Bytes("MZ\x90\0 this is an executable"u8.ToArray(), "invoice.pdf", "application/pdf");

        var response = await clerk.PostAsync(U("/api/invoices"), content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Real_type_comes_from_the_bytes_not_the_header()
    {
        using var clerk = factory.Client("clara", "Clerk");
        // A real PNG uploaded with a wrong name and declared type.
        using var content = Bytes(await File.ReadAllBytesAsync(Path.Combine(DataDir, "syn-0018.png")), "scan.pdf", "application/pdf");

        var response = await clerk.PostAsync(U("/api/invoices"), content);
        response.EnsureSuccessStatusCode();
        var location = response.Headers.Location!.ToString();

        var file = await clerk.GetAsync(U(location + "/file"));
        Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Infected_file_is_blocked_and_never_stored()
    {
        var scanner = new FakeScanner(() => new ScanResult(ScanVerdict.Infected, "Eicar-Test-Signature"));
        using var client = ClientWith(scanner, "mallory-test");
        using var content = FileContent("syn-0012.pdf");

        var response = await client.PostAsync(U("/api/invoices"), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, scanner.Calls);
        var list = await client.GetStringAsync(U("/api/invoices?take=200"));
        Assert.DoesNotContain("syn-0012.pdf", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scanner_down_means_upload_refused_not_accepted_unscanned()
    {
        var scanner = new FakeScanner(() => throw new MalwareScannerUnavailableException("clamd down"));
        using var client = ClientWith(scanner);
        using var content = FileContent("syn-0021.png");

        var response = await client.PostAsync(U("/api/invoices"), content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // ---------- rate limiting ----------

    [Fact]
    public async Task Uploads_are_rate_limited_per_user()
    {
        using var client = SignIn(factory.WithWebHostBuilder(b => b.UseSetting("RateLimits:UploadsPerMinute", "2")).CreateClient(), "speedy", "Clerk");

        var statuses = new List<HttpStatusCode>();
        foreach (var file in new[] { "syn-0032.png", "syn-0034.png", "syn-0040.png" })
        {
            using var content = FileContent(file);
            var r = await client.PostAsync(U("/api/invoices"), content);
            statuses.Add(r.StatusCode);
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(r.Headers.Contains("Retry-After"));
            }
        }

        Assert.Equal([HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests], statuses);
    }

    // ---------- audit export ----------

    [Fact]
    public async Task Auditor_can_export_audit_csv()
    {
        using var clerk = factory.Client("clara", "Clerk");
        using (var content = FileContent("syn-0030.pdf"))
        {
            (await clerk.PostAsync(U("/api/invoices"), content)).EnsureSuccessStatusCode();
        }

        using var auditor = factory.Client("aaron", "Auditor");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var response = await auditor.GetAsync(U($"/api/audit/export?from={today:yyyy-MM-dd}&to={today.AddDays(1):yyyy-MM-dd}"));

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        var csv = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("at_utc,document_id,file_name", csv.TrimStart('﻿'), StringComparison.Ordinal);
        Assert.Contains("syn-0030.pdf", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clerk_cannot_export_audit()
    {
        using var clerk = factory.Client("clara", "Clerk");
        var response = await clerk.GetAsync(U("/api/audit/export?from=2026-01-01&to=2026-02-01"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Export_range_is_bounded()
    {
        using var auditor = factory.Client("aaron", "Auditor");
        var response = await auditor.GetAsync(U("/api/audit/export?from=2020-01-01&to=2026-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+1+1", "\"'+1+1\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("plain", "plain")]
    [InlineData(null, "")]
    public void Csv_cells_are_escaped_and_formula_safe(string? value, string expected) =>
        Assert.Equal(expected, AuditExport.Cell(value));

    // ---------- health & headers ----------

    [Fact]
    public async Task Readiness_checks_the_database()
    {
        using var client = factory.CreateClient();
        var ready = await client.GetAsync(U("/health/ready"));
        var live = await client.GetAsync(U("/health/live"));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Api_responses_carry_security_headers()
    {
        using var clerk = factory.Client("clara", "Clerk");
        var response = await clerk.GetAsync(U("/api/me"));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Server"));
    }
}
