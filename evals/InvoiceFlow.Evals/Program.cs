using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Evals;
using InvoiceFlow.Evals.Dataset;
using InvoiceFlow.Evals.Running;
using InvoiceFlow.Evals.Synthetic;
using InvoiceFlow.Extraction.AzureOpenAi;
using InvoiceFlow.Infrastructure.MockErp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

const string Help = """
    InvoiceFlow.Evals

      check     --data <dir>
                Validate the golden dataset (pairs, JSON shape, arithmetic).

      generate  --out <dir> [--count 30] [--seed 42] [--browser <path-to-edge-or-chrome>]
                Create synthetic invoices (PDF/PNG + .expected.json). Same seed → same files.

      run       --data <dir> [--extractor azure-openai|ground-truth] [--filter <text>] [--limit N]
                [--parallel 4] [--reports evals/reports] [--expect-perfect]
                Run an extractor over the dataset, validate the extracted data against
                <dir>/master-data.json, and write a Markdown + JSON report (field accuracy,
                false accepts, straight-through rate). --expect-perfect exits 1 unless 100%.
                Azure settings: evals/InvoiceFlow.Evals/appsettings.json, user-secrets, or env vars
                (Extraction__AzureOpenAI__Endpoint, __Deployment, __ApiKey).
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Help);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return args[0] switch
{
    "check" => DatasetChecker.Run(Opt("--data") ?? Path.Combine("evals", "data"), Console.Out),
    "generate" => await GenerateAsync(cts.Token),
    "run" => await RunAsync(cts.Token),
    _ => Fail($"Unknown command '{args[0]}'.\n\n{Help}"),
};

async Task<int> GenerateAsync(CancellationToken ct)
{
    var outDir = Opt("--out") ?? Path.Combine("evals", "data");
    var count = int.Parse(Opt("--count") ?? "30", System.Globalization.CultureInfo.InvariantCulture);
    var seed = int.Parse(Opt("--seed") ?? "42", System.Globalization.CultureInfo.InvariantCulture);
    var renderer = new BrowserRenderer(Opt("--browser"));

    Directory.CreateDirectory(outDir);
    var htmlDir = Directory.CreateTempSubdirectory("invoiceflow-html-").FullName;
    Console.WriteLine($"Rendering with {renderer.BrowserPath}");

    var generator = new SyntheticInvoiceGenerator(seed);
    try
    {
        foreach (var doc in generator.Generate(count))
        {
            var htmlPath = Path.Combine(htmlDir, doc.Id + ".html");
            await File.WriteAllTextAsync(htmlPath, doc.Html, ct);

            // Remove a previous rendering in the other format so each id has exactly one document.
            foreach (var old in Directory.GetFiles(outDir, doc.Id + ".*"))
            {
                File.Delete(old);
            }

            var ext = doc.Format == OutputFormat.Pdf ? ".pdf" : ".png";
            await renderer.RenderAsync(htmlPath, Path.GetFullPath(Path.Combine(outDir, doc.Id + ext)), doc.Format, ct);
            DatasetLoader.Save(Path.Combine(outDir, doc.Id + DatasetChecker.ExpectedSuffix), doc.Expected);
            Console.WriteLine($"  {doc.Id}{ext}  [{string.Join(", ", doc.Expected.Meta.Tags)}]  {doc.Expected.Validation?.Scenario} → {doc.Expected.Validation?.Outcome}");
        }
    }
    finally
    {
        Directory.Delete(htmlDir, recursive: true);
    }

    generator.BuildMasterData().Save(Path.Combine(outDir, MasterDataFileName));
    Console.WriteLine($"  {MasterDataFileName} (suppliers + purchase orders)");

    return DatasetChecker.Run(outDir, Console.Out);
}

async Task<int> RunAsync(CancellationToken ct)
{
    var dataDir = Opt("--data") ?? Path.Combine("evals", "data");
    var items = DatasetLoader.Load(dataDir, Opt("--filter"));
    if (Opt("--limit") is { } limit)
    {
        items = items.Take(int.Parse(limit, System.Globalization.CultureInfo.InvariantCulture)).ToList();
    }

    if (items.Count == 0)
    {
        return Fail($"No dataset items in {dataDir}.");
    }

    IInvoiceExtractor extractor;
    try
    {
        extractor = (Opt("--extractor") ?? "azure-openai") switch
        {
            "ground-truth" => new GroundTruthExtractor(items),
            "azure-openai" => CreateAzureExtractor(),
            var other => throw new ArgumentException($"Unknown extractor '{other}'."),
        };
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
    {
        return Fail($"""
            {ex.Message}

            Set the Azure OpenAI settings, e.g. with user-secrets:
              dotnet user-secrets --project evals/InvoiceFlow.Evals set "Extraction:AzureOpenAI:Endpoint" "https://<resource>.openai.azure.com/"
              dotnet user-secrets --project evals/InvoiceFlow.Evals set "Extraction:AzureOpenAI:Deployment" "<deployment-name>"
              dotnet user-secrets --project evals/InvoiceFlow.Evals set "Extraction:AzureOpenAI:ApiKey" "<key>"   (or omit and use 'az login')
            """);
    }

    var parallel = int.Parse(Opt("--parallel") ?? "4", System.Globalization.CultureInfo.InvariantCulture);
    var validation = CreateValidationEngine(dataDir);
    Console.WriteLine($"Running '{extractor.Name}' on {items.Count} documents (parallel {parallel}, validation {(validation is null ? "off: no " + MasterDataFileName : "on")})…");

    var report = await EvalRunner.RunAsync(extractor, items, parallel, Console.Out, ct, validation);
    var (md, _) = EvalRunner.Save(report, Opt("--reports") ?? Path.Combine("evals", "reports"));

    Console.WriteLine();
    Console.WriteLine($"Fully correct: {report.FullyCorrectRate:P1}   Header accuracy: {report.HeaderAccuracy:P1}   " +
                      $"Lines P/R: {report.LinePrecision:P1}/{report.LineRecall:P1}   Cost: ${report.TotalCostUsd:0.0000}");
    if (report.DecisionCount > 0)
    {
        Console.WriteLine($"False accepts: {report.FalseAccepts}   Straight-through: {report.StraightThroughRate:P1}   " +
                          $"Decision accuracy: {report.DecisionAccuracy:P1}   Caught for right reason: {report.CaughtForRightReasonRate:P1}");
    }

    Console.WriteLine($"Report: {md}");

    if (args.Contains("--expect-perfect") && !report.IsPerfect)
    {
        return Fail("--expect-perfect: results are not 100%. See the report.");
    }

    return 0;
}

static ValidationEngine? CreateValidationEngine(string dataDir)
{
    var path = Path.Combine(dataDir, MasterDataFileName);
    if (!File.Exists(path))
    {
        return null;
    }

    var data = MasterDataFile.Load(path);
    var settings = new ValidationSettings
    {
        CompanyTrns = data.CompanyTrns.ToList(),
        ApprovalThreshold = SyntheticInvoiceGenerator.ApprovalThreshold,
    };
    var asOf = data.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return new ValidationEngine(
        new InMemoryMasterDataService(data),
        new InMemoryInvoiceHistory(),
        Options.Create(settings),
        new FixedTimeProvider(asOf));
}

static IInvoiceExtractor CreateAzureExtractor()
{
    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddUserSecrets(typeof(DatasetChecker).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build();

    var options = new AzureOpenAiExtractorOptions();
    config.GetSection(AzureOpenAiExtractorOptions.SectionName).Bind(options);
    return new AzureOpenAiInvoiceExtractor(Options.Create(options), NullLogger<AzureOpenAiInvoiceExtractor>.Instance);
}

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

internal sealed partial class Program
{
    private const string MasterDataFileName = "master-data.json";
}

internal sealed class FixedTimeProvider(DateOnly today) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
}
