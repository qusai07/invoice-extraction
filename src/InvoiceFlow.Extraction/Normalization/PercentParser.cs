namespace InvoiceFlow.Extraction.Normalization;

public static class PercentParser
{
    private static readonly string[] ExemptMarkers = ["exempt", "معفى", "معفاة", "معفي", "n/a", "out of scope"];

    /// <summary>
    /// Parses a VAT rate into a decimal fraction ("5%" → 0.05, "0.05" → 0.05, "5" → 0.05).
    /// Exempt markers return <c>true</c> with <paramref name="rate"/> = null.
    /// </summary>
    public static bool TryParse(string? raw, out decimal? rate)
    {
        rate = null;
        var s = TextNormalizer.Normalize(raw);
        if (s.Length == 0)
        {
            return false;
        }

        if (ExemptMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!AmountParser.TryParse(s.Replace("%", string.Empty, StringComparison.Ordinal), out var value) || value < 0)
        {
            return false;
        }

        // "5%" or "5" → 5 → 0.05.  "0.05" → 0.05.  A real rate is never > 100%.
        var fraction = s.Contains('%', StringComparison.Ordinal) || value > 1 ? value / 100m : value;
        if (fraction > 1)
        {
            return false;
        }

        rate = fraction;
        return true;
    }
}
