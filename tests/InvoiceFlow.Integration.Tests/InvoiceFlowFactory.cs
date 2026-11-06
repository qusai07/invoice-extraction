using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Workflow;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;

namespace InvoiceFlow.Integration.Tests;

/// <summary>
/// The real API against a real SQL Server (Docker, via Testcontainers). One container for all tests.
/// Extraction is replaced by <see cref="DatasetExtractor"/> (returns the golden answer for synthetic files),
/// so the tests exercise everything else: intake, queue, workflow, validation, posting, auth, audit.
/// </summary>
public sealed class InvoiceFlowFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly MsSqlContainer _sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "invoiceflow-tests-" + Guid.NewGuid().ToString("N"));

    public static string RepoRoot { get; } = FindRepoRoot();
    public static string DataDir => Path.Combine(RepoRoot, "evals", "data");

    public async Task InitializeAsync() => await _sql.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _sql.DisposeAsync();
        try
        {
            Directory.Delete(_storage, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing": no appsettings.Development.json, no user-secrets → only the settings below apply.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:InvoiceFlow", _sql.GetConnectionString());
        builder.UseSetting("Auth:Mode", "Dev");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Pipeline:RunInProcess", "false"); // tests drive the pipeline explicitly
        builder.UseSetting("Storage:Provider", "FileSystem");
        builder.UseSetting("Storage:FileSystem:Root", _storage);
        builder.UseSetting("MockErp:SeedFile", Path.Combine(DataDir, "master-data.json"));
        builder.UseSetting("Validation:ApprovalThreshold", "100000");
        builder.UseSetting("Security:MalwareScan:Provider", "None"); // must be explicit outside Development
        builder.UseSetting("RateLimits:UploadsPerMinute", "10000");

        builder.ConfigureTestServices(s => s.AddSingleton<IInvoiceExtractor>(new DatasetExtractor(DataDir)));
    }

    public HttpClient Client(string user, params string[] roles) => SignIn(CreateClient(), user, roles);

    public static HttpClient SignIn(HttpClient client, string user, params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Add("X-Dev-User", user);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", string.Join(',', roles));
        return client;
    }

    /// <summary>Runs queued pipeline jobs until the queue is empty (what the worker does in production).</summary>
    public async Task DrainPipelineAsync()
    {
        for (var i = 0; i < 200; i++)
        {
            await using var scope = Services.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<PipelineProcessor>().ProcessNextAsync("test"))
            {
                return;
            }
        }

        throw new InvalidOperationException("Pipeline did not drain.");
    }

    public static MultipartFormDataContent FileContent(string datasetFile)
    {
        var path = Path.Combine(DataDir, datasetFile);
        var file = new ByteArrayContent(File.ReadAllBytes(path));
        file.Headers.ContentType = new MediaTypeHeaderValue(path.EndsWith(".pdf", StringComparison.Ordinal) ? "application/pdf" : "image/png");
        return new MultipartFormDataContent { { file, "file", Path.GetFileName(path) } };
    }

    /// <summary>From the test binaries, or (if built elsewhere, e.g. --artifacts-path) from this source file's location.</summary>
    private static string FindRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) })
        {
            var dir = start is null ? null : new DirectoryInfo(start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InvoiceFlow.sln")))
            {
                dir = dir.Parent;
            }

            if (dir is not null)
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repo root not found.");
    }
}

/// <summary>Stands in for the AI: returns the expected answer of a synthetic dataset file, by file name.</summary>
public sealed class DatasetExtractor(string dataDir) : IInvoiceExtractor
{
    public string Name => "dataset";

    public Task<ExtractionResult> ExtractAsync(DocumentInput document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var usage = new ExtractionUsage { Extractor = Name, Model = "none", PromptVersion = "none", Pages = 1 };
        var expected = Path.Combine(dataDir, Path.GetFileNameWithoutExtension(document.FileName) + ".expected.json");
        if (!File.Exists(expected))
        {
            return Task.FromResult(ExtractionResult.Fail("unreadable document", usage));
        }

        using var json = JsonDocument.Parse(File.ReadAllText(expected));
        var invoice = json.RootElement.GetProperty("invoice").Deserialize<InvoiceDto>(InvoiceFlowFactory.Json)!;
        return Task.FromResult(new ExtractionResult
        {
            Status = ExtractionStatus.Succeeded,
            DocumentType = DocumentType.Invoice,
            Invoice = invoice,
            Usage = usage,
        });
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<InvoiceFlowFactory>
{
    public const string Name = "api";
}
