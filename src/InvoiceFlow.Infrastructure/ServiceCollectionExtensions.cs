using Azure.Identity;
using Azure.Storage.Blobs;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Application.Security;
using InvoiceFlow.Infrastructure.Security;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Infrastructure.MockErp;
using InvoiceFlow.Infrastructure.Persistence;
using InvoiceFlow.Infrastructure.Pipeline;
using InvoiceFlow.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Everything except extraction and hosting: SQL persistence, job queue, file storage, mock ERP master data + posting,
    /// validation engine and the invoice workflow.
    /// </summary>
    /// <remarks>
    /// Config: <c>ConnectionStrings:InvoiceFlow</c>, <c>MockErp:SeedFile</c>, <c>Storage:Provider</c> (FileSystem | AzureBlob),
    /// <c>Storage:FileSystem:Root</c>, <c>Storage:AzureBlob:ConnectionString</c> + <c>:Container</c>, <c>Validation:*</c>, <c>Pipeline:*</c>.
    /// </remarks>
    public static IServiceCollection AddInvoiceFlowInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string contentRoot, bool isDevelopment = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // ---- master data (mock ERP until Phase 4) ----
        var seedFile = configuration["MockErp:SeedFile"]
            ?? throw new InvalidOperationException("Configure MockErp:SeedFile (path to a master-data JSON file).");
        var data = MasterDataFile.Load(Path.GetFullPath(seedFile, contentRoot));
        services.AddSingleton(data);
        services.AddSingleton<IMasterDataService, InMemoryMasterDataService>();
        services.AddSingleton<IErpPoster, MockErpPoster>();

        // ---- validation ----
        services.AddOptions<ValidationSettings>()
            .Bind(configuration.GetSection(ValidationSettings.SectionName))
            .PostConfigure(s =>
            {
                if (s.CompanyTrns.Count == 0)
                {
                    s.CompanyTrns = data.CompanyTrns.ToList();
                }

                // The configuration binder appends configured list items to the code defaults ([0.05, 0] + [0.05, 0]).
                s.AllowedVatRates = s.AllowedVatRates.Distinct().ToList();
                s.CompanyTrns = s.CompanyTrns.Distinct(StringComparer.Ordinal).ToList();
            });
        services.AddSingleton(TimeProvider.System);
        // Admin-editable settings (Rules page) overlay configuration; one snapshot per request/job.
        services.AddSingleton<Settings.ValidationSettingsStore>();
        services.AddScoped(Settings.ValidationSettingsStore.CreateEngine);
        services.AddScoped<IOptions<ValidationSettings>>(sp =>
            Options.Create(sp.GetRequiredService<Settings.ValidationSettingsStore>().Current().Settings));

        // ---- persistence ----
        var connectionString = configuration.GetConnectionString("InvoiceFlow")
            ?? throw new InvalidOperationException("Configure ConnectionStrings:InvoiceFlow.");
        services.AddDbContext<InvoiceFlowDbContext>(o => o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        services.AddScoped<IInvoiceStore, SqlInvoiceStore>();
        services.AddScoped<IInvoiceHistory, SqlInvoiceHistory>();
        services.AddScoped<IJobQueue, SqlJobQueue>();

        // ---- file storage ----
        var provider = configuration["Storage:Provider"] ?? "FileSystem";
        switch (provider)
        {
            case "FileSystem":
                var root = Path.GetFullPath(configuration["Storage:FileSystem:Root"] ?? "data/documents", contentRoot);
                services.AddSingleton<IDocumentStore>(new FileSystemDocumentStore(root));
                break;
            case "AzureBlob":
                var containerName = configuration["Storage:AzureBlob:Container"] ?? "invoices";
                var serviceUri = configuration["Storage:AzureBlob:ServiceUri"];
                var blobConnection = configuration["Storage:AzureBlob:ConnectionString"];
                // Prefer managed identity (no keys anywhere); connection string for Azurite / local only.
                var container = !string.IsNullOrWhiteSpace(serviceUri)
                    ? new BlobServiceClient(new Uri(serviceUri), new DefaultAzureCredential()).GetBlobContainerClient(containerName)
                    : new BlobContainerClient(blobConnection ?? throw new InvalidOperationException(
                        "Configure Storage:AzureBlob:ServiceUri (managed identity) or :ConnectionString."), containerName);
                var defender = configuration["Security:MalwareScan:Provider"] == "DefenderForStorage";
                services.AddSingleton<IDocumentStore>(new AzureBlobDocumentStore(container, requireDefenderVerdict: defender));
                break;
            default:
                throw new InvalidOperationException($"Unknown Storage:Provider '{provider}'. Use FileSystem or AzureBlob.");
        }

        // ---- malware scanning (ADR-0008) ----
        var scan = configuration["Security:MalwareScan:Provider"];
        services.AddSingleton<IMalwareScanner>(scan switch
        {
            "ClamAV" => new ClamAvScanner(configuration.GetSection("Security:MalwareScan:ClamAV").Get<ClamAvOptions>() ?? new ClamAvOptions()),
            // Scanning happens on the blob after upload (Defender for Storage); no inline scan here.
            "DefenderForStorage" when provider == "AzureBlob" => new NoMalwareScanner("defender-for-storage"),
            "DefenderForStorage" => throw new InvalidOperationException("Security:MalwareScan:Provider=DefenderForStorage requires Storage:Provider=AzureBlob."),
            "None" => new NoMalwareScanner(),
            null when isDevelopment => new NoMalwareScanner(),
            _ => throw new InvalidOperationException(
                "Configure Security:MalwareScan:Provider (ClamAV | DefenderForStorage | None). It must be chosen explicitly outside Development."),
        });

        // ---- identity (ADR-0009) ----
        services.AddMemoryCache();
        var session = configuration.GetSection(Identity.SessionOptions.SectionName).Get<Identity.SessionOptions>() ?? new Identity.SessionOptions();
        if (string.IsNullOrWhiteSpace(session.SigningKey) && isDevelopment)
        {
            // Development convenience: a random key per process start (everyone signs in again after a restart).
            session.SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        }

        services.AddSingleton(session);
        services.AddSingleton<Identity.TokenIssuer>();
        services.AddSingleton<Identity.MfaSecretProtector>();
        services.AddSingleton<Identity.PermissionResolver>();
        services.AddScoped<Identity.IdentityService>();
        services.AddScoped<Identity.UserAdminService>();

        // ---- health ----
        services.AddHealthChecks().AddDbContextCheck<InvoiceFlowDbContext>("database", tags: ["ready"]);

        // ---- workflow + pipeline ----
        services.AddScoped<InvoiceWorkflow>();
        services.AddScoped<PipelineProcessor>();
        services.AddOptions<PipelineOptions>().Bind(configuration.GetSection(PipelineOptions.SectionName));

        return services;
    }

    /// <summary>Runs pipeline jobs in this process (InvoiceFlow.Worker always; the API only when Pipeline:RunInProcess).</summary>
    public static IServiceCollection AddInvoiceFlowPipelineWorker(this IServiceCollection services) =>
        services.AddHostedService<PipelineHostedService>();
}
