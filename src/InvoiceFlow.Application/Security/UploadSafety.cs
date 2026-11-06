namespace InvoiceFlow.Application.Security;

/// <summary>Identifies a file by its first bytes ("magic numbers"). The client's Content-Type is never trusted.</summary>
public static class FileSignature
{
    public const string Pdf = "application/pdf";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    private static ReadOnlySpan<byte> PdfMagic => "%PDF-"u8;
    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];

    /// <returns>The detected media type, or null when it is none of the accepted formats.</returns>
    public static string? Detect(ReadOnlySpan<byte> content)
    {
        // Strict: the signature must be at offset 0. Readers tolerate junk before "%PDF-", but accepting it here
        // would let polyglot files (e.g. an image that is also a PDF) through.
        if (content.StartsWith(PdfMagic))
        {
            return Pdf;
        }

        if (content.StartsWith(PngMagic))
        {
            return Png;
        }

        return content.StartsWith(JpegMagic) ? Jpeg : null;
    }

    public static string ExtensionFor(string mediaType) => mediaType switch
    {
        Pdf => ".pdf",
        Png => ".png",
        Jpeg => ".jpg",
        _ => ".bin",
    };
}

public enum ScanVerdict
{
    Clean,
    Infected,
}

public sealed record ScanResult(ScanVerdict Verdict, string? Signature = null)
{
    public static readonly ScanResult Clean = new(ScanVerdict.Clean);
}

/// <summary>
/// Malware scan before a file is stored. Implementations throw <see cref="MalwareScannerUnavailableException"/> when
/// the scanner can't be reached: intake then fails closed (no unscanned file is accepted).
/// </summary>
public interface IMalwareScanner
{
    string Name { get; }

    Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
}

public sealed class MalwareScannerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// No inline scan. Only for development, or when scanning happens elsewhere (e.g. Microsoft Defender for Storage
/// scans each blob on upload). Must be chosen explicitly outside Development.
/// </summary>
public sealed class NoMalwareScanner(string name = "none") : IMalwareScanner
{
    public string Name { get; } = name;

    public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default) =>
        Task.FromResult(ScanResult.Clean);
}
