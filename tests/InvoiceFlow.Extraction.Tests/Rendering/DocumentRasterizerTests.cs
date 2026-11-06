using InvoiceFlow.Application.Extraction;
using InvoiceFlow.Extraction.Rendering;

namespace InvoiceFlow.Extraction.Tests.Rendering;

public class DocumentRasterizerTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];

    private static string DataDir([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) })
        {
            var dir = start is null ? null : new DirectoryInfo(start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InvoiceFlow.sln")))
            {
                dir = dir.Parent;
            }

            if (dir is not null)
            {
                return Path.Combine(dir.FullName, "evals", "data");
            }
        }

        throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public void Renders_every_pdf_page_as_png()
    {
        // The golden dataset contains multi-page synthetic PDFs (tag "multi-page").
        var pdf = Directory.GetFiles(DataDir(), "*.pdf").Select(DocumentInput.FromFile).First();

        var result = DocumentRasterizer.Rasterize(pdf, dpi: 72, maxPages: 10);

        Assert.Null(result.Error);
        Assert.NotEmpty(result.Pages);
        Assert.All(result.Pages, p => Assert.Equal(PngSignature, p.Bytes.Take(4)));
        Assert.Equal(Enumerable.Range(1, result.Pages.Count), result.Pages.Select(p => p.PageNumber));
    }

    [Fact]
    public void Truncates_at_max_pages()
    {
        var multiPage = Directory.GetFiles(DataDir(), "*.pdf")
            .Select(DocumentInput.FromFile)
            .Select(d => DocumentRasterizer.Rasterize(d, dpi: 36, maxPages: 1))
            .FirstOrDefault(r => r.TotalPages > 1);

        Assert.NotNull(multiPage);
        Assert.Single(multiPage.Pages);
        Assert.True(multiPage.Truncated);
    }

    [Fact]
    public void Images_pass_through()
    {
        var png = new DocumentInput("x.png", "image/png", PngSignature);
        var result = DocumentRasterizer.Rasterize(png, 200, 10);
        Assert.Single(result.Pages);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Corrupt_pdf_returns_error_not_exception()
    {
        var bad = new DocumentInput("x.pdf", "application/pdf", "not a pdf"u8.ToArray());
        var result = DocumentRasterizer.Rasterize(bad, 200, 10);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Pages);
    }

    [Fact]
    public void Unsupported_type_returns_error()
    {
        var heic = new DocumentInput("x.heic", "image/heic", new byte[] { 1 });
        Assert.NotNull(DocumentRasterizer.Rasterize(heic, 200, 10).Error);
    }
}
