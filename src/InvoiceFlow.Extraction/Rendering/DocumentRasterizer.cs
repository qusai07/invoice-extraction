using InvoiceFlow.Application.Extraction;
using PDFtoImage;
using SkiaSharp;

namespace InvoiceFlow.Extraction.Rendering;

public sealed record PageImage(int PageNumber, string ContentType, byte[] Bytes);

public sealed record RasterizeResult(IReadOnlyList<PageImage> Pages, int TotalPages, string? Error)
{
    public bool Truncated => TotalPages > Pages.Count;
}

/// <summary>Turns a document into page images for a vision model. PDFs are rendered with PDFium.</summary>
public static class DocumentRasterizer
{
    public static RasterizeResult Rasterize(DocumentInput document, int dpi, int maxPages)
    {
        switch (document.ContentType)
        {
            case "image/png":
            case "image/jpeg":
                return new RasterizeResult([new PageImage(1, document.ContentType, document.Content.ToArray())], 1, null);

            case "application/pdf":
                return RenderPdf(document.Content.ToArray(), dpi, maxPages);

            default:
                return new RasterizeResult([], 0, $"Unsupported content type '{document.ContentType}'.");
        }
    }

    private static RasterizeResult RenderPdf(byte[] pdf, int dpi, int maxPages)
    {
        int total;
        try
        {
            total = Conversion.GetPageCount(pdf);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new RasterizeResult([], 0, $"Could not open PDF: {ex.Message}");
        }

        var pages = new List<PageImage>(Math.Min(total, maxPages));
        var options = new RenderOptions(Dpi: dpi, WithAnnotations: true, WithFormFill: true);

        for (var i = 0; i < total && i < maxPages; i++)
        {
            using var bitmap = Conversion.ToImage(pdf, page: i, options: options);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            pages.Add(new PageImage(i + 1, "image/png", data.ToArray()));
        }

        return new RasterizeResult(pages, total, null);
    }
}
