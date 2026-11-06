using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InvoiceFlow.Api;
using InvoiceFlow.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static InvoiceFlow.Integration.Tests.InvoiceFlowFactory;

namespace InvoiceFlow.Integration.Tests.Api;

/// <summary>Real sign-in (Auth:Mode=Session) against SQL Server: bootstrap admin, lockout, rotation, revocation (ADR-0009).</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthTests : IDisposable
{
    private const string AdminEmail = "admin.tests@example.com";
    private const string BootstrapPassword = "Qqq1234$$";
    private const string StrongPassword = "lantern orbit mango 2026";

    private readonly WebApplicationFactory<Program> _app;

    // Shared by all tests in the run (one database): the admin's state, incl. the last used code step, outlives a test.
    private static readonly MovableTime _time = new(DateTimeOffset.UtcNow);
    private static byte[]? _mfaSecret;

    public AuthTests(InvoiceFlowFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Mode", "Session");
            b.UseSetting("Auth:Session:SigningKey", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i * 7 + 3)).ToArray()));
            b.UseSetting("Auth:Session:MfaEncryptionKey", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i * 11 + 5)).ToArray()));
            b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
            b.UseSetting("Bootstrap:AdminPassword", BootstrapPassword);
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(_time));
        });
    }

    public void Dispose() => _app.Dispose();

    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        private readonly Lock _lock = new();

        public void Advance(TimeSpan by)
        {
            lock (_lock)
            {
                _now += by;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _now;
            }
        }
    }

    private sealed record CodeStep(bool CodeRequired, string Challenge);

    private static Uri U(string path) => new(path, UriKind.Relative);

    // https so the Secure refresh cookie is stored and sent; cookies are handled per client.
    private HttpClient Browser() => _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });

    /// <summary>A fresh code from the "phone": the clock moves one step on, as a code works only once.</summary>
    private static string NextCode()
    {
        _time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        return Totp.Code(_mfaSecret!, Totp.StepAt(_time.GetUtcNow()));
    }

    /// <summary>Password step, then (when the account uses two-step sign-in) the code step with a valid code.</summary>
    private static async Task<(HttpStatusCode Status, AuthEndpoints.SessionResponse? Session)> LoginAsync(HttpClient c, string email, string password)
    {
        var r = await c.PostAsJsonAsync(U("/api/auth/login"), new { email, password }, Json);
        if (!r.IsSuccessStatusCode)
        {
            return (r.StatusCode, null);
        }

        var body = await r.Content.ReadAsStringAsync();
        if (body.Contains("\"codeRequired\":true", StringComparison.Ordinal))
        {
            var step = System.Text.Json.JsonSerializer.Deserialize<CodeStep>(body, Json)!;
            r = await c.PostAsJsonAsync(U("/api/auth/login/code"), new { challenge = step.Challenge, code = NextCode() }, Json);
            if (!r.IsSuccessStatusCode)
            {
                return (r.StatusCode, null);
            }

            body = await r.Content.ReadAsStringAsync();
        }

        return (HttpStatusCode.OK, System.Text.Json.JsonSerializer.Deserialize<AuthEndpoints.SessionResponse>(body, Json));
    }

    /// <summary>Required for the admin: set up the authenticator app (QR secret → first code). Returns the new session's token.</summary>
    private static async Task<(string Token, IReadOnlyList<string> RecoveryCodes)> SetUpMfaAsync(HttpClient c, string token)
    {
        var setup = Bearer(HttpMethod.Post, "/api/auth/mfa/setup", token);
        var r = await c.SendAsync(setup);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadFromJsonAsync<IdentityService.MfaSetup>(Json);
        _mfaSecret = Base32.Decode(s!.Secret);

        var enable = Bearer(HttpMethod.Post, "/api/auth/mfa/enable", token);
        enable.Content = JsonContent.Create(new { code = NextCode() });
        r = await c.SendAsync(enable);
        r.EnsureSuccessStatusCode();
        var codes = await r.Content.ReadFromJsonAsync<AuthEndpoints.RecoveryCodesResponse>(Json);
        return (token, codes!.RecoveryCodes);
    }

    private static HttpRequestMessage Csrf(HttpMethod method, string path)
    {
        var m = new HttpRequestMessage(method, U(path));
        m.Headers.Add(AuthEndpoints.CsrfHeader, "1");
        return m;
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string path, string token)
    {
        var m = new HttpRequestMessage(method, U(path));
        m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return m;
    }

    /// <summary>
    /// Signs in as the bootstrap admin (super admin), first changing the bootstrap password and setting up two-step
    /// sign-in if that hasn't happened yet in this run.
    /// </summary>
    private async Task<(HttpClient Client, string Token)> AdminAsync()
    {
        var c = Browser();
        var (status, session) = await LoginAsync(c, AdminEmail, StrongPassword);
        if (status != HttpStatusCode.OK)
        {
            (_, session) = await LoginAsync(c, AdminEmail, BootstrapPassword);
            var change = Bearer(HttpMethod.Post, "/api/auth/change-password", session!.AccessToken);
            change.Content = JsonContent.Create(new { currentPassword = BootstrapPassword, newPassword = StrongPassword });
            var r = await c.SendAsync(change);
            r.EnsureSuccessStatusCode();
            session = await r.Content.ReadFromJsonAsync<AuthEndpoints.SessionResponse>(Json);
        }

        if (session!.User.MfaSetupRequired)
        {
            await SetUpMfaAsync(c, session.AccessToken);
        }

        return (c, session.AccessToken);
    }

    [Fact]
    public async Task Bootstrap_admin_must_change_password_before_doing_anything()
    {
        using var c = Browser();
        var (status, session) = await LoginAsync(c, AdminEmail, BootstrapPassword);
        if (status != HttpStatusCode.OK)
        {
            return; // another test in this class already replaced the bootstrap password
        }

        Assert.True(session!.User.MustChangePassword);
        Assert.Empty(session.User.Permissions);

        // No permissions until the password is changed.
        var list = await c.SendAsync(Bearer(HttpMethod.Get, "/api/invoices", session.AccessToken));
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

        // Weak new password is refused with reasons.
        var weak = Bearer(HttpMethod.Post, "/api/auth/change-password", session.AccessToken);
        weak.Content = JsonContent.Create(new { currentPassword = BootstrapPassword, newPassword = "Qqq12345$$$" });
        Assert.Equal(HttpStatusCode.BadRequest, (await c.SendAsync(weak)).StatusCode);

        var ok = Bearer(HttpMethod.Post, "/api/auth/change-password", session.AccessToken);
        ok.Content = JsonContent.Create(new { currentPassword = BootstrapPassword, newPassword = StrongPassword });
        var changed = await c.SendAsync(ok);
        changed.EnsureSuccessStatusCode();
        var fresh = await changed.Content.ReadFromJsonAsync<AuthEndpoints.SessionResponse>(Json);
        Assert.False(fresh!.User.MustChangePassword);

        // Next: two-step sign-in is required for an admin. Still no permissions until it is on.
        Assert.True(fresh.User.MfaSetupRequired);
        Assert.Empty(fresh.User.Permissions);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Bearer(HttpMethod.Get, "/api/invoices", fresh.AccessToken))).StatusCode);
        await SetUpMfaAsync(c, fresh.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Bearer(HttpMethod.Get, "/api/invoices", fresh.AccessToken))).StatusCode);
        var me = await (await c.SendAsync(Bearer(HttpMethod.Get, "/api/me", fresh.AccessToken))).Content.ReadFromJsonAsync<AuthEndpoints.MeResponse>(Json);
        Assert.True(me!.MfaEnabled);
        Assert.Contains("admin.users", me.Permissions);

        // The token from before the change is dead (security stamp changed).
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Bearer(HttpMethod.Get, "/api/me", session.AccessToken))).StatusCode);
    }

    [Fact]
    public async Task Two_step_sign_in_needs_a_valid_unused_code()
    {
        var (admin, _) = await AdminAsync();
        admin.Dispose();
        using var c = Browser();

        // Password alone gives no session, only a short-lived challenge that is useless as an access token.
        var first = await c.PostAsJsonAsync(U("/api/auth/login"), new { email = AdminEmail, password = StrongPassword }, Json);
        var step = await first.Content.ReadFromJsonAsync<CodeStep>(Json);
        Assert.True(step!.CodeRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Bearer(HttpMethod.Get, "/api/me", step.Challenge))).StatusCode);

        // Wrong code → refused; the right code → session; the same code again → refused (single use).
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.PostAsJsonAsync(U("/api/auth/login/code"), new { challenge = step.Challenge, code = "000000" }, Json)).StatusCode);
        var code = NextCode();
        Assert.Equal(HttpStatusCode.OK,
            (await c.PostAsJsonAsync(U("/api/auth/login/code"), new { challenge = step.Challenge, code }, Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.PostAsJsonAsync(U("/api/auth/login/code"), new { challenge = step.Challenge, code }, Json)).StatusCode);

        // The challenge expires after a few minutes.
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await c.PostAsJsonAsync(U("/api/auth/login/code"), new { challenge = step.Challenge, code = NextCode() }, Json)).StatusCode);
    }

    [Fact]
    public async Task Required_two_step_sign_in_cannot_be_turned_off()
    {
        var (c, token) = await AdminAsync();
        using (c)
        {
            var off = Bearer(HttpMethod.Post, "/api/auth/mfa/disable", token);
            off.Content = JsonContent.Create(new { password = StrongPassword, code = NextCode() });
            Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(off)).StatusCode);
        }
    }

    [Fact]
    public async Task Unknown_account_and_wrong_password_look_the_same()
    {
        using var c = Browser();
        var unknown = await c.PostAsJsonAsync(U("/api/auth/login"), new { email = "nobody@example.com", password = "whatever-whatever" }, Json);
        var wrong = await c.PostAsJsonAsync(U("/api/auth/login"), new { email = AdminEmail, password = "wrong-password-123" }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        // Same message (only the per-request trace id differs): the answer doesn't reveal whether the account exists.
        var a = await unknown.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(Json);
        var b = await wrong.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(Json);
        Assert.Equal((a!.Title, a.Detail, a.Status), (b!.Title, b.Detail, b.Status));
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var (_, _) = await AdminAsync(); // make sure the admin exists with StrongPassword
        using var c = Browser();
        for (var i = 0; i < 5; i++)
        {
            await c.PostAsJsonAsync(U("/api/auth/login"), new { email = AdminEmail, password = $"wrong-{i}-password" }, Json);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(c, AdminEmail, StrongPassword)).Status);

        _time.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(c, AdminEmail, StrongPassword)).Status);
    }

    [Fact]
    public async Task Refresh_rotates_and_a_reused_token_ends_the_whole_sign_in()
    {
        var (c, _) = await AdminAsync();
        using (c)
        {
            var first = await c.SendAsync(Csrf(HttpMethod.Post, "/api/auth/refresh"));
            first.EnsureSuccessStatusCode();
            var stolen = first.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith(AuthEndpoints.RefreshCookie, StringComparison.Ordinal)).Split(';')[0];

            // Legitimate client refreshes again → the cookie value 'stolen' is now an old, rotated token.
            (await c.SendAsync(Csrf(HttpMethod.Post, "/api/auth/refresh"))).EnsureSuccessStatusCode();
            _time.Advance(TimeSpan.FromMinutes(1)); // outside the 10 s multi-tab grace window

            // Attacker replays the old token.
            using var attacker = Browser();
            var replay = Csrf(HttpMethod.Post, "/api/auth/refresh");
            replay.Headers.Add("Cookie", stolen);
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.SendAsync(replay)).StatusCode);

            // …and the legitimate session is ended too (theft detected).
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Csrf(HttpMethod.Post, "/api/auth/refresh"))).StatusCode);
        }
    }

    [Fact]
    public async Task Refresh_requires_the_csrf_header()
    {
        var (c, _) = await AdminAsync();
        using (c)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostAsync(U("/api/auth/refresh"), null)).StatusCode);
        }
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var (c, _) = await AdminAsync();
        using (c)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Csrf(HttpMethod.Post, "/api/auth/logout"))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Csrf(HttpMethod.Post, "/api/auth/refresh"))).StatusCode);
        }
    }

    [Fact]
    public async Task Logout_everywhere_kills_existing_access_tokens_immediately()
    {
        var (laptop, laptopToken) = await AdminAsync();
        var (phone, phoneToken) = await AdminAsync();
        using (laptop)
        using (phone)
        {
            Assert.Equal(HttpStatusCode.OK, (await phone.SendAsync(Bearer(HttpMethod.Get, "/api/me", phoneToken))).StatusCode);

            var all = Bearer(HttpMethod.Post, "/api/auth/logout-everywhere", laptopToken);
            Assert.Equal(HttpStatusCode.NoContent, (await laptop.SendAsync(all)).StatusCode);

            // The phone's access token is still unexpired, but its security stamp is old → rejected now, not in 15 minutes.
            Assert.Equal(HttpStatusCode.Unauthorized, (await phone.SendAsync(Bearer(HttpMethod.Get, "/api/me", phoneToken))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await phone.SendAsync(Csrf(HttpMethod.Post, "/api/auth/refresh"))).StatusCode);
        }
    }

    [Fact]
    public async Task Bearer_token_with_a_wrong_signature_is_rejected()
    {
        var (c, token) = await AdminAsync();
        using (c)
        {
            var tampered = token[..^4] + (token[^4] == 'A' ? "BBBB" : "AAAA");
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Bearer(HttpMethod.Get, "/api/me", tampered))).StatusCode);
        }
    }
}
