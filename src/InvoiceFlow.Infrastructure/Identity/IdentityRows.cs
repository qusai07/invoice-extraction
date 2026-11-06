using Microsoft.EntityFrameworkCore;

namespace InvoiceFlow.Infrastructure.Identity;

public sealed class UserRow
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";

    /// <summary>Upper-case email for unique, case-insensitive lookup.</summary>
    public string NormalizedEmail { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Null for SSO-only users.</summary>
    public string? PasswordHash { get; set; }

    public string? EntraObjectId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool MustChangePassword { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndUtc { get; set; }

    /// <summary>Changes on password change, deactivation, role change, logout-everywhere → old sessions stop working.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Highest invoice total this user may approve (AED). Null = none. Used from slice 3.</summary>
    public decimal? ApprovalLimit { get; set; }

    public bool ApprovalUnlimited { get; set; }
    public string PreferredLanguage { get; set; } = "en";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset? LastLoginAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class RoleRow
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Built-in role: can be edited, not deleted.</summary>
    public bool IsSystem { get; set; }

    public List<RolePermissionRow> Permissions { get; set; } = [];
}

public sealed class RolePermissionRow
{
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = "";
}

public sealed class UserRoleRow
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}

/// <summary>Only the SHA-256 of a refresh token is stored. A family is one sign-in; each refresh adds a row to it.</summary>
public sealed class RefreshTokenRow
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Hard end of the sign-in, whatever the activity.</summary>
    public DateTimeOffset FamilyExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public string? UserAgent { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>Sign-in and account security events (who, when, from where). Never updated.</summary>
public sealed class SecurityEventRow
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>The email that was typed (for failed attempts on unknown accounts too).</summary>
    public string? Email { get; set; }

    public string Event { get; set; } = "";
    public string? Actor { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? Details { get; set; }
}

internal static class IdentityModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<UserRow>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.NormalizedEmail).HasMaxLength(256);
            e.HasIndex(x => x.NormalizedEmail).IsUnique();
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.EntraObjectId).HasMaxLength(64);
            e.HasIndex(x => x.EntraObjectId).IsUnique().HasFilter("[EntraObjectId] IS NOT NULL");
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
            e.Property(x => x.ApprovalLimit).HasPrecision(18, 2);
            e.Property(x => x.PreferredLanguage).HasMaxLength(8);
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<RoleRow>(e =>
        {
            e.ToTable("Roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(p => p.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RolePermissionRow>(e =>
        {
            e.ToTable("RolePermissions");
            e.HasKey(x => new { x.RoleId, x.Permission });
            e.Property(x => x.Permission).HasMaxLength(64);
        });

        b.Entity<UserRoleRow>(e =>
        {
            e.ToTable("UserRoles");
            e.HasKey(x => new { x.UserId, x.RoleId });
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RoleRow>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshTokenRow>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsUnicode(false);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.RevokedAt });
            e.HasIndex(x => x.FamilyId);
            e.Property(x => x.SecurityStamp).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(400);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasOne<UserRow>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SecurityEventRow>(e =>
        {
            e.ToTable("SecurityEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.Event).HasMaxLength(64);
            e.Property(x => x.Actor).HasMaxLength(256);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(400);
            e.Property(x => x.Details).HasMaxLength(2000);
            e.HasIndex(x => new { x.UserId, x.At });
            e.HasIndex(x => x.At);
        });
    }
}
