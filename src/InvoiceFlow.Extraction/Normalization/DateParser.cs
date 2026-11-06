using System.Globalization;

namespace InvoiceFlow.Extraction.Normalization;

/// <summary>
/// Parses invoice dates as printed. Numeric dates are day-first by default (UAE/GCC convention).
/// Arabic month names are supported.
/// </summary>
public static class DateParser
{
    private static readonly (string Arabic, string English)[] ArabicMonths =
    [
        // Longer names first so "كانون الثاني" wins over "كانون".
        ("كانون الثاني", "Jan"), ("شباط", "Feb"), ("آذار", "Mar"), ("نيسان", "Apr"), ("أيار", "May"), ("حزيران", "Jun"),
        ("تموز", "Jul"), ("آب", "Aug"), ("أيلول", "Sep"), ("تشرين الأول", "Oct"), ("تشرين الثاني", "Nov"), ("كانون الأول", "Dec"),
        ("يناير", "Jan"), ("فبراير", "Feb"), ("مارس", "Mar"), ("أبريل", "Apr"), ("ابريل", "Apr"), ("إبريل", "Apr"),
        ("مايو", "May"), ("يونيو", "Jun"), ("يونيه", "Jun"), ("يوليو", "Jul"), ("يوليه", "Jul"), ("أغسطس", "Aug"),
        ("اغسطس", "Aug"), ("سبتمبر", "Sep"), ("أكتوبر", "Oct"), ("اكتوبر", "Oct"), ("نوفمبر", "Nov"), ("ديسمبر", "Dec"),
    ];

    private static readonly string[] NamedFormats =
    [
        "d MMM yyyy", "d MMMM yyyy", "d-MMM-yyyy", "d-MMMM-yyyy", "d MMM, yyyy", "MMM d, yyyy", "MMMM d, yyyy",
        "MMM d yyyy", "d-MMM-yy", "d MMM yy", "d/MMM/yyyy",
    ];

    private static readonly string[] IsoFormats = ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d"];

    private static readonly string[] DayFirstFormats = ["d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d/M/yy", "d-M-yy", "d.M.yy"];
    private static readonly string[] MonthFirstFormats = ["M/d/yyyy", "M-d-yyyy", "M.d.yyyy", "M/d/yy", "M-d-yy", "M.d.yy"];

    public static bool TryParse(string? raw, out DateOnly date, bool dayFirst = true)
    {
        date = default;
        var s = TextNormalizer.Normalize(raw);
        if (s.Length == 0)
        {
            return false;
        }

        foreach (var (arabic, english) in ArabicMonths)
        {
            s = s.Replace(arabic, english, StringComparison.Ordinal);
        }

        // Drop Arabic/English noise around dates: "14 Oct 2026م", "Date: ...", trailing "AD".
        s = s.Replace("م", string.Empty, StringComparison.Ordinal).Replace(".,", ",", StringComparison.Ordinal).Trim();
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        string[][] groups = [IsoFormats, NamedFormats, dayFirst ? DayFirstFormats : MonthFirstFormats];
        foreach (var formats in groups)
        {
            if (DateOnly.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date) &&
                date.Year is >= 2000 and <= 2100)
            {
                return true;
            }
        }

        date = default;
        return false;
    }
}
