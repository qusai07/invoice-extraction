using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using InvoiceFlow.Application.Security;

namespace InvoiceFlow.Infrastructure.Security;

public sealed class ClamAvOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3310;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Scans with a ClamAV daemon (clamd) over TCP using the INSTREAM command. For on-prem installs; in Azure,
/// Microsoft Defender for Storage malware scanning is the managed alternative.
/// Any doubt (unreachable, timeout, unexpected reply) → <see cref="MalwareScannerUnavailableException"/> → upload refused.
/// </summary>
public sealed class ClamAvScanner(ClamAvOptions options) : IMalwareScanner
{
    private const int ChunkSize = 64 * 1024;

    public string Name => "clamav";

    public async Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        string reply;

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();

            await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            var length = new byte[4];
            for (var offset = 0; offset < content.Length; offset += ChunkSize)
            {
                var chunk = content.Slice(offset, Math.Min(ChunkSize, content.Length - offset));
                BinaryPrimitives.WriteUInt32BigEndian(length, (uint)chunk.Length);
                await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(chunk, timeout.Token).ConfigureAwait(false);
            }

            BinaryPrimitives.WriteUInt32BigEndian(length, 0); // end of stream
            await stream.WriteAsync(length, timeout.Token).ConfigureAwait(false);

            reply = await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new MalwareScannerUnavailableException($"ClamAV at {options.Host}:{options.Port} is unavailable: {ex.Message}", ex);
        }

        return Parse(reply);
    }

    /// <summary>"stream: OK" → clean; "stream: Eicar-Test-Signature FOUND" → infected; anything else → unavailable.</summary>
    public static ScanResult Parse(string reply)
    {
        var r = reply.Trim('\0', ' ', '\n', '\r');
        if (r.EndsWith(": OK", StringComparison.Ordinal))
        {
            return ScanResult.Clean;
        }

        if (r.EndsWith(" FOUND", StringComparison.Ordinal))
        {
            var start = r.IndexOf(": ", StringComparison.Ordinal);
            var signature = start >= 0 ? r[(start + 2)..^" FOUND".Length] : r;
            return new ScanResult(ScanVerdict.Infected, signature);
        }

        // e.g. "INSTREAM size limit exceeded. ERROR": treat as "could not scan", never as clean.
        throw new MalwareScannerUnavailableException($"Unexpected ClamAV reply: {r}");
    }

    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[512];
        var sb = new StringBuilder();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            sb.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (sb.ToString().Contains('\0', StringComparison.Ordinal) || sb.Length > 4096)
            {
                break;
            }
        }

        return sb.ToString();
    }
}
