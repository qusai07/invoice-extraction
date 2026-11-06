using System.Security.Claims;
using System.Text.Encodings.Web;
using InvoiceFlow.Application.Identity;
using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Api.Security;

/// <summary>Endpoint policies. Each maps to one permission (ADR-0009); <see cref="Read"/> accepts own or all.</summary>
public static class Policies
{
    public const string Read = "invoices.read";
    public const string Submit = Permissions.InvoiceSubmit;
    public const string Correct = Permissions.InvoiceCorrect;
    public const string Reject = Permissions.InvoiceReject;
    public const string Override = Permissions.InvoiceOverride;
    public const string Approve = Permissions.InvoiceApprove;
    public const string RetryPosting = Permissions.InvoiceRetryPosting;
    public const string Audit = Permissions.AuditExport;

    // Read access for admin pages (a page often needs data owned by a neighbouring area).
    public const string AnyAdmin = "admin.any";
    public const string RolesRead = "roles.read";        // Users page lists roles to assign
    public const string SecurityLogRead = "securitylog.read";
    public const string RulesRead = "rules.read";        // auditors may read the rules in force
}

public static class ApprovalLimitClaim
{
    public const string Type = "apl";
    public const string Unlimited = "unlimited";
}

public static class AuthSetup
{
    public const string DevScheme = "Dev";
    public const string SessionMode = "Session";

    /// <summary>
    /// Auth:Mode = Session (default): InvoiceFlow's own sessions (POST /api/auth/login …); bearer access tokens are
    /// validated, then the user's current state (active, security stamp, permissions) is checked on every request.
    /// Auth:Mode = Dev: headers X-Dev-User / X-Dev-Roles (roles → permissions from the database). Development/Testing only.
    /// </summary>
    public static void AddInvoiceFlowAuth(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var mode = builder.Configuration["Auth:Mode"] ?? SessionMode;

        if (mode == DevScheme)
        {
            if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Auth:Mode=Dev is only allowed in the Development and Testing environments.");
            }

            builder.Services.AddAuthentication(DevScheme).AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevScheme, null);
        }
        else if (mode == SessionMode)
        {
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .Configure<TokenIssuer>((o, issuer) =>
                {
                    o.MapInboundClaims = false;
                    o.TokenValidationParameters = issuer.ValidationParameters();
                    o.Events = new JwtBearerEvents { OnTokenValidated = ValidateCurrentStateAsync };
                });
        }
        else
        {
            throw new InvalidOperationException($"Unknown Auth:Mode '{mode}'. Use Session (default) or Dev.");
        }

        var authz = builder.Services.AddAuthorizationBuilder()
            // Default deny: every endpoint needs a signed-in user unless marked AllowAnonymous.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Read, p => p.RequireAssertion(ctx =>
                ctx.User.HasClaim(SessionClaims.Permission, Permissions.InvoiceReadAll) ||
                ctx.User.HasClaim(SessionClaims.Permission, Permissions.InvoiceReadOwn)));
        foreach (var permission in Permissions.All)
        {
            authz.AddPolicy(permission, p => p.RequireClaim(SessionClaims.Permission, permission));
        }

        static Func<AuthorizationHandlerContext, bool> AnyOf(params string[] perms) =>
            ctx => perms.Any(p => ctx.User.HasClaim(SessionClaims.Permission, p));

        authz.AddPolicy(Policies.AnyAdmin, p => p.RequireAssertion(AnyOf(Permissions.AdminUsers, Permissions.AdminRoles,
            Permissions.AdminRules, Permissions.AdminSuppliers, Permissions.AdminSystem)));
        authz.AddPolicy(Policies.RolesRead, p => p.RequireAssertion(AnyOf(Permissions.AdminRoles, Permissions.AdminUsers)));
        authz.AddPolicy(Policies.SecurityLogRead, p => p.RequireAssertion(AnyOf(Permissions.AuditRead, Permissions.AdminUsers)));
        authz.AddPolicy(Policies.RulesRead, p => p.RequireAssertion(AnyOf(Permissions.AdminRules, Permissions.AuditRead)));
    }

    /// <summary>
    /// The token is valid; is the user still allowed in? Rejects deactivated users and tokens from before a password change,
    /// role change or logout-everywhere (security stamp). Adds the user's current permissions as claims.
    /// A user who must change their password gets no permissions: only /api/me, change-password and logout work.
    /// </summary>
    private static async Task ValidateCurrentStateAsync(TokenValidatedContext ctx)
    {
        var sub = ctx.Principal?.FindFirstValue("sub");
        var stamp = ctx.Principal?.FindFirstValue(SessionClaims.Stamp);
        if (!Guid.TryParse(sub, out var userId))
        {
            ctx.Fail("No subject.");
            return;
        }

        var resolver = ctx.HttpContext.RequestServices.GetRequiredService<PermissionResolver>();
        var user = await resolver.GetAsync(userId, ctx.HttpContext.RequestAborted);
        if (user is null || !user.IsActive || !string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal))
        {
            ctx.Fail("Session is no longer valid.");
            return;
        }

        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        if (!user.MustChangePassword)
        {
            identity.AddClaims(user.Permissions.Select(p => new Claim(SessionClaims.Permission, p)));
            var limit = user.ApprovalUnlimited ? ApprovalLimitClaim.Unlimited : user.ApprovalLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (limit is not null)
            {
                identity.AddClaim(new Claim(ApprovalLimitClaim.Type, limit));
            }
        }

        identity.AddClaims(user.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
    }

    public static UserContext ToUserContext(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var id = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.Identity?.Name
                 ?? throw new InvalidOperationException("Signed-in user has no id claim.");
        var name = principal.FindFirstValue("name") ?? principal.FindFirstValue("email") ?? id;
        var roles = principal.Claims.Where(c => c.Type is ClaimTypes.Role or "roles").Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var permissions = principal.Claims.Where(c => c.Type == SessionClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var limitClaim = principal.FindFirstValue(ApprovalLimitClaim.Type);
        decimal? limit = limitClaim == ApprovalLimitClaim.Unlimited ? decimal.MaxValue
            : decimal.TryParse(limitClaim, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var l) ? l : null;
        return new UserContext(id, name, roles) { Permissions = permissions, ApprovalLimit = limit };
    }

    /// <summary>Users who may only see their own uploads.</summary>
    public static bool SeesOnlyOwnDocuments(this UserContext user) => !user.Can(Permissions.InvoiceReadAll);
}

/// <summary>Development-only sign-in from headers: <c>X-Dev-User: alice</c>, <c>X-Dev-Roles: Clerk,Supervisor</c>.</summary>
public sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PermissionResolver resolver)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Dev-User"].ToString();
        if (string.IsNullOrWhiteSpace(user))
        {
            return AuthenticateResult.NoResult();
        }

        var roles = Request.Headers["X-Dev-Roles"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var permissions = await resolver.PermissionsOfRolesAsync(roles, Context.RequestAborted);

        // Development users approve without a limit (no user record to hold one).
        var claims = new List<Claim> { new("sub", user), new("name", user), new(ApprovalLimitClaim.Type, ApprovalLimitClaim.Unlimited) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(permissions.Select(p => new Claim(SessionClaims.Permission, p)));

        var identity = new ClaimsIdentity(claims, AuthSetup.DevScheme, "name", ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSetup.DevScheme));
    }
}
