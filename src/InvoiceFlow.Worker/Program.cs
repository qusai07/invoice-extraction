using InvoiceFlow.Extraction;
using InvoiceFlow.Infrastructure;
using InvoiceFlow.Infrastructure.Observability;

// Runs pipeline jobs (extract → validate → post). Scale out by running more instances;
// the SQL job queue hands each job to exactly one worker (ADR-0005).
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddInvoiceFlowInfrastructure(builder.Configuration, builder.Environment.ContentRootPath, builder.Environment.IsDevelopment());
builder.Services.AddInvoiceFlowTelemetry(builder.Logging, builder.Configuration, "invoiceflow-worker");
builder.Services.AddInvoiceExtraction(builder.Configuration, builder.Environment.ContentRootPath, builder.Environment.IsDevelopment());
builder.Services.AddInvoiceFlowPipelineWorker();

var host = builder.Build();
await host.RunAsync();
