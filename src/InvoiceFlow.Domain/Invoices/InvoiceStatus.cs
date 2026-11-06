namespace InvoiceFlow.Domain.Invoices;

/// <summary>Lifecycle of an invoice document. See PLAN.md §6.</summary>
public enum InvoiceStatus
{
    Received = 0,
    Preprocessing = 1,
    Extracting = 2,
    Extracted = 3,
    Validating = 4,
    NeedsReview = 5,
    PendingApproval = 6,
    Approved = 7,
    Posting = 8,
    Posted = 9,
    PostingFailed = 10,
    Rejected = 11,
    Duplicate = 12,
    Closed = 13,
}
