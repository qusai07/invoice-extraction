using System.Collections.Frozen;

namespace InvoiceFlow.Domain.Invoices;

/// <summary>
/// Single source of truth for allowed status transitions. Any transition not listed here is a bug.
/// </summary>
public static class InvoiceStateMachine
{
    private static readonly FrozenDictionary<InvoiceStatus, InvoiceStatus[]> Transitions =
        new Dictionary<InvoiceStatus, InvoiceStatus[]>
        {
            [InvoiceStatus.Received] = [InvoiceStatus.Preprocessing, InvoiceStatus.Rejected, InvoiceStatus.Duplicate],
            [InvoiceStatus.Preprocessing] = [InvoiceStatus.Extracting, InvoiceStatus.Rejected],
            // Extraction can fail after all fallbacks → a human types it in.
            [InvoiceStatus.Extracting] = [InvoiceStatus.Extracted, InvoiceStatus.NeedsReview, InvoiceStatus.Rejected],
            [InvoiceStatus.Extracted] = [InvoiceStatus.Validating],
            [InvoiceStatus.Validating] =
                [InvoiceStatus.Approved, InvoiceStatus.PendingApproval, InvoiceStatus.NeedsReview, InvoiceStatus.Duplicate],
            // Corrected → re-validate; supervisor override → approved (or pending approval above the limit); or reject;
            // or read again when the first read produced nothing (reader not configured / failed).
            [InvoiceStatus.NeedsReview] =
                [InvoiceStatus.Validating, InvoiceStatus.Approved, InvoiceStatus.PendingApproval, InvoiceStatus.Rejected, InvoiceStatus.Extracting],
            [InvoiceStatus.PendingApproval] = [InvoiceStatus.Approved, InvoiceStatus.Rejected, InvoiceStatus.NeedsReview],
            [InvoiceStatus.Approved] = [InvoiceStatus.Posting],
            [InvoiceStatus.Posting] = [InvoiceStatus.Posted, InvoiceStatus.PostingFailed],
            [InvoiceStatus.PostingFailed] = [InvoiceStatus.Posting, InvoiceStatus.NeedsReview],
            [InvoiceStatus.Duplicate] = [InvoiceStatus.Closed],
            [InvoiceStatus.Posted] = [],
            [InvoiceStatus.Rejected] = [],
            [InvoiceStatus.Closed] = [],
        }.ToFrozenDictionary();

    public static bool CanTransition(InvoiceStatus from, InvoiceStatus to) =>
        Transitions.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyList<InvoiceStatus> AllowedFrom(InvoiceStatus from) =>
        Transitions.TryGetValue(from, out var targets) ? targets : [];

    public static bool IsTerminal(InvoiceStatus status) => AllowedFrom(status).Count == 0;

    /// <exception cref="InvalidOperationException">When the transition is not allowed.</exception>
    public static void EnsureCanTransition(InvoiceStatus from, InvoiceStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Invoice status transition {from} → {to} is not allowed.");
        }
    }
}
