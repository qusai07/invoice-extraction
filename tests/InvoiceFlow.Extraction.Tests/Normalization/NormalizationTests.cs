using InvoiceFlow.Extraction.Normalization;

namespace InvoiceFlow.Extraction.Tests.Normalization;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("١٢٣٤٥٦٧٨٩٠", "1234567890")]
    [InlineData("۱۲۳۴۵۶۷۸۹۰", "1234567890")]          // Extended Arabic-Indic (Persian/Urdu)
    [InlineData("١٬٢٣٤٫٥٠", "1,234.50")]
    [InlineData("  INV  -  42 ", "INV - 42")]
    [InlineData("‏٥٪‎", "5%")]
    public void Normalizes_digits_separators_and_spaces(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void DigitsOnly_extracts_trn() =>
        Assert.Equal("100123456700003", TextNormalizer.DigitsOnly("TRN: ١٠٠ ١٢٣ ٤٥٦ ٧٠٠ ٠٠٣"));
}

public class AmountParserTests
{
    [Theory]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("AED 1,234.50", 1234.50)]
    [InlineData("1234.5", 1234.5)]
    [InlineData("1.234,50", 1234.50)]          // European
    [InlineData("1,234", 1234)]                // comma as thousands
    [InlineData("1,234,567", 1234567)]
    [InlineData("12,50", 12.50)]               // comma as decimal
    [InlineData("1.234.567", 1234567)]
    [InlineData("١٬٢٣٤٫٥٠ د.إ", 1234.50)]
    [InlineData("٣٧٢٧٫٥٠", 3727.50)]
    [InlineData("(250.00)", -250.00)]
    [InlineData("-250", -250)]
    [InlineData("250.00-", -250.00)]
    [InlineData("0", 0)]
    [InlineData("20", 20)]
    public void Parses(string input, decimal expected)
    {
        Assert.True(AmountParser.TryParse(input, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N/A")]
    [InlineData("AED")]
    [InlineData(".,")]
    public void Rejects(string? input) => Assert.False(AmountParser.TryParse(input, out _));
}

public class PercentParserTests
{
    [Theory]
    [InlineData("5%", 0.05)]
    [InlineData("5 %", 0.05)]
    [InlineData("٥٪", 0.05)]
    [InlineData("5", 0.05)]
    [InlineData("0.05", 0.05)]
    [InlineData("0%", 0)]
    [InlineData("VAT 5.00%", 0.05)]
    public void Parses(string input, decimal expected)
    {
        Assert.True(PercentParser.TryParse(input, out var rate));
        Assert.Equal(expected, rate);
    }

    [Theory]
    [InlineData("Exempt")]
    [InlineData("معفى")]
    public void Exempt_is_null_rate(string input)
    {
        Assert.True(PercentParser.TryParse(input, out var rate));
        Assert.Null(rate);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("500%")]
    public void Rejects(string input) => Assert.False(PercentParser.TryParse(input, out _));
}

public class DateParserTests
{
    [Theory]
    [InlineData("2026-10-14")]
    [InlineData("14/10/2026")]
    [InlineData("14-10-2026")]
    [InlineData("14.10.2026")]
    [InlineData("14 Oct 2026")]
    [InlineData("14 October 2026")]
    [InlineData("Oct 14, 2026")]
    [InlineData("14-Oct-26")]
    [InlineData("١٤/١٠/٢٠٢٦")]
    [InlineData("١٤ أكتوبر ٢٠٢٦")]
    [InlineData("14 اكتوبر 2026م")]
    [InlineData("١٤ تشرين الأول ٢٠٢٦")]
    public void Parses_to_14_Oct_2026(string input)
    {
        Assert.True(DateParser.TryParse(input, out var date), input);
        Assert.Equal(new DateOnly(2026, 10, 14), date);
    }

    [Fact]
    public void Numeric_dates_are_day_first_by_default()
    {
        Assert.True(DateParser.TryParse("03/04/2026", out var date));
        Assert.Equal(new DateOnly(2026, 4, 3), date);
    }

    [Fact]
    public void Month_first_when_configured()
    {
        Assert.True(DateParser.TryParse("03/04/2026", out var date, dayFirst: false));
        Assert.Equal(new DateOnly(2026, 3, 4), date);
    }

    [Theory]
    [InlineData("31/02/2026")]
    [InlineData("not a date")]
    [InlineData("14/10/1890")]
    public void Rejects(string input) => Assert.False(DateParser.TryParse(input, out _));
}

public class CurrencyNormalizerTests
{
    [Theory]
    [InlineData("AED", "AED")]
    [InlineData("aed", "AED")]
    [InlineData("د.إ", "AED")]
    [InlineData("درهم إماراتي", "AED")]
    [InlineData("Dhs.", "AED")]
    [InlineData("US$", "USD")]
    [InlineData("$", "USD")]
    [InlineData("ر.س", "SAR")]
    [InlineData("€", "EUR")]
    [InlineData("CHF", "CHF")]
    public void Maps_to_iso(string input, string expected) => Assert.Equal(expected, CurrencyNormalizer.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("money")]
    public void Unknown_is_null(string? input) => Assert.Null(CurrencyNormalizer.Normalize(input));
}
