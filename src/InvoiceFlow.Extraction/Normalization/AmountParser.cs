using System.Globalization;
using System.Text;

namespace InvoiceFlow.Extraction.Normalization;

/// <summary>
/// Parses money / quantity text as printed on invoices: "AED 1,234.50", "١٬٢٣٤٫٥٠ د.إ", "1.234,50", "(250.00)".
/// </summary>
public static class AmountParser
{
    public static bool TryParse(string? raw, out decimal value)
    {
        value = 0;
        var s = TextNormalizer.Normalize(raw);
        if (s.Length == 0)
        {
            return false;
        }

        var negative = s.Contains('(', StringComparison.Ordinal) && s.Contains(')', StringComparison.Ordinal);

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c) || c is '.' or ',')
            {
                sb.Append(c);
            }
            else if (c == '-' && sb.Length == 0)
            {
                negative = true;
            }
            else if (c == '-' && sb.Length > 0 && char.IsAsciiDigit(sb[^1]))
            {
                // Trailing minus (some ERPs print "250.00-"). Only counts if nothing numeric follows.
                negative = true;
            }
        }

        var number = sb.ToString().Trim('.', ',');
        if (number.Length == 0 || !number.Any(char.IsAsciiDigit))
        {
            return false;
        }

        number = ResolveSeparators(number);
        if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        if (negative)
        {
            value = -value;
        }

        return true;
    }

    /// <summary>Returns a string with only digits and at most one '.' as the decimal point.</summary>
    private static string ResolveSeparators(string n)
    {
        var lastDot = n.LastIndexOf('.');
        var lastComma = n.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            // Both present: the last one is the decimal separator.
            var decimalSep = lastDot > lastComma ? '.' : ',';
            var thousandsSep = decimalSep == '.' ? ',' : '.';
            return n.Replace(thousandsSep.ToString(), string.Empty, StringComparison.Ordinal)
                .Replace(decimalSep, '.');
        }

        if (lastComma >= 0)
        {
            var commas = n.Count(c => c == ',');
            var digitsAfter = n.Length - lastComma - 1;
            // "1,234" / "1,234,567" → thousands. "12,5" / "12,50" → decimal.
            return commas == 1 && digitsAfter is 1 or 2
                ? n.Replace(',', '.')
                : n.Replace(",", string.Empty, StringComparison.Ordinal);
        }

        if (lastDot >= 0 && n.Count(c => c == '.') > 1)
        {
            // "1.234.567" → thousands.
            return n.Replace(".", string.Empty, StringComparison.Ordinal);
        }

        // Single '.' is a decimal point (UAE/GCC convention).
        return n;
    }
}
