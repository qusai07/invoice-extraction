using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Application.Matching;
using InvoiceFlow.Application.Tests.Validation;
using InvoiceFlow.Domain.MasterData;

namespace InvoiceFlow.Application.Tests.Matching;

public class MatchTextTests
{
    [Theory]
    [InlineData("Gulf Star Trading Co. LLC", "GULF STAR TRADING CO L.L.C", 1.0)]
    [InlineData("Gulf Star Trading Co. LLC", "Gulf Star Trading", 1.0)]
    [InlineData("شركة نجمة الخليج للتجارة ذ.م.م", "نجمة الخليج للتجارة", 1.0)]
    [InlineData("مؤسسة النور للقرطاسية", "موسسة النور للقرطاسيه", 1.0)]   // hamza / ta marbuta variants
    [InlineData("Gulf Star Trading", "Blue Wave Cleaning", 0.0)]
    public void Supplier_name_similarity(string a, string b, double expected) =>
        Assert.Equal(expected, MatchText.Similarity(a, b, dropLegalForms: true), 2);
}

public class SupplierResolverTests
{
    private static SupplierResolver Resolver(params Supplier[] suppliers) => new(new FakeMasterData(suppliers, []));

    [Fact]
    public async Task Trn_wins()
    {
        var match = await Resolver(Fixtures.Supplier, Fixtures.OtherSupplier)
            .ResolveAsync(new InvoiceDto { SupplierName = "Something else", SupplierTrn = Fixtures.Supplier.Trn });
        Assert.Equal(SupplierMatchMethod.Trn, match.Method);
        Assert.Equal("SUP-1", match.Supplier?.Id);
    }

    [Fact]
    public async Task Alias_matches()
    {
        var match = await Resolver(Fixtures.Supplier, Fixtures.OtherSupplier).ResolveAsync(new InvoiceDto { SupplierName = "GULF STAR" });
        Assert.Equal("SUP-1", match.Supplier?.Id);
    }

    [Fact]
    public async Task Ambiguous_name_is_not_resolved()
    {
        var twin = Fixtures.Supplier with { Id = "SUP-9", Trn = "100000000000013", Aliases = [] };
        var match = await Resolver(Fixtures.Supplier, twin).ResolveAsync(new InvoiceDto { SupplierName = "Gulf Star Trading Co. LLC" });
        Assert.Null(match.Supplier);
    }
}

public class LineMatcherTests
{
    private static readonly IReadOnlyList<PoLine> Po = Fixtures.Po.Lines;

    [Fact]
    public void Matches_by_sku_regardless_of_order()
    {
        var lines = Fixtures.Invoice.Lines.Reverse().ToList();
        var matches = LineMatcher.Match(lines, Po);
        Assert.Equal([20, 10], matches.Select(m => m.PoLine!.LineNo));
        Assert.All(matches, m => Assert.Equal(LineMatchMethod.Sku, m.Method));
    }

    [Fact]
    public void Matches_arabic_description_without_sku()
    {
        var line = new InvoiceLineDto { Description = "خرطوشة حبر ليزر 26A", Quantity = 5, UnitPrice = 20m, LineTotal = 100m };
        var match = Assert.Single(LineMatcher.Match([line], Po));
        Assert.Equal(20, match.PoLine?.LineNo);
        Assert.Equal(LineMatchMethod.Description, match.Method);
    }

    [Fact]
    public void Bilingual_description_matches_english_po_line()
    {
        var line = new InvoiceLineDto { Description = "Laser toner cartridge 26A خرطوشة حبر ليزر 26A", Quantity = 5, UnitPrice = 20m, LineTotal = 100m };
        Assert.Equal(20, Assert.Single(LineMatcher.Match([line], Po)).PoLine?.LineNo);
    }

    [Fact]
    public void Repeated_item_same_price_pairs_by_quantity_fit()
    {
        // Same SKU and price twice; only the quantities tell the PO lines apart.
        PoLine[] po =
        [
            new() { LineNo = 1, Sku = "CEM", Description = "Cement", Quantity = 7, UnitPrice = 18m },
            new() { LineNo = 2, Sku = "CEM", Description = "Cement", Quantity = 32, UnitPrice = 18m },
        ];
        InvoiceLineDto[] lines =
        [
            new() { Sku = "CEM", Description = "Cement", Quantity = 5, UnitPrice = 18m, LineTotal = 90m },
            new() { Sku = "CEM", Description = "Cement", Quantity = 30, UnitPrice = 18m, LineTotal = 540m },
        ];

        var matches = LineMatcher.Match(lines, po);
        Assert.Equal([1, 2], matches.Select(m => m.PoLine!.LineNo));
    }

    [Fact]
    public void Price_alone_is_not_enough()
    {
        var line = new InvoiceLineDto { Description = "Consulting services", Quantity = 1, UnitPrice = 100m, LineTotal = 100m };
        var match = Assert.Single(LineMatcher.Match([line], Po));
        Assert.Null(match.PoLine);
        Assert.NotEmpty(match.Suggestions);
    }

    [Fact]
    public void Repeated_sku_is_assigned_one_to_one_by_closest_price()
    {
        PoLine[] po =
        [
            new() { LineNo = 1, Sku = "CAT", Description = "Catering", Quantity = 10, UnitPrice = 30m },
            new() { LineNo = 2, Sku = "CAT", Description = "Catering", Quantity = 10, UnitPrice = 45m },
        ];
        InvoiceLineDto[] lines =
        [
            new() { Sku = "CAT", Description = "Catering", Quantity = 10, UnitPrice = 45m, LineTotal = 450m },
            new() { Sku = "CAT", Description = "Catering", Quantity = 10, UnitPrice = 30m, LineTotal = 300m },
        ];

        var matches = LineMatcher.Match(lines, po);
        Assert.Equal([2, 1], matches.Select(m => m.PoLine!.LineNo));
    }
}
