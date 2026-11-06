namespace InvoiceFlow.Application.Identity;

/// <summary>
/// Everything a user can be allowed to do. The API authorizes by these (ADR-0009); roles are named sets of them.
/// Codes are stored in the database: never rename one, only add new ones.
/// </summary>
public static class Permissions
{
    public const string InvoiceReadOwn = "invoice.read.own";
    public const string InvoiceReadAll = "invoice.read.all";
    public const string InvoiceSubmit = "invoice.submit";
    public const string InvoiceCorrect = "invoice.correct";
    public const string InvoiceReject = "invoice.reject";
    public const string InvoiceOverride = "invoice.override";
    public const string InvoiceApprove = "invoice.approve";
    public const string InvoiceRetryPosting = "invoice.retry-posting";
    public const string AuditRead = "audit.read";
    public const string AuditExport = "audit.export";
    public const string AdminUsers = "admin.users";
    public const string AdminRoles = "admin.roles";
    public const string AdminRules = "admin.rules";
    public const string AdminSuppliers = "admin.suppliers";
    public const string AdminSystem = "admin.system";

    public static readonly IReadOnlyList<string> All =
    [
        InvoiceReadOwn, InvoiceReadAll, InvoiceSubmit, InvoiceCorrect, InvoiceReject, InvoiceOverride, InvoiceApprove,
        InvoiceRetryPosting, AuditRead, AuditExport, AdminUsers, AdminRoles, AdminRules, AdminSuppliers, AdminSystem,
    ];

    public static bool IsKnown(string permission) => All.Contains(permission);

    /// <summary>Permissions that make an account high-value: MFA becomes mandatory (slice 2).</summary>
    public static readonly IReadOnlySet<string> Privileged = new HashSet<string>(StringComparer.Ordinal)
    {
        InvoiceApprove, InvoiceOverride, AdminUsers, AdminRoles, AdminRules, AdminSuppliers, AdminSystem,
    };
}

/// <summary>Roles seeded on first start. Admins can edit them later, but not delete them.</summary>
public static class BuiltInRoles
{
    public const string Submitter = "Submitter";
    public const string Clerk = "Clerk";
    public const string Supervisor = "Supervisor";
    public const string FinanceAdmin = "FinanceAdmin";
    public const string Auditor = "Auditor";
    public const string SystemAdmin = "SystemAdmin";

    /// <summary>
    /// Every permission, always (re-synced at start, so new permissions are included). Locked: its permissions can't be
    /// edited, and only a super admin can grant it or manage a super admin's account. The bootstrap admin gets it.
    /// </summary>
    public const string SuperAdmin = "SuperAdmin";

    public static readonly IReadOnlyDictionary<string, (string Description, string[] Permissions)> Defaults =
        new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            [SuperAdmin] = ("Full access: every permission, including ones added later. Locked.", [.. Permissions.All]),
            [Submitter] = ("Uploads invoices and follows their status.",
                [Permissions.InvoiceReadOwn, Permissions.InvoiceSubmit]),
            [Clerk] = ("Accounts-payable clerk: reviews and corrects exceptions.",
                [Permissions.InvoiceReadAll, Permissions.InvoiceSubmit, Permissions.InvoiceCorrect, Permissions.InvoiceReject]),
            [Supervisor] = ("AP supervisor: everything a clerk does, plus override and approval.",
                [Permissions.InvoiceReadAll, Permissions.InvoiceSubmit, Permissions.InvoiceCorrect, Permissions.InvoiceReject,
                 Permissions.InvoiceOverride, Permissions.InvoiceApprove, Permissions.InvoiceRetryPosting]),
            [FinanceAdmin] = ("Finance manager: approves large invoices, reads the audit trail.",
                [Permissions.InvoiceReadAll, Permissions.InvoiceApprove, Permissions.AuditRead, Permissions.AuditExport]),
            [Auditor] = ("Read-only access to invoices and the audit trail.",
                [Permissions.InvoiceReadAll, Permissions.AuditRead, Permissions.AuditExport]),
            [SystemAdmin] = ("Manages users, roles, rules, suppliers and the system. Does not process invoices.",
                [Permissions.InvoiceReadAll, Permissions.AuditRead, Permissions.AuditExport, Permissions.AdminUsers,
                 Permissions.AdminRoles, Permissions.AdminRules, Permissions.AdminSuppliers, Permissions.AdminSystem]),
        };
}

/// <summary>NIST SP 800-63B style: length over complexity rules, block obvious and common choices.</summary>
public static class PasswordPolicy
{
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password1234", "password12345", "123456789012", "qwertyuiop12", "invoiceflow1", "invoiceflow123",
        "administrator", "admin1234567", "welcome12345", "letmein12345", "passw0rd1234", "p@ssw0rd1234",
    };

    /// <returns>Problems in plain language; empty when the password is acceptable.</returns>
    public static IReadOnlyList<string> Check(string? password, string? email = null)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            problems.Add($"Use at least {MinLength} characters. A short sentence is easy to remember and hard to guess.");
            return problems;
        }

        if (password.Length > MaxLength)
        {
            problems.Add($"Use at most {MaxLength} characters.");
        }

        if (Common.Contains(password) || password.Distinct().Count() <= 2)
        {
            problems.Add("This password is too common or too repetitive.");
        }

        var local = email?.Split('@')[0];
        if (!string.IsNullOrWhiteSpace(local) && local.Length >= 3 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("Don't include your email name in the password.");
        }

        return problems;
    }
}
