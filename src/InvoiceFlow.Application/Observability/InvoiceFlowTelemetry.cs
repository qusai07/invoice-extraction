using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace InvoiceFlow.Application.Observability;

/// <summary>
/// Traces and business metrics. Exported via OpenTelemetry (Application Insights or any OTLP backend).
/// Dashboards derive the PLAN.md §1 KPIs from these, e.g. touchless rate =
/// decisions{outcome=Approved, by=system} / invoices.received.
/// </summary>
public static class InvoiceFlowTelemetry
{
    public const string Name = "InvoiceFlow";

    public static readonly ActivitySource Source = new(Name, "1.0");
    private static readonly Meter Meter = new(Name, "1.0");

    public static readonly Counter<long> InvoicesReceived =
        Meter.CreateCounter<long>("invoiceflow.invoices.received", "{invoice}", "Documents accepted at intake.");

    public static readonly Counter<long> UploadsRejected =
        Meter.CreateCounter<long>("invoiceflow.uploads.rejected", "{upload}", "Uploads refused at intake (type mismatch, malware, scanner down).");

    public static readonly Counter<long> Decisions =
        Meter.CreateCounter<long>("invoiceflow.invoices.decisions", "{decision}", "Validation outcomes. Tags: outcome, by (system|human).");

    public static readonly Counter<long> HumanActions =
        Meter.CreateCounter<long>("invoiceflow.review.actions", "{action}", "Reviewer actions. Tag: action.");

    public static readonly Histogram<double> ExtractionCost =
        Meter.CreateHistogram<double>("invoiceflow.extraction.cost", "USD", "AI cost per extracted document.");

    public static readonly Histogram<double> StepDuration =
        Meter.CreateHistogram<double>("invoiceflow.pipeline.step.duration", "s", "Pipeline step duration. Tags: step, result.");

    public static readonly Counter<long> StepFailures =
        Meter.CreateCounter<long>("invoiceflow.pipeline.step.failures", "{failure}", "Pipeline step failures. Tags: step, final.");
}
