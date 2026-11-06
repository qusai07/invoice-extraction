using InvoiceFlow.Application.Identity;
using InvoiceFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvoiceFlow.Infrastructure.Identity;

/// <summary>
/// Runs at API start (after migrations):
/// 1. creates missing built-in roles (never overwrites an admin's edits);
/// 2. if no active administrator exists, creates one from <c>Bootstrap:AdminEmail</c> / <c>Bootstrap:AdminPassword</c>
///    (user-secrets or environment only — never in a committed file). That account must change its password at first login.
/// </summary>
public static partial class IdentitySeeder
{
    public static async Task SeedAsync(InvoiceFlowDbContext db, IConfiguration configuration, TimeProvider time, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(configuration);

        var existing = await db.Roles.Select(r => r.Name).ToListAsync(ct).ConfigureAwait(false);
        foreach (var (name, (description, permissions)) in BuiltInRoles.Defaults)
        {
            if (existing.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            var role = new RoleRow { Id = Guid.NewGuid(), Name = name, Description = description, IsSystem = true };
            role.Permissions.AddRange(permissions.Select(p => new RolePermissionRow { RoleId = role.Id, Permission = p }));
            db.Roles.Add(role);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Super admin = every permission, always: add any that were introduced since the role was created.
        var super = await db.Roles.Include(r => r.Permissions).FirstAsync(r => r.Name == BuiltInRoles.SuperAdmin, ct).ConfigureAwait(false);
        var missing = Permissions.All.Except(super.Permissions.Select(p => p.Permission), StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            super.Permissions.AddRange(missing.Select(p => new RolePermissionRow { RoleId = super.Id, Permission = p }));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // The configured bootstrap admin is (or becomes) a super admin, also on systems seeded before the role existed.
        var bootstrapEmail = configuration["Bootstrap:AdminEmail"]?.Trim();
        if (!string.IsNullOrWhiteSpace(bootstrapEmail))
        {
            var key = IdentityService.Normalize(bootstrapEmail);
            var existingAdmin = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == key && u.IsActive, ct).ConfigureAwait(false);
            if (existingAdmin is not null && !await db.UserRoles.AnyAsync(ur => ur.UserId == existingAdmin.Id && ur.RoleId == super.Id, ct).ConfigureAwait(false))
            {
                GrantSuperAdmin(db, existingAdmin, super, time.GetUtcNow(), "Bootstrap administrator made super admin (Bootstrap:AdminEmail).");
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                LogSuperGranted(logger, existingAdmin.Email);
            }
        }

        var adminExists = await db.UserRoles
            .Join(db.Users.Where(u => u.IsActive), ur => ur.UserId, u => u.Id, (ur, _) => ur.RoleId)
            .Join(db.RolePermissions.Where(p => p.Permission == Permissions.AdminUsers), roleId => roleId, p => p.RoleId, (r, _) => r)
            .AnyAsync(ct).ConfigureAwait(false);
        if (adminExists)
        {
            return;
        }

        var email = configuration["Bootstrap:AdminEmail"]?.Trim();
        var password = configuration["Bootstrap:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
        {
            LogNoAdmin(logger);
            return;
        }

        var normalized = IdentityService.Normalize(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (user is null)
        {
            user = new UserRow
            {
                Id = Guid.NewGuid(),
                Email = email,
                NormalizedEmail = normalized,
                DisplayName = configuration["Bootstrap:AdminName"] ?? "Administrator",
                CreatedAt = now,
                CreatedBy = "bootstrap",
                ApprovalUnlimited = false,
            };
            db.Users.Add(user);
        }

        user.IsActive = true;
        user.PasswordHash = new PasswordHasher<UserRow>().HashPassword(user, password);
        user.MustChangePassword = true; // the bootstrap password lives in config; replace it at first login
        user.SecurityStamp = Guid.NewGuid().ToString("N");

        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == super.Id, ct).ConfigureAwait(false))
        {
            GrantSuperAdmin(db, user, super, now, null);
        }

        db.SecurityEvents.Add(new SecurityEventRow
        {
            At = now, UserId = user.Id, Email = email, Event = "BootstrapAdmin", Actor = "system",
            Details = "First administrator created from configuration as super admin; password change required at first login.",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        LogBootstrapped(logger, email);
    }

    private static void GrantSuperAdmin(InvoiceFlowDbContext db, UserRow user, RoleRow super, DateTimeOffset now, string? auditNote)
    {
        db.UserRoles.Add(new UserRoleRow { UserId = user.Id, RoleId = super.Id });
        user.ApprovalUnlimited = true; // all permissions includes approving any amount (four-eyes still applies)
        user.ApprovalLimit = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N"); // new rights apply at once
        if (auditNote is not null)
        {
            db.SecurityEvents.Add(new SecurityEventRow
            {
                At = now, UserId = user.Id, Email = user.Email, Event = "SuperAdminGranted", Actor = "system", Details = auditNote,
            });
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Email} was made super admin (it is the configured Bootstrap:AdminEmail).")]
    private static partial void LogSuperGranted(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No active administrator exists. Set Bootstrap:AdminEmail and Bootstrap:AdminPassword (user-secrets / environment) and restart.")]
    private static partial void LogNoAdmin(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap administrator {Email} created. They must change the password at first login; remove Bootstrap:AdminPassword from configuration afterwards.")]
    private static partial void LogBootstrapped(ILogger logger, string email);
}
