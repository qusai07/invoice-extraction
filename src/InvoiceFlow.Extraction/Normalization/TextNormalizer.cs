using System.Text;

namespace InvoiceFlow.Extraction.Normalization;

/// <summary>Low-level text clean-up shared by all parsers. Pure functions, no culture dependence.</summary>
public static class TextNormalizer
{
    /// <summary>
    /// Converts Arabic-Indic (٠-٩) and Extended Arabic-Indic (۰-۹) digits to Latin, Arabic decimal/thousands/percent
    /// signs to Latin equivalents, removes bidi control marks, and collapses whitespace.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        var lastWasSpace = false;

        foreach (var ch in input)
        {
            var c = ch switch
            {
                >= '٠' and <= '٩' => (char)('0' + (ch - '٠')),
                >= '۰' and <= '۹' => (char)('0' + (ch - '۰')),
                '٫' => '.',   // Arabic decimal separator
                '٬' => ',',   // Arabic thousands separator
                '،' => ',',   // Arabic comma
                '٪' => '%',   // Arabic percent sign
                ' ' or ' ' or ' ' or '\t' or '\r' or '\n' => ' ',
                _ => ch,
            };

            // Drop bidi / zero-width marks (LRM, RLM, ALM, ZWJ/ZWNJ, embedding controls).
            if (c is '‎' or '‏' or '؜' or '​' or '‌' or '‍' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩'))
            {
                continue;
            }

            if (c == ' ')
            {
                if (!lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            sb.Append(c);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Normalized text, or null when nothing is left.</summary>
    public static string? CleanOrNull(string? input)
    {
        var s = Normalize(input);
        return s.Length == 0 ? null : s;
    }

    public static string DigitsOnly(string? input)
    {
        var s = Normalize(input);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
