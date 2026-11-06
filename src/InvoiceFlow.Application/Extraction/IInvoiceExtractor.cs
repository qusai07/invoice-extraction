namespace InvoiceFlow.Application.Extraction;

/// <summary>
/// Turns a document into an <see cref="InvoiceDto"/>. Implementations only read; they never decide (ADR-0002).
/// Implementations must not throw for bad documents. They return <see cref="ExtractionStatus.Failed"/> instead.
/// </summary>
public interface IInvoiceExtractor
{
    /// <summary>Stable name used in config, eval reports and audit (e.g. "azure-openai").</summary>
    string Name { get; }

    Task<ExtractionResult> ExtractAsync(DocumentInput document, CancellationToken cancellationToken = default);
}

public sealed record DocumentInput(string FileName, string ContentType, ReadOnlyMemory<byte> Content)
{
    public static DocumentInput FromFile(string path)
    {
        var contentType = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".PDF" => "application/pdf",
            ".PNG" => "image/png",
            ".JPG" or ".JPEG" => "image/jpeg",
            ".TIF" or ".TIFF" => "image/tiff",
            ".HEIC" => "image/heic",
            _ => "application/octet-stream",
        };
        return new DocumentInput(Path.GetFileName(path), contentType, File.ReadAllBytes(path));
    }
}

public enum ExtractionStatus
{
    Succeeded,

    /// <summary>The model says this is not an invoice (receipt, letter, …).</summary>
    NotAnInvoice,

    /// <summary>Could not extract (unreadable, unsupported format, provider error, refusal).</summary>
    Failed,
}

public enum DocumentType
{
    Unknown,
    Invoice,
    CreditNote,
    Other,
}

public sealed record ExtractionResult
{
    public required ExtractionStatus Status { get; init; }
    public InvoiceDto? Invoice { get; init; }
    public DocumentType DocumentType { get; init; }

    /// <summary>Where each field was found and the exact text as printed (for UI highlighting and audit).</summary>
    public IReadOnlyList<FieldEvidence> Evidence { get; init; } = [];

    /// <summary>Values the model read but C# could not normalize (e.g. an unparseable date). The field is null.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    public required ExtractionUsage Usage { get; init; }
    public string? Error { get; init; }

    public static ExtractionResult Fail(string error, ExtractionUsage usage) =>
        new() { Status = ExtractionStatus.Failed, Error = error, Usage = usage };
}

/// <param name="Field">Field path, e.g. "total" or "lines[2].unitPrice".</param>
/// <param name="Page">1-based page number, or null when unknown.</param>
/// <param name="SourceText">The value exactly as printed on the document.</param>
public sealed record FieldEvidence(string Field, int? Page, string SourceText);

public sealed record ExtractionUsage
{
    public required string Extractor { get; init; }
    public string? Model { get; init; }
    public string? PromptVersion { get; init; }
    public int Pages { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal CostUsd { get; init; }
    public TimeSpan Duration { get; init; }
}
