using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Validation;

namespace InvoiceFlow.Evals.Dataset;

/// <summary>One <c>*.expected.json</c> file. Format: evals/data/README.md.</summary>
public sealed record ExpectedFile
{
    public ExpectedMeta Meta { get; init; } = new();
    public required InvoiceDto Invoice { get; init; }

    /// <summary>
    /// Other answers that also count as correct, per field. E.g. a bilingual invoice where both the English
    /// and the Arabic supplier name are printed: <c>"alternatives": { "supplierName": ["…"] }</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string[]>? Alternatives { get; init; }

    /// <summary>Expected business decision against <c>master-data.json</c>. Optional (real invoices may not have it).</summary>
    public ExpectedValidation? Validation { get; init; }
}

/// <param name="Scenario">What was planted, e.g. "clean", "price-increase", "iban-changed".</param>
/// <param name="ErrorCodes">Rule codes that must fire (errors only), sorted.</param>
public sealed record ExpectedValidation(string Scenario, ValidationOutcome Outcome, IReadOnlyList<string> ErrorCodes);

public sealed record ExpectedMeta
{
    public string Source { get; init; } = "unknown";
    public string Language { get; init; } = "unknown";
    public string Quality { get; init; } = "unknown";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? Notes { get; init; }
}

public sealed record DatasetItem(string Id, string DocumentPath, ExpectedFile Expected);

public static class DatasetLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // Keep Arabic readable in the files instead of \u escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IReadOnlyList<DatasetItem> Load(string dataDir, string? filter = null)
    {
        var items = new List<DatasetItem>();
        foreach (var expectedPath in Directory.GetFiles(dataDir, "*" + DatasetChecker.ExpectedSuffix).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(expectedPath)[..^DatasetChecker.ExpectedSuffix.Length];
            if (filter is not null && !id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var document = Directory.GetFiles(dataDir, id + ".*")
                .FirstOrDefault(p => !p.EndsWith(DatasetChecker.ExpectedSuffix, StringComparison.OrdinalIgnoreCase));
            if (document is null)
            {
                continue; // reported by `check`
            }

            var expected = JsonSerializer.Deserialize<ExpectedFile>(File.ReadAllText(expectedPath), JsonOptions)
                ?? throw new InvalidDataException($"{expectedPath} is empty.");
            items.Add(new DatasetItem(id, document, expected));
        }

        return items;
    }

    public static void Save(string path, ExpectedFile file) =>
        File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions) + Environment.NewLine);
}
