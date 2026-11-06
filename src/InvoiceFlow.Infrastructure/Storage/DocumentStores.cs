using Azure.Storage.Blobs;
using InvoiceFlow.Application.Workflow;

namespace InvoiceFlow.Infrastructure.Storage;

internal static class StoragePaths
{
    /// <summary>"{id}/original.pdf". The user-supplied file name is never part of the path (no traversal, no odd characters).</summary>
    public static string For(Guid documentId, string fileExtension)
    {
        var ext = new string((fileExtension ?? "").ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '.').Take(10).ToArray());
        if (!ext.StartsWith('.'))
        {
            ext = ext.Length == 0 ? ".bin" : "." + ext;
        }

        return $"{documentId:N}/original{ext}";
    }
}

/// <summary>Local folder. For development, tests and on-prem installs without Blob storage.</summary>
public sealed class FileSystemDocumentStore(string root) : IDocumentStore
{
    private readonly string _root = Path.GetFullPath(root);

    public async Task<string> SaveAsync(Guid documentId, string fileExtension, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        var relative = StoragePaths.For(documentId, fileExtension);
        var full = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // CreateNew: originals are immutable.
        await using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        return relative;
    }

    public Task<byte[]> ReadAsync(string storagePath, CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(Resolve(storagePath), cancellationToken);

    private string Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Storage path escapes the storage root.");
        }

        return full;
    }
}

/// <summary>
/// Azure Blob Storage (Azurite locally). Blobs are written once and never overwritten.
/// In Azure, authenticate with managed identity (ServiceUri) rather than a connection string with keys.
/// </summary>
public sealed class AzureBlobDocumentStore(BlobContainerClient container, bool requireDefenderVerdict = false) : IDocumentStore
{
    /// <summary>Blob index tags written by Microsoft Defender for Storage malware scanning.</summary>
    public const string DefenderResultTag = "Malware Scanning scan result";
    public const string DefenderClean = "No threats found";

    private bool _ensured;

    public async Task<string> SaveAsync(Guid documentId, string fileExtension, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        if (!_ensured)
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            _ensured = true;
        }

        var path = StoragePaths.For(documentId, fileExtension);
        await container.GetBlobClient(path).UploadAsync(BinaryData.FromBytes(content), overwrite: false, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<byte[]> ReadAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        var blob = container.GetBlobClient(storagePath);

        if (requireDefenderVerdict)
        {
            // Defender scans after upload. Until its verdict tag exists, nobody (pipeline or download) reads the file.
            var tags = (await blob.GetTagsAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Value.Tags;
            if (!tags.TryGetValue(DefenderResultTag, out var verdict))
            {
                throw new MalwareScanPendingException($"Malware scan of {storagePath} has not finished yet.");
            }

            if (!string.Equals(verdict, DefenderClean, StringComparison.OrdinalIgnoreCase))
            {
                throw new MalwareDetectedException($"Microsoft Defender for Storage verdict for {storagePath}: {verdict}. File is blocked.");
            }
        }

        var result = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return result.Value.Content.ToArray();
    }
}

/// <summary>Transient: the pipeline retries with backoff until the scan verdict exists.</summary>
public sealed class MalwareScanPendingException(string message) : Exception(message);

/// <summary>The file is malicious. It is never returned to the pipeline or to users.</summary>
public sealed class MalwareDetectedException(string message) : Exception(message);
