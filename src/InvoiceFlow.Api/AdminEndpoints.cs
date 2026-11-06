using System.Security.Claims;
using InvoiceFlow.Api.Security;
using InvoiceFlow.Application.Identity;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Application.Workflow;
using InvoiceFlow.Domain.Invoices;
using InvoiceFlow.Infrastructure.Identity;
using InvoiceFlow.Infrastructure.MockErp;
using InvoiceFlow.Infrastructure.Persistence;
using InvoiceFlow.Infrastructure.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static InvoiceFlow.Api.InvoiceEndpoints;

namespace InvoiceFlow.Api;

/// <summary>Administration, dashboard and audit-viewer endpoints (ADR-0009 slices 3–6).</summary>
public static class AdminEndpoints
{
    public sealed record CreatedUserResponse(Guid Id, string TemporaryPassword);

    public sealed record TemporaryPasswordResponse(string TemporaryPassword);

    public sealed record RuleView(
        string Code, string Category, string NameEn, string NameAr, string DescriptionEn, string DescriptionAr,
        bool Locked, bool Enabled, Severity? Severity);

    public sealed record RulesView(int Version, DateTimeOffset? UpdatedAt, string? UpdatedBy, string? Reason,
        AdminValidationSettings Settings, IReadOnlyList<RuleView> Rules);

    public sealed record SaveRulesRequest(AdminValidationSettings Settings, Dictionary<string, RuleOverride> Rules, string Reason);

    private static Actor ActorOf(ClaimsPrincipal user) =>
        new(Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null,
            user.FindFirstValue("email") ?? user.FindFirstValue("name") ?? "unknown");

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").WithTags("Admin");

        // ---------------- users ----------------
        admin.MapGet("/users", async (UserAdminService svc, CancellationToken ct) => TypedResults.Ok(await svc.ListUsersAsync(ct)))
            .RequireAuthorization(Permissions.AdminUsers);

        admin.MapPost("/users", (SaveUser body, ClaimsPrincipal u, UserAdminService svc, CancellationToken ct) => Run(async () =>
            {
                var (id, temp) = await svc.CreateUserAsync(body, ActorOf(u), ct);
                return TypedResults.Ok(new CreatedUserResponse(id, temp));
            }))
            .RequireAuthorization(Permissions.AdminUsers);

        admin.MapPut("/users/{id:guid}", (Guid id, SaveUser body, ClaimsPrincipal u, UserAdminService svc, CancellationToken ct) => Run(async () =>
            {
                await svc.UpdateUserAsync(id, body, ActorOf(u), ct);
                return TypedResults.NoContent();
            }))
            .RequireAuthorization(Permissions.AdminUsers);

        admin.MapPost("/users/{id:guid}/reset-password", (Guid id, ClaimsPrincipal u, UserAdminService svc, CancellationToken ct) => Run(async () =>
                TypedResults.Ok(new TemporaryPasswordResponse(await svc.ResetPasswordAsync(id, ActorOf(u), ct)))))
            .RequireAuthorization(Permissions.AdminUsers);

        admin.MapPost("/users/{id:guid}/sign-out", (Guid id, ClaimsPrincipal u, UserAdminService svc, CancellationToken ct) => Run(async () =>
            {
                await svc.SignOutEverywhereAsync(id, ActorOf(u), ct);
                return TypedResults.NoContent();
            }))
            .RequireAuthorization(Permissions.AdminUsers);

        admin.MapPost("/users/{id:guid}/unlock", (Guid id, ClaimsPrincipal u, UserAdminService svc, CancellationToken ct) => Run(async () =>
            {
                await svc.UnlockAsync(id, ActorOf(u), ct);
                return TypedResults.NoContent();
            }))
            .RequireAuthorization(Permissions.AdminUsers);

        // ---------------- roles ----------------
        admin.MapGet("/roles", async (UserAdminService svc, CancellationToken ct) => TypedResults.Ok(await svc.ListRolesAsync(ct)))
            .RequireAuthorization(Policies.RolesRead);

        admin.MapPost("/roles", (SaveRole body, ClaimsPrincipal u, UserAdminService svc, CancellationToken ct) => Run(async () =>
                TypedResults.Ok(new { id = await svc.CreateRoleAsync(body, ActorOf(u), ct) })))
            .RequireAuthorization(Permissions.AdminRoles);

        admin.MapPut("/roles/{id:guid}", (Guid id, SaveRole body, ClaimsPrincipal u, UserAdminService svc, CancellationToken ct) => Run(async () =>
            {
                await svc.UpdateRoleAsync(id, body, ActorOf(u), ct);
                return TypedResults.NoContent();
            }))
            .RequireAuthorization(Permissions.AdminRoles);

        admin.MapDelete("/roles/{id:guid}", (Guid id, ClaimsPrincipal u, UserAdminService svc, CancellationToken ct) => Run(async () =>
            {
                await svc.DeleteRoleAsync(id, ActorOf(u), ct);
                return TypedResults.NoContent();
            }))
            .RequireAuthorization(Permissions.AdminRoles);

        // ---------------- security log (sign-ins, admin changes) ----------------
        admin.MapGet("/security-events", async (InvoiceFlowDbContext db, CancellationToken ct, [FromQuery] int take = 200) =>
                TypedResults.Ok(await db.SecurityEvents.AsNoTracking().OrderByDescending(e => e.Id).Take(Math.Clamp(take, 1, 500))
                    .Select(e => new { e.Id, e.At, e.Event, e.Email, e.Actor, e.IpAddress, e.Details }).ToListAsync(ct)))
            .RequireAuthorization(Policies.SecurityLogRead);

        // ---------------- validation rules & settings ----------------
        admin.MapGet("/rules", (ValidationSettingsStore store) => TypedResults.Ok(RulesOf(store.Current())))
            .RequireAuthorization(Policies.RulesRead);

        admin.MapPut("/rules", (SaveRulesRequest body, ClaimsPrincipal u, ValidationSettingsStore store, CancellationToken ct) => Run(async () =>
            {
                var problems = body.Settings.Check().ToList();
                if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 10)
                {
                    problems.Add("Describe the reason for the change (at least 10 characters). It is kept in the history.");
                }

                if (problems.Count > 0)
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = problems.ToArray() });
                }

                await store.SaveAsync(body.Settings, body.Rules ?? [], ActorOf(u).Name, body.Reason.Trim(), ct);
                return TypedResults.Ok(RulesOf(store.Current()));
            }))
            .RequireAuthorization(Permissions.AdminRules);

        admin.MapGet("/rules/history", async (ValidationSettingsStore store, CancellationToken ct) =>
                TypedResults.Ok((await store.HistoryAsync(50, ct)).Select(v => new { v.Version, v.CreatedAt, v.CreatedBy, v.Reason })))
            .RequireAuthorization(Policies.RulesRead);

        // ---------------- suppliers (master data; read-only until the ERP adapter) ----------------
        admin.MapGet("/suppliers", (MasterDataFile data) => TypedResults.Ok(data.Suppliers.Select(s => new
            {
                s.Id, s.Name, s.NameAr, s.Trn, s.Iban, s.DefaultCurrency, s.RequiresGoodsReceipt, s.IsActive,
                OpenPurchaseOrders = data.PurchaseOrders.Count(p => p.SupplierId == s.Id && p.Status == Domain.MasterData.PurchaseOrderStatus.Open),
            })))
            .RequireAuthorization(Permissions.AdminSuppliers);

        // ---------------- system health ----------------
        admin.MapGet("/system", async (InvoiceFlowDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var now = time.GetUtcNow();
                var jobs = await db.PipelineJobs.AsNoTracking().GroupBy(j => new { j.Status, j.Step })
                    .Select(g => new { g.Key.Status, Step = g.Key.Step.ToString(), Count = g.Count() }).ToListAsync(ct);
                var oldestPending = await db.PipelineJobs.AsNoTracking().Where(j => j.Status == "Pending")
                    .OrderBy(j => j.CreatedAt).Select(j => (DateTimeOffset?)j.CreatedAt).FirstOrDefaultAsync(ct);
                var failed = await db.PipelineJobs.AsNoTracking().Where(j => j.Status == "Failed").OrderByDescending(j => j.Id).Take(20)
                    .Join(db.Documents.AsNoTracking(), j => j.DocumentId, d => d.Id, (j, d) => new
                    {
                        j.Id, j.DocumentId, d.FileName, Step = j.Step.ToString(), j.Attempts, j.LastError, j.CompletedAt,
                    })
                    .ToListAsync(ct);
                var documents = await db.Documents.AsNoTracking().CountAsync(ct);
                var users = await db.Users.AsNoTracking().CountAsync(u => u.IsActive, ct);
                return TypedResults.Ok(new
                {
                    Jobs = jobs,
                    OldestPendingSeconds = oldestPending is { } o ? (int)(now - o).TotalSeconds : 0,
                    Failed = failed,
                    Documents = documents,
                    ActiveUsers = users,
                });
            })
            .RequireAuthorization(Permissions.AdminSystem);

        admin.MapPost("/system/jobs/{id:long}/retry", async (long id, InvoiceFlowDbContext db, TimeProvider time, CancellationToken ct) =>
            {
                var n = await db.PipelineJobs.Where(j => j.Id == id && j.Status == "Failed").ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, "Pending").SetProperty(j => j.Attempts, 0)
                    .SetProperty(j => j.NotBefore, time.GetUtcNow()).SetProperty(j => j.LastError, (string?)null), ct);
                return n == 0 ? Results.NotFound() : Results.NoContent();
            })
            .RequireAuthorization(Permissions.AdminSystem);

        // ---------------- invoice audit trail (viewer) ----------------
        app.MapGet("/api/audit/events", async (InvoiceFlowDbContext db, CancellationToken ct,
                [FromQuery] int take = 200, [FromQuery] string? actor = null, [FromQuery] string? action = null) =>
            {
                var q = db.AuditEvents.AsNoTracking();
                if (!string.IsNullOrWhiteSpace(actor))
                {
                    q = q.Where(e => e.Actor.Contains(actor));
                }

                if (!string.IsNullOrWhiteSpace(action))
                {
                    q = q.Where(e => e.Action == action);
                }

                var rows = await q.OrderByDescending(e => e.Id).Take(Math.Clamp(take, 1, 500))
                    .Join(db.Documents.AsNoTracking(), e => e.DocumentId, d => d.Id, (e, d) => new
                    {
                        e.Id, e.At, e.Actor, e.Action, e.FromStatus, e.ToStatus, e.Details, e.DocumentId, d.FileName, d.SupplierName, d.InvoiceNumber,
                    })
                    .ToListAsync(ct);
                return TypedResults.Ok(rows);
            })
            .RequireAuthorization(Permissions.AuditRead)
            .WithTags("Audit");

        // ---------------- dashboard ----------------
        app.MapGet("/api/dashboard", DashboardAsync).RequireAuthorization(Policies.Read).WithTags("Dashboard");
    }

    private static RulesView RulesOf(CurrentValidationSettings current) =>
        new(current.Version, current.Row?.CreatedAt, current.Row?.CreatedBy, current.Row?.Reason,
            AdminValidationSettings.From(current.Settings),
            RuleCatalog.All.Select(r =>
            {
                var o = r.Locked ? null : current.Rules.GetValueOrDefault(r.Code);
                return new RuleView(r.Code, r.Category, r.NameEn, r.NameAr, r.DescriptionEn, r.DescriptionAr, r.Locked, o?.Enabled ?? true, o?.Severity);
            }).ToList());

    private static readonly InvoiceStatus[] Decided =
    [
        InvoiceStatus.NeedsReview, InvoiceStatus.PendingApproval, InvoiceStatus.Approved, InvoiceStatus.Posting,
        InvoiceStatus.Posted, InvoiceStatus.PostingFailed, InvoiceStatus.Rejected,
    ];

    /// <summary>
    /// Numbers for the home page. Submitter-only users see their own uploads only.
    /// Touchless = decided invoices that never went to a person (no NeedsReview in their history).
    /// </summary>
    private static async Task<IResult> DashboardAsync(ClaimsPrincipal principal, InvoiceFlowDbContext db, TimeProvider time, CancellationToken ct)
    {
        var user = principal.ToUserContext();
        var now = time.GetUtcNow();
        var docs = db.Documents.AsNoTracking();
        if (user.SeesOnlyOwnDocuments())
        {
            docs = docs.Where(d => d.SubmittedBy == user.Id);
        }

        var byStatus = await docs.GroupBy(d => d.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(InvoiceStatus s) => byStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        var since = now.AddDays(-30);
        var recent = await docs.Where(d => d.ReceivedAt >= since)
            .Select(d => new { d.Id, d.ReceivedAt, d.Status, d.Total, d.Currency }).ToListAsync(ct);
        var recentIds = recent.Select(d => d.Id).ToList();
        var reviewed = (await db.AuditEvents.AsNoTracking()
                .Where(e => e.ToStatus == InvoiceStatus.NeedsReview && recentIds.Contains(e.DocumentId))
                .Select(e => e.DocumentId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var decided = recent.Where(d => Decided.Contains(d.Status)).ToList();
        var touchless = decided.Count(d => !reviewed.Contains(d.Id));

        // Last 14 days, per UTC day: straight-through vs needed a person.
        var days = Enumerable.Range(0, 14).Select(i => DateOnly.FromDateTime(now.UtcDateTime.Date.AddDays(i - 13))).ToList();
        var series = days.Select(day =>
        {
            var ofDay = decided.Where(d => DateOnly.FromDateTime(d.ReceivedAt.UtcDateTime) == day).ToList();
            return new { Day = day, StraightThrough = ofDay.Count(d => !reviewed.Contains(d.Id)), NeededReview = ofDay.Count(d => reviewed.Contains(d.Id)) };
        }).ToList();

        var postedValue = recent.Where(d => d.Status == InvoiceStatus.Posted && d.Total is not null)
            .GroupBy(d => d.Currency ?? "").Select(g => new { Currency = g.Key, Total = g.Sum(d => d.Total!.Value) }).ToList();

        // What can I act on?
        var limit = user.ApprovalLimit;
        var approvable = 0;
        if (user.Can(Permissions.InvoiceApprove) && limit is { } l)
        {
            var pending = docs.Where(d => d.Status == InvoiceStatus.PendingApproval && d.ResolvedBy != user.Id);
            // "Unlimited" is decimal.MaxValue, which doesn't fit SQL decimal(18,4): don't send it as a parameter.
            approvable = l == decimal.MaxValue
                ? await pending.CountAsync(ct)
                : await pending.CountAsync(d => (d.Total ?? 0) <= l, ct);
        }

        var activity = await db.AuditEvents.AsNoTracking().OrderByDescending(e => e.Id)
            .Join(docs, e => e.DocumentId, d => d.Id, (e, d) => new { e.At, e.Actor, e.Action, e.ToStatus, e.DocumentId, d.FileName, d.SupplierName })
            .Take(8).ToListAsync(ct);

        return TypedResults.Ok(new
        {
            ReceivedToday = recent.Count(d => d.ReceivedAt >= now.UtcDateTime.Date),
            Received7Days = recent.Count(d => d.ReceivedAt >= now.AddDays(-7)),
            Received30Days = recent.Count,
            Decided30Days = decided.Count,
            Touchless30Days = touchless,
            NeedsReview = Count(InvoiceStatus.NeedsReview),
            PendingApproval = Count(InvoiceStatus.PendingApproval),
            PostingFailed = Count(InvoiceStatus.PostingFailed),
            Processing = byStatus.Where(x => x.Status is InvoiceStatus.Received or InvoiceStatus.Preprocessing or InvoiceStatus.Extracting
                or InvoiceStatus.Extracted or InvoiceStatus.Validating or InvoiceStatus.Approved or InvoiceStatus.Posting).Sum(x => x.Count),
            Posted = Count(InvoiceStatus.Posted),
            PostedValue30Days = postedValue,
            ApprovableByMe = approvable,
            MyApprovalLimit = limit == decimal.MaxValue ? (decimal?)null : limit,
            MyApprovalUnlimited = limit == decimal.MaxValue,
            Series = series,
            Activity = activity,
        });
    }
}
