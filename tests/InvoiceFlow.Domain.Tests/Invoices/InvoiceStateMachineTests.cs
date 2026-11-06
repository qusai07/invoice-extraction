using InvoiceFlow.Domain.Invoices;

namespace InvoiceFlow.Domain.Tests.Invoices;

public class InvoiceStateMachineTests
{
    [Fact]
    public void Every_status_has_a_transition_entry()
    {
        foreach (var status in Enum.GetValues<InvoiceStatus>())
        {
            // Throws nothing and returns a list (possibly empty) for every status.
            Assert.NotNull(InvoiceStateMachine.AllowedFrom(status));
        }
    }

    [Theory]
    [InlineData(InvoiceStatus.Posted)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Closed)]
    public void Terminal_states_have_no_exits(InvoiceStatus status) =>
        Assert.True(InvoiceStateMachine.IsTerminal(status));

    [Fact]
    public void Happy_path_touchless_is_allowed()
    {
        InvoiceStatus[] path =
        [
            InvoiceStatus.Received, InvoiceStatus.Preprocessing, InvoiceStatus.Extracting, InvoiceStatus.Extracted,
            InvoiceStatus.Validating, InvoiceStatus.Approved, InvoiceStatus.Posting, InvoiceStatus.Posted,
        ];

        for (var i = 0; i < path.Length - 1; i++)
        {
            Assert.True(InvoiceStateMachine.CanTransition(path[i], path[i + 1]), $"{path[i]} → {path[i + 1]}");
        }
    }

    [Fact]
    public void Review_loop_is_allowed() =>
        Assert.True(
            InvoiceStateMachine.CanTransition(InvoiceStatus.Validating, InvoiceStatus.NeedsReview) &&
            InvoiceStateMachine.CanTransition(InvoiceStatus.NeedsReview, InvoiceStatus.Validating));

    [Theory]
    [InlineData(InvoiceStatus.Extracted, InvoiceStatus.Approved)]   // must pass validation first
    [InlineData(InvoiceStatus.Received, InvoiceStatus.Posting)]
    [InlineData(InvoiceStatus.NeedsReview, InvoiceStatus.Posting)]  // must be approved first
    [InlineData(InvoiceStatus.Posted, InvoiceStatus.Posting)]       // never post twice
    public void Skipping_validation_or_approval_is_rejected(InvoiceStatus from, InvoiceStatus to)
    {
        Assert.False(InvoiceStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => InvoiceStateMachine.EnsureCanTransition(from, to));
    }

    [Fact]
    public void Posted_is_only_reachable_from_Posting()
    {
        var sources = Enum.GetValues<InvoiceStatus>()
            .Where(s => InvoiceStateMachine.CanTransition(s, InvoiceStatus.Posted));

        Assert.Equal([InvoiceStatus.Posting], sources);
    }
}
