using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Evals.Dataset;

namespace InvoiceFlow.Evals.Running;

/// <summary>
/// Returns the expected answer. Used to self-test the eval pipeline: it must score 100%.
/// If it doesn't, the scorer or the dataset is broken, not the model.
/// </summary>
public sealed class GroundTruthExtractor(IReadOnlyList<DatasetItem> items) : IInvoiceExtractor
{
    private readonly Dictionary<string, InvoiceDto> _byFile =
        items.ToDictionary(i => Path.GetFileName(i.DocumentPath), i => i.Expected.Invoice, StringComparer.OrdinalIgnoreCase);

    public string Name => "ground-truth";

    public Task<ExtractionResult> ExtractAsync(DocumentInput document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var usage = new ExtractionUsage { Extractor = Name, Model = "none", PromptVersion = "none", Pages = 1 };
        return Task.FromResult(_byFile.TryGetValue(document.FileName, out var invoice)
            ? new ExtractionResult { Status = ExtractionStatus.Succeeded, DocumentType = DocumentType.Invoice, Invoice = invoice, Usage = usage }
            : ExtractionResult.Fail("not in dataset", usage));
    }
}
