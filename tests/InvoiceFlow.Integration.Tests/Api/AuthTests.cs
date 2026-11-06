using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InvoiceFlow.Api;
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
    private readonly MovableTime _time = new(DateTimeOffset.UtcNow);

    public AuthTests(InvoiceFlowFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Mode", "Session");
            b.UseSetting("Auth:Session:SigningKey", Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i * 7 + 3)).ToArray()));
            b.UseSetting("Bootstrap:AdminEmail", AdminEmail);
            b.UseSetting("Bootstrap:AdminPassword", BootstrapPassword);
            b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(_time));
        });
    }

    public void Dispose() => _app.Dispose();

    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static Uri U(string path) => new(path, UriKind.Relative);

    // https so the Secure refresh cookie is stored and sent; cookies are handled per client.
    private HttpClient Browser() => _app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });

    private static async Task<(HttpStatusCode Status, AuthEndpoints.SessionResponse? Session)> LoginAsync(HttpClient c, string email, string password)
    {
        var r = await c.PostAsJsonAsync(U("/api/auth/login"), new { email, password }, Json);
        return (r.StatusCode, r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<AuthEndpoints.SessionResponse>(Json) : null);
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

    /// <summary>Signs in as the bootstrap admin, changing the password first if still required.</summary>
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

        return (c, session!.AccessToken);
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
        Assert.Contains("admin.users", fresh.User.Permissions);

        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(Bearer(HttpMethod.Get, "/api/invoices", fresh.AccessToken))).StatusCode);
        // The token from before the change is dead (security stamp changed).
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Bearer(HttpMethod.Get, "/api/me", session.AccessToken))).StatusCode);
    }

    [Fact]
    public async Task Unknown_account_and_wrong_password_look_the_same()
    {
        using var c = Browser();
        var unknown = await c.PostAsJsonAsync(U("/api/auth/login"), new { email = "nobody@example.com", password = "whatever-whatever" }, Json);
        var wrong = await c.PostAsJsonAsync(U("/api/auth/login"), new { email = AdminEmail, password = "wrong-password-123" }, Json);

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
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
