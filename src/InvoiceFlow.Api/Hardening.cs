using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace InvoiceFlow.Api;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>Requests per minute per signed-in user (anonymous: per IP), all endpoints.</summary>
    public int RequestsPerMinute { get; set; } = 600;

    /// <summary>Uploads per minute per user. Bulk import should use a dedicated channel (email / API batch).</summary>
    public int UploadsPerMinute { get; set; } = 60;

    /// <summary>
    /// Sign-in attempts (password and code steps, two-step setup) per minute per IP address: slows password spraying.
    /// Generous because a whole office may share one address; each account is also locked after 5 wrong attempts.
    /// </summary>
    public int LoginsPerMinute { get; set; } = 30;
}

public static class Hardening
{
    public const string UploadPolicy = "uploads";
    public const long MaxRequestBytes = 25 * 1024 * 1024;

    public static void AddInvoiceFlowHardening(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Size limits at the server and multipart layers (the endpoint also checks the file size).
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = MaxRequestBytes;
            k.AddServerHeader = false;
        });
        builder.Services.Configure<FormOptions>(f =>
        {
            f.MultipartBodyLengthLimit = InvoiceEndpoints.MaxUploadBytes;
            f.ValueCountLimit = 32;
        });

        var limits = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(PartitionKey(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.RequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                }));

            o.AddPolicy(UploadPolicy, ctx =>
                RateLimitPartition.GetTokenBucketLimiter(PartitionKey(ctx), _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Math.Max(1, limits.UploadsPerMinute),
                    TokensPerPeriod = Math.Max(1, limits.UploadsPerMinute),
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                }));
        });
    }

    /// <summary>Per user when signed in, per client IP otherwise.</summary>
    private static string PartitionKey(HttpContext ctx) =>
        ctx.User.FindFirstValue("oid") ?? ctx.User.FindFirstValue("sub")
        ?? "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

    /// <summary>Headers for a JSON API that is never meant to be rendered or framed by a browser.</summary>
    public static IApplicationBuilder UseApiSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            h.CacheControl = "no-store";
            await next();
        });

    /// <summary>/health/live: the process runs. /health/ready: dependencies (database) are reachable.</summary>
    public static void MapInvoiceFlowHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
    }
}
