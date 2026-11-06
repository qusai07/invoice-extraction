using System.Globalization;
using System.Text.Json;

namespace InvoiceFlow.Evals;

/// <summary>
/// Validates the golden dataset: every document has a matching <c>.expected.json</c>, the JSON has the
/// required shape, and the hand-labelled numbers are internally consistent (catches labelling typos).
/// Format: evals/data/README.md.
/// </summary>
public static class DatasetChecker
{
    public const string ExpectedSuffix = ".expected.json";
    private const decimal Tolerance = 0.01m;

    private static readonly HashSet<string> DocumentExtensions =
        new([".pdf", ".png", ".jpg", ".jpeg", ".heic", ".tif", ".tiff"], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] RequiredInvoiceFields =
        ["supplierName", "invoiceNumber", "invoiceDate", "currency", "total", "lines"];

    public static int Run(string dataDir, TextWriter output)
    {
        if (!Directory.Exists(dataDir))
        {
            output.WriteLine($"Dataset folder not found: {dataDir}");
            return 1;
        }

        var errors = new List<string>();
        var files = Directory.GetFiles(dataDir);
        var documents = files.Where(f => DocumentExtensions.Contains(Path.GetExtension(f))).ToList();
        var expected = files.Where(f => f.EndsWith(ExpectedSuffix, StringComparison.OrdinalIgnoreCase)).ToList();

        var documentIds = documents.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedIds = expected.Select(f => Path.GetFileName(f)[..^ExpectedSuffix.Length])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        errors.AddRange(documentIds.Except(expectedIds).Select(id => $"{id}: document has no {ExpectedSuffix}"));
        errors.AddRange(expectedIds.Except(documentIds).Select(id => $"{id}: {ExpectedSuffix} has no document"));

        foreach (var path in expected)
        {
            errors.AddRange(CheckExpected(path).Select(e => $"{Path.GetFileName(path)}: {e}"));
        }

        output.WriteLine($"Documents: {documents.Count}   Expected files: {expected.Count}   Errors: {errors.Count}");
        foreach (var error in errors)
        {
            output.WriteLine($"  ✗ {error}");
        }

        return errors.Count == 0 ? 0 : 1;
    }

    public static IEnumerable<string> CheckExpected(string path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (JsonException ex)
        {
            return [$"invalid JSON ({ex.Message})"];
        }

        using (doc)
        {
            return CheckInvoice(doc.RootElement).ToList();
        }
    }

    public static IEnumerable<string> CheckInvoice(JsonElement root)
    {
        if (!root.TryGetProperty("invoice", out var invoice) || invoice.ValueKind != JsonValueKind.Object)
        {
            yield return "missing 'invoice' object";
            yield break;
        }

        foreach (var field in RequiredInvoiceFields)
        {
            if (!invoice.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                yield return $"invoice.{field} is required";
            }
        }

        if (invoice.TryGetProperty("invoiceDate", out var date) && date.ValueKind == JsonValueKind.String &&
            !DateOnly.TryParseExact(date.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            yield return "invoice.invoiceDate must be yyyy-MM-dd";
        }

        decimal linesSum = 0;
        if (invoice.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
        {
            var n = 0;
            foreach (var line in lines.EnumerateArray())
            {
                n++;
                var qty = Number(line, "quantity");
                var price = Number(line, "unitPrice");
                var lineTotal = Number(line, "lineTotal");
                if (lineTotal is null)
                {
                    yield return $"lines[{n}].lineTotal is required";
                    continue;
                }

                linesSum += lineTotal.Value;
                if (qty is not null && price is not null && Math.Abs(qty.Value * price.Value - lineTotal.Value) > Tolerance)
                {
                    yield return $"lines[{n}]: quantity × unitPrice ({qty * price}) ≠ lineTotal ({lineTotal})";
                }
            }
        }

        var subtotal = Number(invoice, "subtotal");
        var vat = Number(invoice, "vatTotal");
        var total = Number(invoice, "total");

        if (subtotal is not null && Math.Abs(linesSum - subtotal.Value) > Tolerance)
        {
            yield return $"Σ lineTotal ({linesSum}) ≠ subtotal ({subtotal})";
        }

        if (subtotal is not null && vat is not null && total is not null && Math.Abs(subtotal.Value + vat.Value - total.Value) > Tolerance)
        {
            yield return $"subtotal + vatTotal ({subtotal + vat}) ≠ total ({total})";
        }
    }

    private static decimal? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;
}
