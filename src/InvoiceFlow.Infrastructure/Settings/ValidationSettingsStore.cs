using System.Text.Json;
using System.Text.Json.Serialization;
using InvoiceFlow.Application.MasterData;
using InvoiceFlow.Application.Validation;
using InvoiceFlow.Domain.MasterData;
using InvoiceFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InvoiceFlow.Infrastructure.Settings;

/// <summary>One saved version of the admin's validation settings. Never updated: each change adds a row.</summary>
public sealed class SettingsVersionRow
{
    public int Version { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public string RulesJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>What an admin edits on the Rules page.</summary>
public sealed record AdminValidationSettings
{
    public decimal ApprovalThreshold { get; init; }
    public int MaxInvoiceAgeDays { get; init; }
    public bool RequirePurchaseOrder { get; init; }
    public IReadOnlyList<decimal> AllowedVatRates { get; init; } = [];
    public IReadOnlyList<string> CompanyTrns { get; init; } = [];
    public decimal UnitPricePct { get; init; }
    public decimal UnitPriceAbs { get; init; }
    public decimal QuantityOverPct { get; init; }

    public static AdminValidationSettings From(ValidationSettings s) => new()
    {
        ApprovalThreshold = s.ApprovalThreshold,
        MaxInvoiceAgeDays = s.MaxInvoiceAgeDays,
        RequirePurchaseOrder = s.RequirePurchaseOrder,
        AllowedVatRates = s.AllowedVatRates.ToList(),
        CompanyTrns = s.CompanyTrns.ToList(),
        UnitPricePct = s.DefaultTolerance.UnitPricePct,
        UnitPriceAbs = s.DefaultTolerance.UnitPriceAbs,
        QuantityOverPct = s.DefaultTolerance.QuantityOverPct,
    };

    public ValidationSettings ToSettings() => new()
    {
        ApprovalThreshold = ApprovalThreshold,
        MaxInvoiceAgeDays = MaxInvoiceAgeDays,
        RequirePurchaseOrder = RequirePurchaseOrder,
        AllowedVatRates = AllowedVatRates.ToList(),
        CompanyTrns = CompanyTrns.ToList(),
        DefaultTolerance = new ToleranceProfile { UnitPricePct = UnitPricePct, UnitPriceAbs = UnitPriceAbs, QuantityOverPct = QuantityOverPct },
    };

    /// <returns>Problems in plain language; empty when valid.</returns>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        if (ApprovalThreshold < 0) p.Add("Approval threshold can't be negative.");
        if (MaxInvoiceAgeDays is < 1 or > 3650) p.Add("Maximum invoice age must be between 1 and 3650 days.");
        if (AllowedVatRates.Count == 0 || AllowedVatRates.Any(r => r is < 0 or > 1)) p.Add("Allowed VAT rates must be fractions between 0 and 1 (5% = 0.05).");
        if (CompanyTrns.Any(t => t.Length != 15 || !t.All(char.IsAsciiDigit))) p.Add("Company TRNs must be 15 digits.");
        if (UnitPricePct is < 0 or > 0.5m) p.Add("Price tolerance must be between 0% and 50%.");
        if (UnitPriceAbs is < 0 or > 1000m) p.Add("Absolute price tolerance must be between 0 and 1,000.");
        if (QuantityOverPct is < 0 or > 0.5m) p.Add("Quantity tolerance must be between 0% and 50%.");
        return p;
    }
}

public sealed record CurrentValidationSettings(int Version, ValidationSettings Settings, IReadOnlyDictionary<string, RuleOverride> Rules, SettingsVersionRow? Row);

/// <summary>
/// Current validation settings = configuration defaults overlaid with the latest admin version (cached briefly).
/// Every validation runs with one consistent snapshot, and records its version.
/// </summary>
public sealed class ValidationSettingsStore(IServiceScopeFactory scopes, IMemoryCache cache, IOptionsMonitor<ValidationSettings> defaults)
{
    private const string CacheKey = "validation-settings";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(20);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public CurrentValidationSettings Current()
    {
        if (cache.TryGetValue(CacheKey, out CurrentValidationSettings? cached) && cached is not null)
        {
            return cached;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>();
        var row = db.SettingsVersions.AsNoTracking().OrderByDescending(v => v.Version).FirstOrDefault();

        CurrentValidationSettings current;
        if (row is null)
        {
            current = new CurrentValidationSettings(0, defaults.CurrentValue, new Dictionary<string, RuleOverride>(), null);
        }
        else
        {
            var admin = JsonSerializer.Deserialize<AdminValidationSettings>(row.SettingsJson, Json) ?? AdminValidationSettings.From(defaults.CurrentValue);
            var rules = JsonSerializer.Deserialize<Dictionary<string, RuleOverride>>(row.RulesJson, Json) ?? [];
            current = new CurrentValidationSettings(row.Version, admin.ToSettings(), rules, row);
        }

        cache.Set(CacheKey, current, CacheFor);
        return current;
    }

    public async Task<SettingsVersionRow> SaveAsync(AdminValidationSettings settings, IReadOnlyDictionary<string, RuleOverride> rules, string actor, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>();
        var next = (await db.SettingsVersions.MaxAsync(v => (int?)v.Version, ct).ConfigureAwait(false) ?? 0) + 1;

        // Locked rules are never stored as overridden.
        var clean = rules.Where(r => RuleCatalog.Find(r.Key) is { Locked: false }).ToDictionary(r => r.Key, r => r.Value);
        var row = new SettingsVersionRow
        {
            Version = next,
            SettingsJson = JsonSerializer.Serialize(settings, Json),
            RulesJson = JsonSerializer.Serialize(clean, Json),
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = actor,
            Reason = reason,
        };
        db.SettingsVersions.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        cache.Remove(CacheKey);
        return row;
    }

    public async Task<IReadOnlyList<SettingsVersionRow>> HistoryAsync(int take, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InvoiceFlowDbContext>();
        return await db.SettingsVersions.AsNoTracking().OrderByDescending(v => v.Version).Take(take).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Engine for one request/job, using the current settings snapshot and rule overrides.</summary>
    public static ValidationEngine CreateEngine(IServiceProvider sp)
    {
        var current = sp.GetRequiredService<ValidationSettingsStore>().Current();
        return new ValidationEngine(
            sp.GetRequiredService<IMasterDataService>(),
            sp.GetRequiredService<IInvoiceHistory>(),
            Options.Create(current.Settings),
            sp.GetRequiredService<TimeProvider>(),
            RuleSet.Apply(current.Rules),
            current.Version);
    }
}
