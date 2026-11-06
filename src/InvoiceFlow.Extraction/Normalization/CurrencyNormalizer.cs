namespace InvoiceFlow.Extraction.Normalization;

public static class CurrencyNormalizer
{
    private static readonly (string Marker, string Code)[] Markers =
    [
        ("د.إ", "AED"), ("درهم", "AED"), ("dhs", "AED"), ("dh", "AED"), ("aed", "AED"),
        ("ر.س", "SAR"), ("ريال سعودي", "SAR"), ("sar", "SAR"),
        ("ر.ق", "QAR"), ("qar", "QAR"),
        ("د.ك", "KWD"), ("kwd", "KWD"),
        ("ر.ع", "OMR"), ("omr", "OMR"),
        ("د.ب", "BHD"), ("bhd", "BHD"),
        ("usd", "USD"), ("us$", "USD"), ("$", "USD"),
        ("eur", "EUR"), ("€", "EUR"),
        ("gbp", "GBP"), ("£", "GBP"),
        ("inr", "INR"), ("₹", "INR"),
    ];

    /// <summary>Maps printed currency text to an ISO 4217 code. Returns null when not recognized.</summary>
    public static string? Normalize(string? raw)
    {
        var s = TextNormalizer.Normalize(raw);
        if (s.Length == 0)
        {
            return null;
        }

        // Already an ISO code, e.g. "AED".
        if (s.Length == 3 && s.All(char.IsAsciiLetter))
        {
            return s.ToUpperInvariant();
        }

        foreach (var (marker, code) in Markers)
        {
            if (s.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        return null;
    }
}
