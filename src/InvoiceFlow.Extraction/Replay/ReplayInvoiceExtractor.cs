using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceFlow.Application.Extraction;

namespace InvoiceFlow.Extraction.Replay;

/// <summary>
/// DEVELOPMENT ONLY. Pretends to be the AI: for a synthetic dataset file (syn-0001.pdf) it returns the answer stored in
/// syn-0001.expected.json, with approximate evidence (English number formatting, page 1) so the review UI can be
/// demonstrated without Azure credentials. The API refuses to register it outside the Development environment.
/// </summary>
public sealed class ReplayInvoiceExtractor(string datasetDirectory) : IInvoiceExtractor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Name => "replay";

    public async Task<ExtractionResult> ExtractAsync(DocumentInput document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var usage = new ExtractionUsage { Extractor = Name, Model = "none", PromptVersion = "none", Pages = 1 };

        // Only a bare file name is used, so a crafted name can't point outside the dataset folder.
        var path = Path.Combine(datasetDirectory, Path.GetFileNameWithoutExtension(Path.GetFileName(document.FileName)) + ".expected.json");
        if (!File.Exists(path))
        {
            return ExtractionResult.Fail("Replay extractor: not a dataset file (only works for evals/data documents).", usage);
        }

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        var invoice = json.RootElement.GetProperty("invoice").Deserialize<InvoiceDto>(Json)!;

        return new ExtractionResult
        {
            Status = ExtractionStatus.Succeeded,
            DocumentType = DocumentType.Invoice,
            Invoice = invoice,
            Evidence = Evidence(invoice),
            Usage = usage,
        };
    }

    private static List<FieldEvidence> Evidence(InvoiceDto i)
    {
        var e = new List<FieldEvidence>();
        void Add(string field, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                e.Add(new FieldEvidence(field, 1, text));
            }
        }

        static string? M(decimal? v) => v?.ToString("#,##0.00", Inv);

        Add("supplierName", i.SupplierName);
        Add("supplierTrn", i.SupplierTrn);
        Add("customerTrn", i.CustomerTrn);
        Add("invoiceNumber", i.InvoiceNumber);
        Add("poNumber", i.PoNumber);
        Add("iban", i.Iban);
        Add("subtotal", M(i.Subtotal));
        Add("vatTotal", M(i.VatTotal));
        Add("total", M(i.Total));
        for (var n = 0; n < i.Lines.Count; n++)
        {
            Add($"lines[{n}].sku", i.Lines[n].Sku);
            Add($"lines[{n}].unitPrice", M(i.Lines[n].UnitPrice));
            Add($"lines[{n}].lineTotal", M(i.Lines[n].LineTotal));
        }

        return e;
    }
}
