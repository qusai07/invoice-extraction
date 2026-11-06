using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvoiceFlow.Extraction.Contract;

/// <summary>
/// What the model returns: values transcribed as printed (strings) + page numbers.
/// Shape is fixed by <c>invoice-extraction.v1.schema.json</c>. C# turns this into an InvoiceDto.
/// </summary>
public sealed record RawExtraction
{
    public string DocumentType { get; init; } = "other";
    public RawField? SupplierName { get; init; }
    public RawField? SupplierTrn { get; init; }
    public RawField? CustomerTrn { get; init; }
    public RawField? InvoiceNumber { get; init; }
    public RawField? InvoiceDate { get; init; }
    public RawField? DueDate { get; init; }
    public RawField? PoNumber { get; init; }
    public RawField? Currency { get; init; }
    public RawField? Subtotal { get; init; }
    public RawField? VatTotal { get; init; }
    public RawField? Total { get; init; }
    public RawField? Iban { get; init; }
    public IReadOnlyList<RawLine> Lines { get; init; } = [];
}

public sealed record RawField(string? Value, int? Page);

public sealed record RawLine(
    string? Sku,
    string? Description,
    string? Quantity,
    string? UnitPrice,
    string? LineTotal,
    string? VatRate,
    int? Page);

/// <summary>Versioned prompt + schema pair. Bump both together; the version is stored with every extraction.</summary>
public static class ExtractionContract
{
    public const string Version = "v1";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Prompt { get; } = ReadResource($"extract-invoice.{Version}.md");

    public static string Schema { get; } = ReadResource($"invoice-extraction.{Version}.schema.json");

    public static RawExtraction Deserialize(string json) =>
        JsonSerializer.Deserialize<RawExtraction>(json, JsonOptions)
        ?? throw new JsonException("Model returned an empty JSON document.");

    private static string ReadResource(string fileName)
    {
        var assembly = typeof(ExtractionContract).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource {name} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
