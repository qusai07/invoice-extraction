namespace InvoiceFlow.Application.Validation;

/// <param name="Locked">Fraud / integrity rules: an admin can't disable them or turn them into warnings (ADR-0009).</param>
public sealed record RuleInfo(string Code, string Category, string NameEn, string NameAr, string DescriptionEn, string DescriptionAr, bool Locked);

/// <summary>Human descriptions of every rule, for the admin Rules page and help texts.</summary>
public static class RuleCatalog
{
    public static readonly IReadOnlyList<RuleInfo> All =
    [
        new("EXT-001", "Reading", "Unreadable value", "قيمة غير مقروءة",
            "The AI read a value it could not turn into a date, amount or code. A person checks it.",
            "قرأ الذكاء الاصطناعي قيمة تعذّر تحويلها إلى تاريخ أو مبلغ أو رمز. يراجعها شخص.", false),
        new("EXT-002", "Reading", "Not an invoice", "ليست فاتورة",
            "The document looks like a credit note, quotation or something else.",
            "يبدو المستند إشعاراً دائناً أو عرض سعر أو شيئاً آخر.", false),
        new("FMT-001", "Format", "Required fields", "الحقول المطلوبة",
            "Supplier, invoice number, date, currency, total and at least one line must be present.",
            "يجب وجود المورد ورقم الفاتورة والتاريخ والعملة والإجمالي وبند واحد على الأقل.", false),
        new("FMT-002", "Format", "TRN format", "صيغة الرقم الضريبي",
            "UAE tax registration numbers have 15 digits.", "يتكون الرقم الضريبي الإماراتي من 15 رقماً.", false),
        new("FMT-003", "Format", "Invoice date", "تاريخ الفاتورة",
            "Not in the future; warns when older than the allowed age; due date not before invoice date.",
            "ليس في المستقبل؛ تنبيه إذا كانت أقدم من المدة المسموحة؛ الاستحقاق ليس قبل تاريخ الفاتورة.", false),
        new("CUS-001", "Fraud", "Addressed to us", "موجّهة إلينا",
            "The customer TRN on the invoice must be one of our company TRNs.",
            "يجب أن يكون الرقم الضريبي للعميل أحد أرقام شركتنا.", true),
        new("ARI-001", "Arithmetic", "Lines add up", "مجموع البنود",
            "The sum of line amounts equals the subtotal.", "مجموع مبالغ البنود يساوي المجموع الفرعي.", false),
        new("ARI-002", "Arithmetic", "Quantity × price", "الكمية × السعر",
            "Each line: quantity × unit price equals the line amount.", "لكل بند: الكمية × سعر الوحدة = مبلغ البند.", false),
        new("ARI-003", "Arithmetic", "Total", "الإجمالي",
            "Subtotal + VAT equals the total payable.", "المجموع الفرعي + الضريبة = الإجمالي المستحق.", true),
        new("VAT-001", "VAT", "VAT amount", "مبلغ الضريبة",
            "VAT matches the line amounts × their rates; unusual rates are flagged.",
            "الضريبة تطابق مبالغ البنود × نسبها؛ النسب غير المعتادة يُنبَّه عليها.", false),
        new("VAT-002", "VAT", "VAT needs a TRN", "الضريبة تتطلب رقماً ضريبياً",
            "Only a VAT-registered supplier (TRN on the invoice) may charge VAT.",
            "لا يحق احتساب الضريبة إلا لمورد مسجّل (رقم ضريبي على الفاتورة).", false),
        new("SUP-001", "Supplier", "Known supplier", "مورد معروف",
            "The supplier exists in master data and is active.", "المورد موجود في البيانات الرئيسية ونشط.", false),
        new("SUP-002", "Fraud", "Bank account (IBAN)", "الحساب البنكي (آيبان)",
            "The IBAN on the invoice equals the registered IBAN. A different account is a classic fraud.",
            "الآيبان على الفاتورة يطابق المسجل. الحساب المختلف أسلوب احتيال شائع.", true),
        new("SUP-003", "Fraud", "TRN matches supplier", "تطابق الرقم الضريبي",
            "Name matches a supplier but the printed TRN is different.", "الاسم يطابق مورداً لكن الرقم الضريبي المطبوع مختلف.", true),
        new("DUP-001", "Duplicates", "Same file again", "الملف نفسه مجدداً",
            "This exact file was received before.", "تم استلام هذا الملف نفسه من قبل.", true),
        new("DUP-002", "Duplicates", "Same invoice again", "الفاتورة نفسها مجدداً",
            "Same supplier and invoice number already received.", "تم استلام رقم الفاتورة نفسه من المورد نفسه.", true),
        new("PO-001", "Purchase order", "PO exists", "أمر الشراء موجود",
            "A PO number is printed, exists, is open and belongs to this supplier.",
            "رقم أمر الشراء مطبوع وموجود ومفتوح ويخص هذا المورد.", false),
        new("PO-005", "Purchase order", "Currency matches PO", "العملة تطابق أمر الشراء",
            "Invoice currency equals the PO currency.", "عملة الفاتورة تساوي عملة أمر الشراء.", false),
        new("PO-002", "Purchase order", "Lines on the PO", "البنود في أمر الشراء",
            "Every invoice line matches a PO line (item code or description + price).",
            "كل بند في الفاتورة يطابق بنداً في أمر الشراء.", false),
        new("PO-003", "Purchase order", "Price vs PO", "السعر مقابل أمر الشراء",
            "Unit price is not above the PO price beyond the tolerance.", "سعر الوحدة لا يتجاوز سعر أمر الشراء بأكثر من الهامش المسموح.", false),
        new("PO-004", "Purchase order", "Quantity vs PO", "الكمية مقابل أمر الشراء",
            "Invoiced quantity is not more than what is still open on the PO.", "الكمية المفوترة لا تتجاوز المتبقي في أمر الشراء.", false),
        new("GRN-001", "Purchase order", "Goods received", "البضاعة مستلمة",
            "For 3-way-matched suppliers: invoiced quantity was received and not yet invoiced.",
            "للموردين بالمطابقة الثلاثية: الكمية المفوترة مستلمة وغير مفوترة سابقاً.", false),
        new("APR-001", "Approval", "Second approver", "اعتماد ثانٍ",
            "Invoices at or above the approval threshold need a second person to approve.",
            "الفواتير التي تساوي حد الاعتماد أو تتجاوزه تحتاج اعتماد شخص ثانٍ.", true),
    ];

    public static RuleInfo? Find(string code) => All.FirstOrDefault(r => r.Code == code);
}

/// <summary>Per-rule admin choice. Locked rules ignore it.</summary>
public sealed record RuleOverride(bool Enabled = true, Severity? Severity = null);

/// <summary>Wraps a rule to apply an admin's severity override (Error ↔ Warning).</summary>
public sealed class SeverityOverrideRule(IValidationRule inner, Severity severity) : IValidationRule
{
    public string Code => inner.Code;

    public IEnumerable<ValidationFinding> Evaluate(ValidationContext context) =>
        inner.Evaluate(context).Select(f => f.Severity == Severity.Info ? f : f with { Severity = severity });
}

public static class RuleSet
{
    /// <summary>The default rules with admin overrides applied (disabled removed, severities changed). Locked rules are untouched.</summary>
    public static IReadOnlyList<IValidationRule> Apply(IReadOnlyDictionary<string, RuleOverride>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return ValidationEngine.DefaultRules;
        }

        var result = new List<IValidationRule>();
        foreach (var rule in ValidationEngine.DefaultRules)
        {
            var locked = RuleCatalog.Find(rule.Code)?.Locked ?? true;
            if (locked || !overrides.TryGetValue(rule.Code, out var o))
            {
                result.Add(rule);
                continue;
            }

            if (!o.Enabled)
            {
                continue;
            }

            result.Add(o.Severity is { } s and not Severity.Info ? new SeverityOverrideRule(rule, s) : rule);
        }

        return result;
    }
}
