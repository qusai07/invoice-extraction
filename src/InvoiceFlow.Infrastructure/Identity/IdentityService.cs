using InvoiceFlow.Application.Identity;
using InvoiceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceFlow.Infrastructure.Identity;

public sealed record ClientInfo(string? IpAddress, string? UserAgent);

public sealed record SessionResult(
    string AccessToken, DateTimeOffset AccessExpiresAt, string RefreshToken, DateTimeOffset RefreshExpiresAt, CurrentUser User);

public sealed record CurrentUser(
    Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions,
    decimal? ApprovalLimit, bool ApprovalUnlimited, bool MustChangePassword, string PreferredLanguage, string SecurityStamp, bool IsActive);

public enum AuthError
{
    /// <summary>Wrong email or password, unknown or inactive account, or locked out: deliberately one answer.</summary>
    SignInFailed,

    /// <summary>Refresh token unknown, expired, revoked, or the account changed (stamp).</summary>
    SessionExpired,

    /// <summary>Two tabs refreshed at the same moment; the client should simply retry once.</summary>
    RefreshRace,

    WrongCurrentPassword,
    WeakPassword,
}

public sealed record AuthOutcome(SessionResult? Session, AuthError? Error, IReadOnlyList<string>? Problems = null)
{
    public static AuthOutcome Ok(SessionResult s) => new(s, null);

    public static AuthOutcome Fail(AuthError e, IReadOnlyList<string>? problems = null) => new(null, e, problems);
}

/// <summary>Local sign-in, sessions (rotating refresh tokens with reuse detection), logout, password change.</summary>
public sealed class IdentityService(
    InvoiceFlowDbContext db,
    TokenIssuer tokens,
    SessionOptions options,
    PermissionResolver permissions,
    TimeProvider time)
{
    // Same cost for unknown emails as for real ones: response time doesn't reveal whether an account exists.
    private static readonly string DummyHash = new PasswordHasher<UserRow>().HashPassword(new UserRow(), Guid.NewGuid().ToString());
    private static readonly PasswordHasher<UserRow> Hasher = new();
    private static readonly TimeSpan RaceWindow = TimeSpan.FromSeconds(10);

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public async Task<AuthOutcome> LoginAsync(string email, string password, ClientInfo client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = time.GetUtcNow();
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == Normalize(email ?? ""), ct).ConfigureAwait(false);

        if (user?.PasswordHash is null)
        {
            Hasher.VerifyHashedPassword(new UserRow(), DummyHash, password ?? "");
            await LogAsync("LoginFailed", user?.Id, email, client, "unknown account or no local password", ct).ConfigureAwait(false);
            return AuthOutcome.Fail(AuthError.SignInFailed);
        }

        if (user.LockoutEndUtc > now)
        {
            Hasher.VerifyHashedPassword(user, DummyHash, password ?? "");
            await LogAsync("LoginFailed", user.Id, email, client, "locked out", ct).ConfigureAwait(false);
            return AuthOutcome.Fail(AuthError.SignInFailed);
        }

        var verdict = Hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "");
        if (verdict == PasswordVerificationResult.Failed || !user.IsActive)
        {
            if (verdict == PasswordVerificationResult.Failed)
            {
                user.FailedLoginCount++;
                if (user.FailedLoginCount >= options.MaxFailedLogins)
                {
                    user.LockoutEndUtc = now + options.LockoutDuration;
                    user.FailedLoginCount = 0;
                    await LogAsync("LockedOut", user.Id, email, client, $"{options.MaxFailedLogins} failed attempts", ct, save: false).ConfigureAwait(false);
                }
            }

            await LogAsync("LoginFailed", user.Id, email, client, user.IsActive ? "wrong password" : "inactive account", ct).ConfigureAwait(false);
            return AuthOutcome.Fail(AuthError.SignInFailed);
        }

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = Hasher.HashPassword(user, password!);
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginAt = now;
        var session = await StartSessionAsync(user, Guid.NewGuid(), now + options.AbsoluteLifetime, client, ct).ConfigureAwait(false);
        await LogAsync("LoginSucceeded", user.Id, user.Email, client, null, ct).ConfigureAwait(false);
        return AuthOutcome.Ok(session);
    }

    /// <summary>Rotates the refresh token. A token used twice means it was stolen: the whole sign-in is ended.</summary>
    public async Task<AuthOutcome> RefreshAsync(string refreshToken, ClientInfo client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var now = time.GetUtcNow();
        var hash = TokenIssuer.Hash(refreshToken ?? "");
        var row = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct).ConfigureAwait(false);
        if (row is null)
        {
            return AuthOutcome.Fail(AuthError.SessionExpired);
        }

        if (row.RevokedAt is { } revokedAt)
        {
            if (row.ReplacedById is not null && now - revokedAt < RaceWindow)
            {
                return AuthOutcome.Fail(AuthError.RefreshRace); // another tab just rotated it
            }

            await RevokeFamilyAsync(row.FamilyId, now, ct).ConfigureAwait(false);
            await LogAsync("RefreshTokenReuse", row.UserId, null, client, "revoked token presented again; sign-in ended", ct).ConfigureAwait(false);
            return AuthOutcome.Fail(AuthError.SessionExpired);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == row.UserId, ct).ConfigureAwait(false);
        if (row.ExpiresAt <= now || row.FamilyExpiresAt <= now || user is null || !user.IsActive || user.SecurityStamp != row.SecurityStamp)
        {
            row.RevokedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return AuthOutcome.Fail(AuthError.SessionExpired);
        }

        var session = await StartSessionAsync(user, row.FamilyId, row.FamilyExpiresAt, client, ct, replacing: row).ConfigureAwait(false);
        return AuthOutcome.Ok(session);
    }

    public async Task LogoutAsync(string? refreshToken, ClientInfo client, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        var hash = TokenIssuer.Hash(refreshToken);
        var row = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct).ConfigureAwait(false);
        if (row is not null)
        {
            await RevokeFamilyAsync(row.FamilyId, time.GetUtcNow(), ct).ConfigureAwait(false);
            await LogAsync("Logout", row.UserId, null, client, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Ends every session of the user (all devices), including access tokens via the stamp.</summary>
    public async Task LogoutEverywhereAsync(Guid userId, string actor, ClientInfo client, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return;
        }

        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await RevokeAllAsync(userId, time.GetUtcNow(), ct).ConfigureAwait(false);
        permissions.Invalidate(userId);
        await LogAsync("LogoutEverywhere", userId, user.Email, client, null, ct, actor: actor).ConfigureAwait(false);
    }

    public async Task<AuthOutcome> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, ClientInfo client, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct).ConfigureAwait(false);
        if (user?.PasswordHash is null ||
            Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed)
        {
            await LogAsync("PasswordChangeFailed", userId, user?.Email, client, "wrong current password", ct).ConfigureAwait(false);
            return AuthOutcome.Fail(AuthError.WrongCurrentPassword);
        }

        var problems = PasswordPolicy.Check(newPassword, user.Email).ToList();
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
        {
            problems.Add("The new password must be different from the current one.");
        }

        if (problems.Count > 0)
        {
            return AuthOutcome.Fail(AuthError.WeakPassword, problems);
        }

        var now = time.GetUtcNow();
        user.PasswordHash = Hasher.HashPassword(user, newPassword);
        user.MustChangePassword = false;
        user.SecurityStamp = Guid.NewGuid().ToString("N"); // other devices are signed out
        await RevokeAllAsync(userId, now, ct).ConfigureAwait(false);
        permissions.Invalidate(userId);

        var session = await StartSessionAsync(user, Guid.NewGuid(), now + options.AbsoluteLifetime, client, ct).ConfigureAwait(false);
        await LogAsync("PasswordChanged", userId, user.Email, client, null, ct).ConfigureAwait(false);
        return AuthOutcome.Ok(session);
    }

    // ---------- internals ----------

    private async Task<SessionResult> StartSessionAsync(
        UserRow user, Guid familyId, DateTimeOffset familyExpires, ClientInfo client, CancellationToken ct, RefreshTokenRow? replacing = null)
    {
        var now = time.GetUtcNow();
        var refresh = TokenIssuer.NewRefreshToken();
        var expires = Min(now + options.IdleTimeout, familyExpires);
        var row = new RefreshTokenRow
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = TokenIssuer.Hash(refresh),
            SecurityStamp = user.SecurityStamp,
            CreatedAt = now,
            ExpiresAt = expires,
            FamilyExpiresAt = familyExpires,
            UserAgent = Truncate(client.UserAgent, 400),
            IpAddress = Truncate(client.IpAddress, 64),
        };
        db.RefreshTokens.Add(row);
        if (replacing is not null)
        {
            replacing.RevokedAt = now;
            replacing.ReplacedById = row.Id;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var (access, accessExpires) = tokens.CreateAccessToken(user);
        var current = await permissions.GetAsync(user.Id, ct).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("User vanished during sign-in.");
        return new SessionResult(access, accessExpires, refresh, expires, current);
    }

    private Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private Task RevokeAllAsync(Guid userId, DateTimeOffset now, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private async Task LogAsync(string evt, Guid? userId, string? email, ClientInfo client, string? details, CancellationToken ct, bool save = true, string? actor = null)
    {
        db.SecurityEvents.Add(new SecurityEventRow
        {
            At = time.GetUtcNow(),
            UserId = userId,
            Email = Truncate(email, 256),
            Event = evt,
            Actor = actor,
            IpAddress = Truncate(client.IpAddress, 64),
            UserAgent = Truncate(client.UserAgent, 400),
            Details = details,
        });
        if (save)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

/// <summary>
/// Who a user is right now: active?, stamp, roles, permissions. Cached for a few seconds so every API call can check it
/// cheaply: deactivation, role changes and logout-everywhere take effect within <see cref="CacheFor"/>.
/// </summary>
public sealed class PermissionResolver(IServiceScopeFactory scopes, IMemoryCache cache)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    public async Task<CurrentUser?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(userId), out CurrentUser? cached))
        {
            return cached;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var roles = await db.UserRoles.AsNoTracking().Where(ur => ur.UserId == userId)
            .Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r)
            .Select(r => new { r.Name, Permissions = r.Permissions.Select(p => p.Permission) })
            .ToListAsync(ct).ConfigureAwait(false);

        var current = new CurrentUser(
            user.Id, user.Email, user.DisplayName,
            roles.Select(r => r.Name).Order(StringComparer.Ordinal).ToList(),
            roles.SelectMany(r => r.Permissions).Distinct().Order(StringComparer.Ordinal).ToList(),
            user.ApprovalLimit, user.ApprovalUnlimited, user.MustChangePassword, user.PreferredLanguage, user.SecurityStamp, user.IsActive);

        cache.Set(Key(userId), current, CacheFor);
        return current;
    }

    /// <summary>Permissions of named roles (development sign-in, where roles come from headers).</summary>
    public async Task<IReadOnlyList<string>> PermissionsOfRolesAsync(IReadOnlyCollection<string> roleNames, CancellationToken ct = default)
    {
        var key = $"roles:{_generation}:" + string.Join(',', roleNames.Order(StringComparer.Ordinal));
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>();
        IReadOnlyList<string> perms = await db.Roles.AsNoTracking().Where(r => roleNames.Contains(r.Name))
            .SelectMany(r => r.Permissions.Select(p => p.Permission)).Distinct().ToListAsync(ct).ConfigureAwait(false);
        cache.Set(key, perms, CacheFor);
        return perms;
    }

    public void Invalidate(Guid userId) => cache.Remove(Key(userId));

    /// <summary>A role's permissions changed: every cached user is stale.</summary>
    public void InvalidateAll() => Interlocked.Increment(ref _generation);

    private static int _generation;

    private static string Key(Guid userId) => $"user:{_generation}:{userId:N}";
}
