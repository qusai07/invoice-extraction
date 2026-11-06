using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using InvoiceFlow.Application.Identity;
using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlow.Infrastructure.Identity;

public sealed record RoleRef(Guid Id, string Name);

public sealed record UserSummary(
    Guid Id, string Email, string DisplayName, bool IsActive, IReadOnlyList<RoleRef> Roles, decimal? ApprovalLimit, bool ApprovalUnlimited,
    DateTimeOffset? LastLoginAt, DateTimeOffset? LockedUntil, bool MustChangePassword, DateTimeOffset CreatedAt, bool HasPassword,
    bool MfaEnabled = false);

public sealed record SaveUser(string Email, string DisplayName, IReadOnlyList<Guid> RoleIds, decimal? ApprovalLimit, bool ApprovalUnlimited, bool IsActive = true);

public sealed record RoleSummary(Guid Id, string Name, string Description, bool IsSystem, IReadOnlyList<string> Permissions, int UserCount);

public sealed record SaveRole(string Name, string Description, IReadOnlyList<string> Permissions);

/// <summary>The admin's identity of record for audit entries.</summary>
/// <param name="ClaimsSuperAdmin">From the sign-in claims; only used when there is no user id (development sign-in).</param>
public sealed record Actor(Guid? Id, string Name, bool ClaimsSuperAdmin = false);

/// <summary>
/// User and role administration with guard rails (ADR-0009): never zero active administrators, no self-escalation,
/// every change audited with before → after.
/// </summary>
public sealed class UserAdminService(InvoiceFlowDbContext db, PermissionResolver permissions, TimeProvider time)
{
    private static readonly PasswordHasher<UserRow> Hasher = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ======================= users =======================

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct).ConfigureAwait(false);
        var links = await db.UserRoles.AsNoTracking().Join(db.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Id, r.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return users.Select(u => new UserSummary(
            u.Id, u.Email, u.DisplayName, u.IsActive,
            links.Where(l => l.UserId == u.Id).Select(l => new RoleRef(l.Id, l.Name)).OrderBy(r => r.Name, StringComparer.Ordinal).ToList(),
            u.ApprovalLimit, u.ApprovalUnlimited, u.LastLoginAt, u.LockoutEndUtc > now ? u.LockoutEndUtc : null,
            u.MustChangePassword, u.CreatedAt, u.PasswordHash is not null, u.MfaSecret is not null)).ToList();
    }

    /// <returns>The new user's id and a one-time temporary password (shown to the admin once, never stored in clear).</returns>
    public async Task<(Guid Id, string TemporaryPassword)> CreateUserAsync(SaveUser req, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var email = ValidEmail(req.Email);
        var name = Required(req.DisplayName, "Display name");
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == IdentityService.Normalize(email), ct).ConfigureAwait(false))
        {
            throw new WorkflowException(WorkflowErrorKind.Conflict, "A user with this email already exists.");
        }

        await EnsureRolesExistAsync(req.RoleIds, ct).ConfigureAwait(false);
        if (req.RoleIds.Contains(await SuperRoleIdAsync(ct).ConfigureAwait(false)))
        {
            await RequireSuperAsync(actor, ct).ConfigureAwait(false);
        }

        var temp = TemporaryPassword();
        var user = new UserRow
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = IdentityService.Normalize(email),
            DisplayName = name,
            IsActive = true,
            MustChangePassword = true,
            ApprovalLimit = req.ApprovalUnlimited ? null : req.ApprovalLimit,
            ApprovalUnlimited = req.ApprovalUnlimited,
            CreatedAt = time.GetUtcNow(),
            CreatedBy = actor.Name,
        };
        ValidateLimit(user.ApprovalLimit);
        user.PasswordHash = Hasher.HashPassword(user, temp);
        db.Users.Add(user);
        db.UserRoles.AddRange(req.RoleIds.Distinct().Select(r => new UserRoleRow { UserId = user.Id, RoleId = r }));
        Audit("UserCreated", user.Id, email, actor, new { after = await DescribeAsync(user, req.RoleIds, ct).ConfigureAwait(false) });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (user.Id, temp);
    }

    public async Task UpdateUserAsync(Guid id, SaveUser req, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
                   ?? throw new WorkflowException(WorkflowErrorKind.NotFound, "User not found.");
        var currentRoles = await db.UserRoles.Where(ur => ur.UserId == id).Select(ur => ur.RoleId).ToListAsync(ct).ConfigureAwait(false);
        var newRoles = req.RoleIds.Distinct().ToList();
        var newLimit = req.ApprovalUnlimited ? null : req.ApprovalLimit;
        ValidateLimit(newLimit);

        var rolesChanged = !currentRoles.ToHashSet().SetEquals(newRoles);
        var limitChanged = user.ApprovalLimit != newLimit || user.ApprovalUnlimited != req.ApprovalUnlimited;
        var activeChanged = user.IsActive != req.IsActive;
        if (actor.Id == id && (rolesChanged || limitChanged || activeChanged))
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden, "You can't change your own roles, approval limit or status. Ask another administrator.");
        }

        await EnsureRolesExistAsync(newRoles, ct).ConfigureAwait(false);
        var superId = await SuperRoleIdAsync(ct).ConfigureAwait(false);
        if (currentRoles.Contains(superId) || newRoles.Contains(superId))
        {
            await RequireSuperAsync(actor, ct).ConfigureAwait(false);
        }

        var before = await DescribeAsync(user, currentRoles, ct).ConfigureAwait(false);

        user.DisplayName = Required(req.DisplayName, "Display name");
        user.ApprovalLimit = newLimit;
        user.ApprovalUnlimited = req.ApprovalUnlimited;
        user.IsActive = req.IsActive;
        if (rolesChanged || activeChanged)
        {
            user.SecurityStamp = Guid.NewGuid().ToString("N"); // new rights apply now, not at next sign-in
            db.UserRoles.RemoveRange(db.UserRoles.Where(ur => ur.UserId == id));
            db.UserRoles.AddRange(newRoles.Select(r => new UserRoleRow { UserId = id, RoleId = r }));
        }

        if (!req.IsActive)
        {
            await RevokeSessionsAsync(id, ct).ConfigureAwait(false);
        }

        Audit("UserUpdated", id, user.Email, actor, new { before, after = await DescribeAsync(user, newRoles, ct).ConfigureAwait(false) });
        await SaveKeepingAnAdminAsync(ct).ConfigureAwait(false);
        permissions.Invalidate(id);
    }

    /// <summary>
    /// An administrator sets another user's password: the one they typed (checked against the password policy) or,
    /// when empty, a generated one. The user's sessions end, and a lockout is cleared. The password itself is never logged.
    /// </summary>
    /// <returns>The new password, so the administrator can pass it on.</returns>
    public async Task<string> ResetPasswordAsync(Guid id, Actor actor, CancellationToken ct, string? newPassword = null, bool mustChange = true)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Id == id)
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden, "To change your own password, use \"Change password\" in your account menu.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
                   ?? throw new WorkflowException(WorkflowErrorKind.NotFound, "User not found.");
        if (user.PasswordHash is null)
        {
            throw new WorkflowException(WorkflowErrorKind.BadRequest, "This user signs in with Microsoft only and has no password here.");
        }

        await RequireSuperIfTargetIsSuperAsync(id, actor, ct).ConfigureAwait(false);

        var chosen = !string.IsNullOrEmpty(newPassword);
        var password = chosen ? newPassword! : TemporaryPassword();
        if (chosen)
        {
            var problems = PasswordPolicy.Check(password, user.Email);
            if (problems.Count > 0)
            {
                throw new WorkflowException(WorkflowErrorKind.BadRequest, string.Join(" ", problems));
            }
        }

        user.PasswordHash = Hasher.HashPassword(user, password);
        user.MustChangePassword = mustChange;
        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await RevokeSessionsAsync(id, ct).ConfigureAwait(false);
        Audit("PasswordReset", id, user.Email, actor, new
        {
            note = chosen ? "password set by administrator" : "temporary password generated",
            mustChangeAtNextSignIn = mustChange,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        permissions.Invalidate(id);
        return password;
    }

    public async Task SignOutEverywhereAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
                   ?? throw new WorkflowException(WorkflowErrorKind.NotFound, "User not found.");
        await RequireSuperIfTargetIsSuperAsync(id, actor, ct).ConfigureAwait(false);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await RevokeSessionsAsync(id, ct).ConfigureAwait(false);
        Audit("SignedOutByAdmin", id, user.Email, actor, null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        permissions.Invalidate(id);
    }

    /// <summary>
    /// The user lost their phone and recovery codes: turn two-step sign-in off and end their sessions. If their role
    /// requires it, they are asked to set it up again right after their next sign-in.
    /// </summary>
    public async Task ResetMfaAsync(Guid id, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Id == id)
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden, "You can't reset your own two-step sign-in. Ask another administrator.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
                   ?? throw new WorkflowException(WorkflowErrorKind.NotFound, "User not found.");
        await RequireSuperIfTargetIsSuperAsync(id, actor, ct).ConfigureAwait(false);
        user.MfaSecret = null;
        user.MfaPendingSecret = null;
        user.MfaEnabledAt = null;
        user.MfaLastTimeStep = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.RecoveryCodes.Where(r => r.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await RevokeSessionsAsync(id, ct).ConfigureAwait(false);
        Audit("MfaReset", id, user.Email, actor, new { note = "two-step sign-in reset by administrator; sessions ended" });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        permissions.Invalidate(id);
    }

    public async Task UnlockAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false)
                   ?? throw new WorkflowException(WorkflowErrorKind.NotFound, "User not found.");
        user.LockoutEndUtc = null;
        user.FailedLoginCount = 0;
        Audit("Unlocked", id, user.Email, actor, null);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ======================= roles =======================

    public async Task<IReadOnlyList<RoleSummary>> ListRolesAsync(CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().Include(r => r.Permissions).OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name)
            .ToListAsync(ct).ConfigureAwait(false);
        var counts = await db.UserRoles.AsNoTracking().GroupBy(ur => ur.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        return roles.Select(r => new RoleSummary(r.Id, r.Name, r.Description, r.IsSystem,
            r.Permissions.Select(p => p.Permission).Order(StringComparer.Ordinal).ToList(), counts.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<Guid> CreateRoleAsync(SaveRole req, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var name = Required(req.Name, "Role name");
        if (await db.Roles.AnyAsync(r => r.Name == name, ct).ConfigureAwait(false))
        {
            throw new WorkflowException(WorkflowErrorKind.Conflict, "A role with this name already exists.");
        }

        var role = new RoleRow { Id = Guid.NewGuid(), Name = name, Description = req.Description?.Trim() ?? "", IsSystem = false };
        role.Permissions.AddRange(ValidPermissions(req.Permissions).Select(p => new RolePermissionRow { RoleId = role.Id, Permission = p }));
        db.Roles.Add(role);
        Audit("RoleCreated", null, null, actor, new { role = name, permissions = role.Permissions.Select(p => p.Permission) });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return role.Id;
    }

    public async Task UpdateRoleAsync(Guid id, SaveRole req, Actor actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false)
                   ?? throw new WorkflowException(WorkflowErrorKind.NotFound, "Role not found.");
        var name = Required(req.Name, "Role name");
        if (role.Name == BuiltInRoles.SuperAdmin && !role.Permissions.Select(p => p.Permission).ToHashSet().SetEquals(ValidPermissions(req.Permissions)))
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden, "Super admin always has every permission. It can't be changed.");
        }

        if (role.IsSystem && name != role.Name)
        {
            throw new WorkflowException(WorkflowErrorKind.BadRequest, "Built-in roles can't be renamed.");
        }

        if (name != role.Name && await db.Roles.AnyAsync(r => r.Name == name && r.Id != id, ct).ConfigureAwait(false))
        {
            throw new WorkflowException(WorkflowErrorKind.Conflict, "A role with this name already exists.");
        }

        var actorHasRole = actor.Id is { } aid && await db.UserRoles.AnyAsync(ur => ur.UserId == aid && ur.RoleId == id, ct).ConfigureAwait(false);
        var newPermissions = ValidPermissions(req.Permissions);
        var before = role.Permissions.Select(p => p.Permission).Order(StringComparer.Ordinal).ToList();
        if (actorHasRole && !before.ToHashSet().SetEquals(newPermissions))
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden, "You can't change the permissions of a role you have yourself. Ask another administrator.");
        }

        role.Name = name;
        role.Description = req.Description?.Trim() ?? "";
        role.Permissions.Clear();
        role.Permissions.AddRange(newPermissions.Select(p => new RolePermissionRow { RoleId = id, Permission = p }));
        Audit("RoleUpdated", null, null, actor, new { role = name, before, after = newPermissions });
        await SaveKeepingAnAdminAsync(ct).ConfigureAwait(false);
        permissions.InvalidateAll();
    }

    public async Task DeleteRoleAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false)
                   ?? throw new WorkflowException(WorkflowErrorKind.NotFound, "Role not found.");
        if (role.IsSystem)
        {
            throw new WorkflowException(WorkflowErrorKind.BadRequest, "Built-in roles can't be deleted. Remove their permissions instead.");
        }

        if (await db.UserRoles.AnyAsync(ur => ur.RoleId == id, ct).ConfigureAwait(false))
        {
            throw new WorkflowException(WorkflowErrorKind.Conflict, "This role is still assigned to users. Remove it from them first.");
        }

        db.Roles.Remove(role);
        Audit("RoleDeleted", null, null, actor, new { role = role.Name });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ======================= helpers =======================

    /// <summary>Saves in a transaction and rolls back if no active user would still hold admin.users.</summary>
    private async Task SaveKeepingAnAdminAsync(CancellationToken ct)
    {
        var superBefore = await ActiveSuperAdminsAsync(ct).ConfigureAwait(false);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var adminsLeft = await db.UserRoles
                .Join(db.Users.Where(u => u.IsActive), ur => ur.UserId, u => u.Id, (ur, _) => ur)
                .Join(db.RolePermissions.Where(p => p.Permission == Permissions.AdminUsers), ur => ur.RoleId, p => p.RoleId, (ur, _) => ur.UserId)
                .Distinct().CountAsync(ct).ConfigureAwait(false);
            if (adminsLeft == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                throw new WorkflowException(WorkflowErrorKind.Conflict,
                    "This change would leave no active administrator. Give someone else the 'Manage users' permission first.");
            }

            if (superBefore > 0 && await ActiveSuperAdminsAsync(ct).ConfigureAwait(false) == 0)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                throw new WorkflowException(WorkflowErrorKind.Conflict,
                    "This change would leave no active super admin. Make someone else super admin first.");
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private Task<int> ActiveSuperAdminsAsync(CancellationToken ct) =>
        db.UserRoles.Join(db.Roles.Where(r => r.Name == BuiltInRoles.SuperAdmin), ur => ur.RoleId, r => r.Id, (ur, _) => ur.UserId)
            .Join(db.Users.Where(u => u.IsActive), id => id, u => u.Id, (id, _) => id)
            .Distinct().CountAsync(ct);

    private async Task<Guid> SuperRoleIdAsync(CancellationToken ct) =>
        await db.Roles.Where(r => r.Name == BuiltInRoles.SuperAdmin).Select(r => r.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private async Task<bool> IsSuperAsync(Guid userId, CancellationToken ct)
    {
        var superId = await SuperRoleIdAsync(ct).ConfigureAwait(false);
        return await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == superId, ct).ConfigureAwait(false);
    }

    /// <summary>Only a super admin may grant/remove the role or manage a super admin's account (no takeover by a user-manager).</summary>
    private async Task RequireSuperAsync(Actor actor, CancellationToken ct)
    {
        var isSuper = actor.Id is { } id ? await IsSuperAsync(id, ct).ConfigureAwait(false) : actor.ClaimsSuperAdmin;
        if (!isSuper)
        {
            throw new WorkflowException(WorkflowErrorKind.Forbidden, "Only a super admin can manage super admin accounts or grant that role.");
        }
    }

    private async Task RequireSuperIfTargetIsSuperAsync(Guid targetId, Actor actor, CancellationToken ct)
    {
        if (await IsSuperAsync(targetId, ct).ConfigureAwait(false))
        {
            await RequireSuperAsync(actor, ct).ConfigureAwait(false);
        }
    }

    private Task RevokeSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private async Task EnsureRolesExistAsync(IReadOnlyList<Guid> roleIds, CancellationToken ct)
    {
        var distinct = roleIds.Distinct().ToList();
        var found = await db.Roles.CountAsync(r => distinct.Contains(r.Id), ct).ConfigureAwait(false);
        if (found != distinct.Count)
        {
            throw new WorkflowException(WorkflowErrorKind.BadRequest, "One of the selected roles doesn't exist.");
        }
    }

    private async Task<object> DescribeAsync(UserRow u, IEnumerable<Guid> roleIds, CancellationToken ct)
    {
        var ids = roleIds.ToList();
        var names = await db.Roles.AsNoTracking().Where(r => ids.Contains(r.Id)).Select(r => r.Name).OrderBy(n => n).ToListAsync(ct).ConfigureAwait(false);
        return new { u.DisplayName, u.IsActive, roles = names, approvalLimit = u.ApprovalUnlimited ? "unlimited" : u.ApprovalLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) };
    }

    private void Audit(string evt, Guid? userId, string? email, Actor actor, object? details) =>
        db.SecurityEvents.Add(new SecurityEventRow
        {
            At = time.GetUtcNow(),
            UserId = userId,
            Email = email,
            Event = evt,
            Actor = actor.Name,
            Details = details is null ? null : Truncate(JsonSerializer.Serialize(details, Json), 2000),
        });

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static IReadOnlyList<string> ValidPermissions(IEnumerable<string>? requested)
    {
        var list = (requested ?? []).Distinct(StringComparer.Ordinal).ToList();
        var unknown = list.Where(p => !Permissions.IsKnown(p)).ToList();
        if (unknown.Count > 0)
        {
            throw new WorkflowException(WorkflowErrorKind.BadRequest, $"Unknown permission(s): {string.Join(", ", unknown)}");
        }

        return list.Order(StringComparer.Ordinal).ToList();
    }

    private static void ValidateLimit(decimal? limit)
    {
        if (limit is < 0)
        {
            throw new WorkflowException(WorkflowErrorKind.BadRequest, "Approval limit can't be negative.");
        }
    }

    private static string Required(string? value, string what) =>
        string.IsNullOrWhiteSpace(value) ? throw new WorkflowException(WorkflowErrorKind.BadRequest, $"{what} is required.") : value.Trim();

    private static string ValidEmail(string? email)
    {
        var e = email?.Trim() ?? "";
        if (e.Length is 0 or > 256 || !MailAddress.TryCreate(e, out var parsed) || parsed.Address != e)
        {
            throw new WorkflowException(WorkflowErrorKind.BadRequest, "Enter a valid email address.");
        }

        return e;
    }

    /// <summary>16 random characters in 4 groups, no look-alikes (0/O, 1/l/I): easy to read out or type once.</summary>
    public static string TemporaryPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..12]}-{s[12..]}";
    }
}
