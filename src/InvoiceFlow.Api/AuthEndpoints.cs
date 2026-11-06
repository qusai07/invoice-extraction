using System.Security.Claims;
using System.Threading.RateLimiting;
using InvoiceFlow.Api.Security;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Infrastructure.Identity;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Api;

/// <summary>Sign-in, session refresh, logout, password change (ADR-0009).</summary>
public static class AuthEndpoints
{
    public const string RefreshCookie = "if_refresh";
    public const string LoginRateLimit = "login";

    /// <summary>Cookie endpoints also require this header: a cross-site form can't set it (CSRF defence in depth).</summary>
    public const string CsrfHeader = "X-InvoiceFlow";

    public sealed record LoginRequest(string Email, string Password);

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public sealed record MeResponse(
        string Id, string? Email, string Name, IReadOnlyCollection<string> Roles, IReadOnlyCollection<string> Permissions,
        decimal ApprovalThreshold, decimal? ApprovalLimit, bool ApprovalUnlimited, bool MustChangePassword, string Language,
        bool MfaEnabled = false, bool MfaSetupRequired = false);

    /// <summary>Password accepted; send <see cref="Challenge"/> with the code to /api/auth/login/code.</summary>
    public sealed record CodeStepResponse(bool CodeRequired, string Challenge);

    public sealed record CodeLoginRequest(string Challenge, string Code);

    public sealed record CodeRequest(string Code);

    public sealed record DisableMfaRequest(string Password, string Code);

    public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

    public sealed record SessionResponse(string AccessToken, DateTimeOffset ExpiresAt, MeResponse User);

    // Same answer for unknown email, wrong password and a locked account (lockout still applies, silently).
    private const string SignInFailedMessage = "Incorrect email or password.";

    public static void AddLoginRateLimit(this IServiceCollection services) =>
        services.Configure<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(o =>
            o.AddPolicy(LoginRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
                "ip:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) })));

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/auth").WithTags("Auth");

        g.MapPost("/login", async Task<IResult> (LoginRequest body, HttpContext http, IdentityService identity, IOptions<ValidationSettings> settings, CancellationToken ct) =>
            {
                var outcome = await identity.LoginAsync(body.Email ?? "", body.Password ?? "", Client(http), ct);
                return outcome switch
                {
                    { Session: { } s } => Issue(http, s, settings.Value),
                    { MfaChallenge: { } c } => TypedResults.Ok(new CodeStepResponse(true, c)),
                    _ => TypedResults.Problem(SignInFailedMessage, statusCode: StatusCodes.Status401Unauthorized),
                };
            })
            .AllowAnonymous().RequireRateLimiting(LoginRateLimit).DisableAntiforgery();

        // Second step: the 6-digit code from the authenticator app, or a one-time recovery code.
        g.MapPost("/login/code", async Task<IResult> (CodeLoginRequest body, HttpContext http, IdentityService identity, IOptions<ValidationSettings> settings, CancellationToken ct) =>
            {
                var outcome = await identity.LoginWithCodeAsync(body.Challenge ?? "", body.Code ?? "", Client(http), ct);
                return outcome.Session is { } s
                    ? Issue(http, s, settings.Value)
                    : TypedResults.Problem("That code didn't work. Check the code in your app (it changes every 30 seconds) and try again.",
                        statusCode: StatusCodes.Status401Unauthorized);
            })
            .AllowAnonymous().RequireRateLimiting(LoginRateLimit).DisableAntiforgery();

        // ---- two-step sign-in, for the signed-in user (allowed while restricted, so required setup can happen) ----
        var mfa = g.MapGroup("/mfa");
        mfa.MapGet("/", async Task<IResult> (ClaimsPrincipal user, IdentityService identity, CancellationToken ct) =>
            UserId(user) is { } id ? TypedResults.Ok(await identity.MfaStatusAsync(id, ct)) : LocalOnly());

        mfa.MapPost("/setup", async Task<IResult> (ClaimsPrincipal user, IdentityService identity, MfaSecretProtector protector, CancellationToken ct) =>
            {
                if (UserId(user) is not { } id)
                {
                    return LocalOnly();
                }

                if (!protector.IsConfigured)
                {
                    return TypedResults.Problem($"Two-step sign-in isn't set up on this server yet (missing {InvoiceFlow.Infrastructure.Identity.SessionOptions.SectionName}:MfaEncryptionKey). Ask an administrator.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                if ((await identity.MfaStatusAsync(id, ct)).Enabled)
                {
                    return TypedResults.Problem("Two-step sign-in is already on. Turn it off first to move to a new phone.", statusCode: StatusCodes.Status409Conflict);
                }

                return TypedResults.Ok(await identity.BeginMfaSetupAsync(id, ct));
            }).DisableAntiforgery();

        mfa.MapPost("/enable", async Task<IResult> (CodeRequest body, ClaimsPrincipal user, HttpContext http, IdentityService identity, CancellationToken ct) =>
            {
                if (UserId(user) is not { } id)
                {
                    return LocalOnly();
                }

                var codes = await identity.EnableMfaAsync(id, body.Code ?? "", Client(http), ct);
                return codes is null ? WrongCode() : TypedResults.Ok(new RecoveryCodesResponse(codes));
            }).RequireRateLimiting(LoginRateLimit).DisableAntiforgery();

        mfa.MapPost("/recovery-codes", async Task<IResult> (CodeRequest body, ClaimsPrincipal user, HttpContext http, IdentityService identity, CancellationToken ct) =>
            {
                if (UserId(user) is not { } id)
                {
                    return LocalOnly();
                }

                var codes = await identity.NewRecoveryCodesAsync(id, body.Code ?? "", Client(http), ct);
                return codes is null ? WrongCode() : TypedResults.Ok(new RecoveryCodesResponse(codes));
            }).RequireRateLimiting(LoginRateLimit).DisableAntiforgery();

        mfa.MapPost("/disable", async Task<IResult> (DisableMfaRequest body, ClaimsPrincipal user, HttpContext http, IdentityService identity, CancellationToken ct) =>
            {
                if (UserId(user) is not { } id)
                {
                    return LocalOnly();
                }

                if ((await identity.MfaStatusAsync(id, ct)).Required)
                {
                    return TypedResults.Problem("Two-step sign-in is required for your role, so it can't be turned off.", statusCode: StatusCodes.Status403Forbidden);
                }

                return await identity.DisableMfaAsync(id, body.Password ?? "", body.Code ?? "", Client(http), ct)
                    ? TypedResults.NoContent()
                    : TypedResults.Problem("The password or the code is not correct.", statusCode: StatusCodes.Status400BadRequest);
            }).RequireRateLimiting(LoginRateLimit).DisableAntiforgery();

        g.MapPost("/refresh", async Task<IResult> (HttpContext http, IdentityService identity, IOptions<ValidationSettings> settings, CancellationToken ct) =>
            {
                if (!HasCsrfHeader(http) || !http.Request.Cookies.TryGetValue(RefreshCookie, out var token))
                {
                    return TypedResults.Problem("No session.", statusCode: StatusCodes.Status401Unauthorized);
                }

                var outcome = await identity.RefreshAsync(token, Client(http), ct);
                if (outcome.Session is { } s)
                {
                    return Issue(http, s, settings.Value);
                }

                if (outcome.Error != AuthError.RefreshRace)
                {
                    ClearCookie(http);
                }

                return TypedResults.Problem(outcome.Error == AuthError.RefreshRace ? "Retry." : "Session expired. Please sign in again.",
                    statusCode: outcome.Error == AuthError.RefreshRace ? StatusCodes.Status409Conflict : StatusCodes.Status401Unauthorized);
            })
            .AllowAnonymous().DisableAntiforgery();

        g.MapPost("/logout", async Task<IResult> (HttpContext http, IdentityService identity, CancellationToken ct) =>
            {
                if (!HasCsrfHeader(http))
                {
                    return TypedResults.Problem("Missing header.", statusCode: StatusCodes.Status400BadRequest);
                }

                http.Request.Cookies.TryGetValue(RefreshCookie, out var token);
                await identity.LogoutAsync(token, Client(http), ct);
                ClearCookie(http);
                return TypedResults.NoContent();
            })
            .AllowAnonymous().DisableAntiforgery();

        g.MapPost("/logout-everywhere", async Task<IResult> (ClaimsPrincipal user, HttpContext http, IdentityService identity, CancellationToken ct) =>
            {
                if (!Guid.TryParse(user.FindFirstValue("sub"), out var id))
                {
                    return TypedResults.Problem("Only available for signed-in accounts.", statusCode: StatusCodes.Status400BadRequest);
                }

                await identity.LogoutEverywhereAsync(id, id.ToString(), Client(http), ct);
                ClearCookie(http);
                return TypedResults.NoContent();
            });

        g.MapPost("/change-password", async Task<IResult> (ChangePasswordRequest body, ClaimsPrincipal user, HttpContext http, IdentityService identity,
                IOptions<ValidationSettings> settings, CancellationToken ct) =>
            {
                if (!Guid.TryParse(user.FindFirstValue("sub"), out var id))
                {
                    return TypedResults.Problem("Only available for local accounts.", statusCode: StatusCodes.Status400BadRequest);
                }

                var outcome = await identity.ChangePasswordAsync(id, body.CurrentPassword ?? "", body.NewPassword ?? "", Client(http), ct);
                return outcome switch
                {
                    { Session: { } s } => Issue(http, s, settings.Value),
                    { Error: AuthError.WeakPassword } => TypedResults.ValidationProblem(
                        new Dictionary<string, string[]> { ["newPassword"] = outcome.Problems!.ToArray() }),
                    _ => TypedResults.Problem("The current password is not correct.", statusCode: StatusCodes.Status400BadRequest),
                };
            })
            .DisableAntiforgery();

        // Who am I, as the API sees it. The UI uses this for menus; the API still enforces every permission.
        app.MapGet("/api/me", async (ClaimsPrincipal user, PermissionResolver resolver, IOptions<ValidationSettings> settings, CancellationToken ct) =>
            {
                if (Guid.TryParse(user.FindFirstValue("sub"), out var id) && await resolver.GetAsync(id, ct) is { } current)
                {
                    return TypedResults.Ok(ToMe(current, settings.Value));
                }

                // Development sign-in (no user row): describe the header identity.
                var u = user.ToUserContext();
                return TypedResults.Ok(new MeResponse(u.Id, null, u.Name, u.Roles.Order(StringComparer.Ordinal).ToList(),
                    u.Permissions.Order(StringComparer.Ordinal).ToList(), settings.Value.ApprovalThreshold, null, true, false, "en"));
            })
            .RequireAuthorization()
            .WithTags("Me");
    }

    public static MeResponse ToMe(CurrentUser u, ValidationSettings settings) =>
        new(u.Id.ToString(), u.Email, u.DisplayName, u.Roles, u.Restricted ? [] : u.Permissions,
            settings.ApprovalThreshold, u.ApprovalLimit, u.ApprovalUnlimited, u.MustChangePassword, u.PreferredLanguage,
            u.MfaEnabled, u.MfaSetupRequired && !u.MustChangePassword);

    private static IResult Issue(HttpContext http, SessionResult s, ValidationSettings settings)
    {
        http.Response.Cookies.Append(RefreshCookie, s.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true, // browsers allow Secure cookies on http://localhost too
            SameSite = SameSiteMode.Strict,
            Path = "/api/auth",
            Expires = s.RefreshExpiresAt,
            IsEssential = true,
        });
        return TypedResults.Ok(new SessionResponse(s.AccessToken, s.AccessExpiresAt, ToMe(s.User, settings)));
    }

    private static Guid? UserId(ClaimsPrincipal user) => Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    private static IResult LocalOnly() =>
        TypedResults.Problem("Only available for signed-in accounts.", statusCode: StatusCodes.Status400BadRequest);

    private static IResult WrongCode() =>
        TypedResults.Problem("That code didn't work. Check the code in your app (it changes every 30 seconds) and try again.",
            statusCode: StatusCodes.Status400BadRequest);

    private static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/auth", Secure = true, SameSite = SameSiteMode.Strict });

    private static bool HasCsrfHeader(HttpContext http) => http.Request.Headers.ContainsKey(CsrfHeader);

    private static ClientInfo Client(HttpContext http) =>
        new(http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
}
