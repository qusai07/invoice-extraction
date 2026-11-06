using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Domain.MasterData;

namespace InvoiceFlow.Application.Matching;

public enum SupplierMatchMethod
{
    None,
    Trn,
    Name,
}

public sealed record SupplierMatch(Supplier? Supplier, SupplierMatchMethod Method, double Score)
{
    public static readonly SupplierMatch NotFound = new(null, SupplierMatchMethod.None, 0);
}

/// <summary>TRN first (exact), then name/alias similarity (EN + AR). Ambiguous name matches are not resolved.</summary>
public sealed class SupplierResolver(IMasterDataService masterData)
{
    public const double MinNameScore = 0.6;
    public const double MinMargin = 0.15;

    public async Task<SupplierMatch> ResolveAsync(InvoiceDto invoice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (!string.IsNullOrEmpty(invoice.SupplierTrn) &&
            await masterData.FindSupplierByTrnAsync(invoice.SupplierTrn, cancellationToken).ConfigureAwait(false) is { } byTrn)
        {
            return new SupplierMatch(byTrn, SupplierMatchMethod.Trn, 1);
        }

        if (string.IsNullOrWhiteSpace(invoice.SupplierName))
        {
            return SupplierMatch.NotFound;
        }

        var candidates = await masterData.SearchSuppliersAsync(invoice.SupplierName, cancellationToken).ConfigureAwait(false);
        var scored = candidates
            .Select(s => (Supplier: s, Score: NameScore(invoice.SupplierName, s)))
            .OrderByDescending(x => x.Score)
            .ToList();

        if (scored.Count == 0 || scored[0].Score < MinNameScore)
        {
            return SupplierMatch.NotFound;
        }

        if (scored.Count > 1 && scored[0].Score - scored[1].Score < MinMargin)
        {
            return SupplierMatch.NotFound; // ambiguous → a person decides
        }

        return new SupplierMatch(scored[0].Supplier, SupplierMatchMethod.Name, scored[0].Score);
    }

    public static double NameScore(string name, Supplier supplier) =>
        new[] { supplier.Name, supplier.NameAr }.Concat(supplier.Aliases)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => MatchText.Similarity(name, n, dropLegalForms: true))
            .DefaultIfEmpty(0)
            .Max();
}
