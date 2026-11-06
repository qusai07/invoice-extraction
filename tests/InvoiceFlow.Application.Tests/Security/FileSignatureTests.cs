using InvoiceFlow.Application.Security;

namespace InvoiceFlow.Application.Tests.Security;

public class FileSignatureTests
{
    [Fact]
    public void Pdf() => Assert.Equal(FileSignature.Pdf, FileSignature.Detect("%PDF-1.7\n..."u8));

    [Fact]
    public void Png() => Assert.Equal(FileSignature.Png, FileSignature.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]));

    [Fact]
    public void Jpeg() => Assert.Equal(FileSignature.Jpeg, FileSignature.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]));

    [Theory]
    [InlineData("MZ executable")]
    [InlineData("<html><script>")]
    [InlineData("PK zip / docx")]
    [InlineData("")]
    public void Anything_else_is_rejected(string content) =>
        Assert.Null(FileSignature.Detect(System.Text.Encoding.ASCII.GetBytes(content)));

    [Fact]
    public void Pdf_marker_later_in_the_file_is_not_enough()
    {
        // Polyglot: something else first, a PDF header later.
        var polyglot = System.Text.Encoding.ASCII.GetBytes("GIF89a....%PDF-1.4 ...");
        Assert.Null(FileSignature.Detect(polyglot));
    }
}
