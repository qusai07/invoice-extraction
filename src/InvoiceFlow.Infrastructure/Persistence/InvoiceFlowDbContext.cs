using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Domain.Invoices;
using Microsoft.EntityFrameworkCore;

namespace InvoiceFlow.Infrastructure.Persistence;

public sealed class InvoiceFlowDbContext(DbContextOptions<InvoiceFlowDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRow> Documents => Set<DocumentRow>();
    public DbSet<CorrectionRow> Corrections => Set<CorrectionRow>();
    public DbSet<AuditRow> AuditEvents => Set<AuditRow>();
    public DbSet<JobRow> PipelineJobs => Set<JobRow>();
    public DbSet<Identity.UserRow> Users => Set<Identity.UserRow>();
    public DbSet<Identity.RecoveryCodeRow> RecoveryCodes => Set<Identity.RecoveryCodeRow>();
    public DbSet<Identity.RoleRow> Roles => Set<Identity.RoleRow>();
    public DbSet<Identity.RolePermissionRow> RolePermissions => Set<Identity.RolePermissionRow>();
    public DbSet<Identity.UserRoleRow> UserRoles => Set<Identity.UserRoleRow>();
    public DbSet<Identity.RefreshTokenRow> RefreshTokens => Set<Identity.RefreshTokenRow>();
    public DbSet<Identity.SecurityEventRow> SecurityEvents => Set<Identity.SecurityEventRow>();
    public DbSet<Settings.SettingsVersionRow> SettingsVersions => Set<Settings.SettingsVersionRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        Identity.IdentityModel.Configure(modelBuilder);
        modelBuilder.Entity<Settings.SettingsVersionRow>(e =>
        {
            e.ToTable("ValidationSettingsVersions");
            e.HasKey(x => x.Version);
            e.Property(x => x.Version).ValueGeneratedNever();
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.Reason).HasMaxLength(1000);
        });

        modelBuilder.Entity<DocumentRow>(e =>
        {
            e.ToTable("Documents");
            e.HasKey(x => x.Id);
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false);
            e.Property(x => x.StoragePath).HasMaxLength(400);
            e.Property(x => x.Source).HasMaxLength(32);
            e.Property(x => x.SubmittedBy).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.SupplierId).HasMaxLength(64);
            e.Property(x => x.SupplierName).HasMaxLength(300);
            e.Property(x => x.InvoiceNumber).HasMaxLength(100);
            e.Property(x => x.InvoiceNumberKey).HasMaxLength(100);
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
            e.Property(x => x.Total).HasPrecision(18, 4);
            e.Property(x => x.PoNumber).HasMaxLength(100);
            e.Property(x => x.ResolvedBy).HasMaxLength(200);
            e.Property(x => x.ApprovedBy).HasMaxLength(200);
            e.Property(x => x.ErpReference).HasMaxLength(100);
            e.Property(x => x.ErrorCodes).HasMaxLength(400);
            e.Property(x => x.RowVersion).IsRowVersion();

            e.HasIndex(x => x.Sha256);
            e.HasIndex(x => new { x.SupplierId, x.InvoiceNumberKey });
            e.HasIndex(x => new { x.Status, x.StatusChangedAt });
            e.HasIndex(x => x.SubmittedBy);
        });

        modelBuilder.Entity<CorrectionRow>(e =>
        {
            e.ToTable("Corrections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Field).HasMaxLength(200);
            e.Property(x => x.UserId).HasMaxLength(200);
            e.HasIndex(x => x.DocumentId);
        });

        modelBuilder.Entity<AuditRow>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Actor).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.DocumentId, x.Id });
        });

        modelBuilder.Entity<JobRow>(e =>
        {
            e.ToTable("PipelineJobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Step).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(16).IsUnicode(false);
            e.Property(x => x.LockedBy).HasMaxLength(200);
            e.HasIndex(x => new { x.Status, x.NotBefore });
            e.HasIndex(x => x.DocumentId);
        });
    }
}

public sealed class DocumentRow
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "default";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string Source { get; set; } = "";
    public string SubmittedBy { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public InvoiceStatus Status { get; set; }
    public DateTimeOffset StatusChangedAt { get; set; }
    public string? SupplierId { get; set; }
    public string? SupplierName { get; set; }
    public string? InvoiceNumber { get; set; }

    /// <summary>Upper-case letters/digits only, for duplicate search ("INV-001" == "inv 001").</summary>
    public string? InvoiceNumberKey { get; set; }

    public DateOnly? InvoiceDate { get; set; }
    public string? Currency { get; set; }
    public decimal? Total { get; set; }
    public string? PoNumber { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ApprovedBy { get; set; }
    public string? ErpReference { get; set; }

    /// <summary>Comma-separated error rule codes of the latest validation, for the review queue list.</summary>
    public string? ErrorCodes { get; set; }

    public string? InvoiceJson { get; set; }
    public string? ExtractionJson { get; set; }
    public string? ValidationJson { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class CorrectionRow
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public string Field { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string UserId { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

public sealed class AuditRow
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public DateTimeOffset At { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public InvoiceStatus? FromStatus { get; set; }
    public InvoiceStatus? ToStatus { get; set; }
    public string? Details { get; set; }
}

public sealed class JobRow
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Done = "Done";
    public const string Failed = "Failed";

    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public PipelineStep Step { get; set; }
    public string Status { get; set; } = Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
