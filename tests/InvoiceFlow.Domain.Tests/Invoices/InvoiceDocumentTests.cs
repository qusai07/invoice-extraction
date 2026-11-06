using InvoiceFlow.Domain.Invoices;

namespace InvoiceFlow.Domain.Tests.Invoices;

public class InvoiceDocumentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private static InvoiceDocument New() =>
        InvoiceDocument.Receive(Guid.NewGuid(), "default", "a.pdf", "application/pdf", 10, "abc", "x/original.pdf", "upload", "clara", T0);

    [Fact]
    public void Receive_raises_audit_event_by_submitter()
    {
        var doc = New();
        var e = Assert.Single(doc.NewEvents);
        Assert.Equal(("clara", "Received", InvoiceStatus.Received), (e.Actor, e.Action, e.ToStatus!.Value));
    }

    [Fact]
    public void Transition_records_from_and_to()
    {
        var doc = New();
        doc.TransitionTo(InvoiceStatus.Preprocessing, "system", T0.AddSeconds(1));

        var e = doc.NewEvents[^1];
        Assert.Equal(InvoiceStatus.Received, e.FromStatus);
        Assert.Equal(InvoiceStatus.Preprocessing, e.ToStatus);
        Assert.Equal(InvoiceStatus.Preprocessing, doc.Status);
        Assert.Equal(T0.AddSeconds(1), doc.StatusChangedAt);
    }

    [Fact]
    public void Illegal_transition_throws_and_changes_nothing()
    {
        var doc = New();
        Assert.Throws<InvalidOperationException>(() => doc.TransitionTo(InvoiceStatus.Posted, "mallory", T0));
        Assert.Equal(InvoiceStatus.Received, doc.Status);
        Assert.Single(doc.NewEvents);
    }

    [Fact]
    public void State_round_trip_preserves_everything_and_raises_no_events()
    {
        var doc = New();
        doc.UpdateSummary("SUP-1", "Gulf Star", "INV-1", new DateOnly(2026, 10, 1), "AED", 100m, "PO-1");
        doc.MarkResolvedBy("clara");

        var restored = InvoiceDocument.Restore(doc.ToState());

        Assert.Equal(doc.ToState(), restored.ToState());
        Assert.Empty(restored.NewEvents);
    }
}
