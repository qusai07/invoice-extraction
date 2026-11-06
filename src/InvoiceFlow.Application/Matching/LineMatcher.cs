using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Domain.MasterData;

namespace InvoiceFlow.Application.Matching;

public enum LineMatchMethod
{
    None,
    Sku,
    Description,
}

/// <param name="InvoiceLineIndex">0-based index into InvoiceDto.Lines.</param>
/// <param name="Suggestions">For unmatched lines: best PO candidates, for the reviewer.</param>
public sealed record LineMatch(int InvoiceLineIndex, PoLine? PoLine, LineMatchMethod Method, double Score, IReadOnlyList<PoLine> Suggestions);

/// <summary>
/// One-to-one matching of invoice lines to PO lines. Scores every pair, then assigns greedily from the highest score.
/// Conservative: price alone is never enough. Description or SKU must agree, otherwise the line goes to review.
/// </summary>
public static class LineMatcher
{
    public const double MinScore = 0.6;

    public static IReadOnlyList<LineMatch> Match(IReadOnlyList<InvoiceLineDto> invoiceLines, IReadOnlyList<PoLine> poLines)
    {
        ArgumentNullException.ThrowIfNull(invoiceLines);
        ArgumentNullException.ThrowIfNull(poLines);

        var pairs = new List<(int Inv, int Po, double Score, LineMatchMethod Method)>();
        for (var i = 0; i < invoiceLines.Count; i++)
        {
            for (var p = 0; p < poLines.Count; p++)
            {
                var (score, method) = Score(invoiceLines[i], poLines[p]);
                pairs.Add((i, p, score, method));
            }
        }

        var matchedInv = new (int Po, double Score, LineMatchMethod Method)?[invoiceLines.Count];
        var usedPo = new bool[poLines.Count];

        // Highest score first; ties broken by order (stable) so results are deterministic.
        foreach (var pair in pairs.OrderByDescending(x => x.Score).ThenBy(x => x.Inv).ThenBy(x => x.Po))
        {
            if (pair.Score < MinScore)
            {
                break;
            }

            if (matchedInv[pair.Inv] is null && !usedPo[pair.Po])
            {
                matchedInv[pair.Inv] = (pair.Po, pair.Score, pair.Method);
                usedPo[pair.Po] = true;
            }
        }

        var result = new List<LineMatch>(invoiceLines.Count);
        for (var i = 0; i < invoiceLines.Count; i++)
        {
            if (matchedInv[i] is { } m)
            {
                result.Add(new LineMatch(i, poLines[m.Po], m.Method, m.Score, []));
            }
            else
            {
                var suggestions = pairs.Where(x => x.Inv == i && x.Score > 0)
                    .OrderByDescending(x => x.Score).Take(3).Select(x => poLines[x.Po]).ToList();
                result.Add(new LineMatch(i, null, LineMatchMethod.None, 0, suggestions));
            }
        }

        return result;
    }

    public static (double Score, LineMatchMethod Method) Score(InvoiceLineDto inv, PoLine po)
    {
        ArgumentNullException.ThrowIfNull(inv);
        ArgumentNullException.ThrowIfNull(po);

        var price = PriceScore(inv.UnitPrice, po.UnitPrice);
        var fit = QuantityFit(inv.Quantity, po.RemainingToInvoice);

        if (!string.IsNullOrWhiteSpace(inv.Sku) && !string.IsNullOrWhiteSpace(po.Sku) &&
            string.Equals(Key(inv.Sku), Key(po.Sku), StringComparison.Ordinal))
        {
            // Same SKU: strong match. Price and quantity fit only decide between repeated SKUs.
            return (0.7 + 0.25 * price + 0.05 * fit, LineMatchMethod.Sku);
        }

        var description = Math.Max(
            MatchText.Containment(inv.Description, po.Description),
            MatchText.Containment(inv.Description, po.DescriptionAr));

        // Price + quantity alone max out at 0.5 < MinScore: the description must agree too.
        return (0.5 * description + 0.4 * price + 0.1 * fit, LineMatchMethod.Description);
    }

    /// <summary>
    /// How tightly the invoiced quantity fits what is still open on the PO line: 1 = exact, smaller = looser, 0 = doesn't fit.
    /// Makes repeated items (same SKU and price, different quantities) pair with the right PO lines.
    /// </summary>
    private static double QuantityFit(decimal? quantity, decimal remaining)
    {
        if (quantity is not { } q || q <= 0 || remaining <= 0 || q > remaining)
        {
            return 0;
        }

        return (double)(q / remaining);
    }

    /// <summary>1 when equal, falling linearly to 0 at 20% difference.</summary>
    private static double PriceScore(decimal? invoicePrice, decimal poPrice)
    {
        if (invoicePrice is not { } p || poPrice <= 0)
        {
            return 0;
        }

        var diff = (double)(Math.Abs(p - poPrice) / poPrice);
        return Math.Max(0, 1 - diff / 0.20);
    }

    private static string Key(string s) => string.Concat(MatchText.Tokens(s));
}
