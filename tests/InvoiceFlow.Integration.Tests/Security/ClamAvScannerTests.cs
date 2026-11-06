using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using InvoiceFlow.Application.Security;
using InvoiceFlow.Infrastructure.Security;

namespace InvoiceFlow.Integration.Tests.Security;

/// <summary>ClamAV adapter against a tiny fake clamd that speaks the INSTREAM protocol.</summary>
public class ClamAvScannerTests
{
    private const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    /// <summary>Reads the INSTREAM framing exactly like clamd and replies based on the content.</summary>
    private static async Task<(int Port, Task<byte[]> Received)> FakeClamd(Func<byte[], string> reply)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var command = new byte[10];
            await stream.ReadExactlyAsync(command);
            Assert.Equal("zINSTREAM\0", Encoding.ASCII.GetString(command));

            using var data = new MemoryStream();
            var len = new byte[4];
            while (true)
            {
                await stream.ReadExactlyAsync(len);
                var n = (int)BinaryPrimitives.ReadUInt32BigEndian(len);
                if (n == 0)
                {
                    break;
                }

                var chunk = new byte[n];
                await stream.ReadExactlyAsync(chunk);
                data.Write(chunk);
            }

            var bytes = data.ToArray();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply(bytes) + "\0"));
            listener.Stop();
            return bytes;
        });

        return (port, received);
    }

    [Fact]
    public async Task Clean_file_is_clean_and_sent_completely_in_chunks()
    {
        var payload = new byte[200_000]; // > 3 chunks of 64 KB
        Random.Shared.NextBytes(payload);
        var (port, received) = await FakeClamd(_ => "stream: OK");

        var result = await new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = port }).ScanAsync(payload);

        Assert.Equal(ScanVerdict.Clean, result.Verdict);
        Assert.Equal(payload, await received);
    }

    [Fact]
    public async Task Eicar_is_reported_with_signature()
    {
        var (port, _) = await FakeClamd(b => Encoding.ASCII.GetString(b).Contains("EICAR", StringComparison.Ordinal)
            ? "stream: Eicar-Test-Signature FOUND"
            : "stream: OK");

        var result = await new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = port }).ScanAsync(Encoding.ASCII.GetBytes(Eicar));

        Assert.Equal(ScanVerdict.Infected, result.Verdict);
        Assert.Equal("Eicar-Test-Signature", result.Signature);
    }

    [Fact]
    public async Task Unreachable_scanner_is_unavailable_not_clean()
    {
        // Port 1 on loopback: connection refused.
        var scanner = new ClamAvScanner(new ClamAvOptions { Host = "127.0.0.1", Port = 1, Timeout = TimeSpan.FromSeconds(3) });
        await Assert.ThrowsAsync<MalwareScannerUnavailableException>(() => scanner.ScanAsync(new byte[] { 1, 2, 3 }));
    }

    [Theory]
    [InlineData("INSTREAM size limit exceeded. ERROR")]
    [InlineData("")]
    [InlineData("something unexpected")]
    public void Unexpected_reply_is_never_treated_as_clean(string reply) =>
        Assert.Throws<MalwareScannerUnavailableException>(() => ClamAvScanner.Parse(reply));
}
