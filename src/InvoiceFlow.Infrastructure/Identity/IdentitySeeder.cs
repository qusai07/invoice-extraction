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

        var adminRole = await db.Roles.FirstAsync(r => r.Name == BuiltInRoles.SystemAdmin, ct).ConfigureAwait(false);
        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == adminRole.Id, ct).ConfigureAwait(false))
        {
            db.UserRoles.Add(new UserRoleRow { UserId = user.Id, RoleId = adminRole.Id });
        }

        db.SecurityEvents.Add(new SecurityEventRow
        {
            At = now, UserId = user.Id, Email = email, Event = "BootstrapAdmin", Actor = "system",
            Details = "First administrator created from configuration; password change required at first login.",
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        LogBootstrapped(logger, email);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No active administrator exists. Set Bootstrap:AdminEmail and Bootstrap:AdminPassword (user-secrets / environment) and restart.")]
    private static partial void LogNoAdmin(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap administrator {Email} created. They must change the password at first login; remove Bootstrap:AdminPassword from configuration afterwards.")]
    private static partial void LogBootstrapped(ILogger logger, string email);
}
