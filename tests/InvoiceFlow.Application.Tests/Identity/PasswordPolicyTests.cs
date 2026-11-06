using InvoiceFlow.Application.Identity;

namespace InvoiceFlow.Application.Tests.Identity;

public class PasswordPolicyTests
{
    [Theory]
    [InlineData("correct horse battery")]
    [InlineData("Invoices-Are-Fun-2026")]
    [InlineData("مرحبا بك في النظام")]
    public void Long_passphrases_are_accepted(string password) =>
        Assert.Empty(PasswordPolicy.Check(password, "clara@example.com"));

    [Theory]
    [InlineData("Qqq1234$$")]   // 9 chars: complexity doesn't replace length
    [InlineData("short")]
    [InlineData("")]
    [InlineData(null)]
    public void Short_passwords_are_rejected(string? password) =>
        Assert.NotEmpty(PasswordPolicy.Check(password));

    [Theory]
    [InlineData("password1234")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("abababababababab")]
    public void Common_or_repetitive_passwords_are_rejected(string password) =>
        Assert.NotEmpty(PasswordPolicy.Check(password));

    [Fact]
    public void Password_containing_the_email_name_is_rejected() =>
        Assert.NotEmpty(PasswordPolicy.Check("my-clara-password-2026", "clara@example.com"));

    [Fact]
    public void Every_built_in_role_uses_only_known_permissions() =>
        Assert.All(BuiltInRoles.Defaults.Values.SelectMany(r => r.Permissions), p => Assert.True(Permissions.IsKnown(p), p));

    [Fact]
    public void Only_the_admin_roles_can_administer_by_default() =>
        Assert.Equal([BuiltInRoles.SuperAdmin, BuiltInRoles.SystemAdmin],
            BuiltInRoles.Defaults.Where(r => r.Value.Permissions.Contains(Permissions.AdminUsers)).Select(r => r.Key).Order(StringComparer.Ordinal));

    [Fact]
    public void Super_admin_has_every_permission() =>
        Assert.Equal(Permissions.All.Order(StringComparer.Ordinal), BuiltInRoles.Defaults[BuiltInRoles.SuperAdmin].Permissions.Order(StringComparer.Ordinal));

    [Fact]
    public void Disabled_rule_is_removed_and_locked_rules_ignore_overrides()
    {
        var rules = InvoiceFlow.Application.Validation.RuleSet.Apply(new Dictionary<string, InvoiceFlow.Application.Validation.RuleOverride>
        {
            ["PO-003"] = new(Enabled: false),
            ["SUP-002"] = new(Enabled: false), // locked: must stay
        });
        var codes = rules.Select(r => r.Code).ToList();
        Assert.DoesNotContain("PO-003", codes);
        Assert.Contains("SUP-002", codes);
    }

    [Fact]
    public void Every_rule_in_the_engine_is_described_in_the_catalog() =>
        Assert.All(InvoiceFlow.Application.Validation.ValidationEngine.DefaultRules,
            r => Assert.NotNull(InvoiceFlow.Application.Validation.RuleCatalog.Find(r.Code)));

    [Fact]
    public void SystemAdmin_does_not_process_invoices_by_default()
    {
        // Separation of duties: whoever grants rights doesn't also approve payments.
        var admin = BuiltInRoles.Defaults[BuiltInRoles.SystemAdmin].Permissions;
        Assert.DoesNotContain(Permissions.InvoiceApprove, admin);
        Assert.DoesNotContain(Permissions.InvoiceOverride, admin);
    }
}
