using System.Collections.Concurrent;
using System.Globalization;
using InvoiceFlow.Application.Workflow;

namespace InvoiceFlow.Infrastructure.MockErp;

/// <summary>Mock ERP posting. Idempotent: the same key always returns the same reference.</summary>
public sealed class MockErpPoster : IErpPoster
{
    private readonly ConcurrentDictionary<string, string> _posted = new(StringComparer.Ordinal);
    private int _counter;

    public IReadOnlyDictionary<string, string> Posted => _posted;

    public Task<string> PostInvoiceAsync(ErpPostRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reference = _posted.GetOrAdd(request.IdempotencyKey,
            _ => "ERP-" + Interlocked.Increment(ref _counter).ToString("000000", CultureInfo.InvariantCulture));
        return Task.FromResult(reference);
    }
}
