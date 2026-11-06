using Azure.Monitor.OpenTelemetry.Exporter;
using InvoiceFlow.Application.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace InvoiceFlow.Infrastructure.Observability;

public static class TelemetrySetup
{
    /// <summary>
    /// OpenTelemetry traces, metrics and logs. Exporter is chosen by configuration:
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> → Azure Monitor (Application Insights);
    /// otherwise <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> → any OTLP backend (Grafana, Jaeger, Seq, …);
    /// otherwise nothing is exported (instrumentation stays in-process, cheap).
    /// </summary>
    /// <param name="configureTracing">Host-specific instrumentation (e.g. ASP.NET Core for the API).</param>
    public static IServiceCollection AddInvoiceFlowTelemetry(
        this IServiceCollection services,
        ILoggingBuilder logging,
        IConfiguration configuration,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logging);

        var appInsights = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        var otlp = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var useAzure = !string.IsNullOrWhiteSpace(appInsights);
        var useOtlp = !useAzure && !string.IsNullOrWhiteSpace(otlp);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(TelemetrySetup).Assembly.GetName().Version?.ToString()))
            .WithTracing(t =>
            {
                t.AddSource(InvoiceFlowTelemetry.Name).AddHttpClientInstrumentation();
                configureTracing?.Invoke(t);
                if (useAzure)
                {
                    t.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsights);
                }
                else if (useOtlp)
                {
                    t.AddOtlpExporter();
                }
            })
            .WithMetrics(m =>
            {
                m.AddMeter(InvoiceFlowTelemetry.Name).AddHttpClientInstrumentation().AddRuntimeInstrumentation();
                configureMetrics?.Invoke(m);
                if (useAzure)
                {
                    m.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsights);
                }
                else if (useOtlp)
                {
                    m.AddOtlpExporter();
                }
            });

        if (useAzure || useOtlp)
        {
            logging.AddOpenTelemetry(o =>
            {
                o.IncludeFormattedMessage = true;
                o.IncludeScopes = true;
                if (useAzure)
                {
                    o.AddAzureMonitorLogExporter(x => x.ConnectionString = appInsights);
                }
                else
                {
                    o.AddOtlpExporter();
                }
            });
        }

        return services;
    }
}
