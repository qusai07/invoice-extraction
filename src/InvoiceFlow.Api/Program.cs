using System.Text.Json.Serialization;
using InvoiceFlow.Api;
using InvoiceFlow.Api.Security;
using InvoiceFlow.Extraction;
using InvoiceFlow.Infrastructure;
using InvoiceFlow.Infrastructure.Observability;
using InvoiceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddInvoiceFlowInfrastructure(builder.Configuration, builder.Environment.ContentRootPath, builder.Environment.IsDevelopment());
builder.Services.AddInvoiceFlowTelemetry(builder.Logging, builder.Configuration, "invoiceflow-api",
    t => t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health")),
    m => m.AddAspNetCoreInstrumentation());

// Extraction is optional: without it, documents go to review for manual entry.
builder.Services.AddInvoiceExtraction(builder.Configuration, builder.Environment.ContentRootPath, builder.Environment.IsDevelopment());

// Production runs the pipeline in InvoiceFlow.Worker; development can run it in the API process.
if (builder.Configuration.GetValue<bool>("Pipeline:RunInProcess"))
{
    builder.Services.AddInvoiceFlowPipelineWorker();
}

builder.AddInvoiceFlowAuth();
builder.AddInvoiceFlowHardening();
builder.Services.AddLoginRateLimit();

// Behind a TLS-terminating proxy (Azure Container Apps ingress, reverse proxy): trust X-Forwarded-*.
var behindProxy = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
if (behindProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // The app is only reachable through the platform's ingress, so the proxy's address isn't fixed.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>();
    if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
    {
        await db.Database.MigrateAsync();
    }

    // Built-in roles + first administrator (from Bootstrap:* secrets) — ADR-0009.
    await InvoiceFlow.Infrastructure.Identity.IdentitySeeder.SeedAsync(
        db, builder.Configuration, TimeProvider.System, app.Logger);
}

if (behindProxy)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseApiSecurityHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// TLS terminates in front of the API (the web app's proxy locally, Container Apps ingress in Azure), so the API must
// not redirect to its own https port: the browser would be sent to a different origin. Opt in only when the API is
// exposed directly to browsers over https.
if (builder.Configuration.GetValue("Https:RedirectHttp", false))
{
    app.UseHttpsRedirection();
}
app.UseAuthentication();
app.UseRateLimiter(); // after authentication: limits are per user
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapInvoiceFlowHealth();
app.MapAuthEndpoints();
app.MapInvoiceEndpoints();
app.MapAdminEndpoints();
app.MapAuditExport();

await app.RunAsync();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
