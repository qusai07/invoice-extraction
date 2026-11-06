/** Permission catalog for the UI (codes mirror InvoiceFlow.Application.Identity.Permissions). */

export interface PermissionInfo {
  code: string;
  group: "invoices" | "audit" | "admin";
  en: [string, string]; // name, description
  ar: [string, string];
}

export const PERMISSIONS: PermissionInfo[] = [
  { code: "invoice.read.own", group: "invoices", en: ["See own uploads", "Only invoices this person uploaded."], ar: ["رؤية ما رفعه", "الفواتير التي رفعها هذا الشخص فقط."] },
  { code: "invoice.read.all", group: "invoices", en: ["See all invoices", "Every invoice and its documents."], ar: ["رؤية كل الفواتير", "كل الفواتير ومستنداتها."] },
  { code: "invoice.submit", group: "invoices", en: ["Upload invoices", "Send new invoice files in."], ar: ["رفع الفواتير", "إرسال ملفات فواتير جديدة."] },
  { code: "invoice.correct", group: "invoices", en: ["Correct invoice data", "Fix values the AI read wrongly; checks run again."], ar: ["تصحيح بيانات الفاتورة", "تصحيح القيم المقروءة خطأً؛ تُعاد الفحوصات."] },
  { code: "invoice.reject", group: "invoices", en: ["Reject invoices", "Stop an invoice with a reason."], ar: ["رفض الفواتير", "إيقاف فاتورة مع ذكر السبب."] },
  { code: "invoice.override", group: "invoices", en: ["Override failed checks", "Accept an invoice despite a failed check (with reason)."], ar: ["تجاوز الفحوصات", "قبول فاتورة رغم فشل فحص (مع السبب)."] },
  { code: "invoice.approve", group: "invoices", en: ["Approve large invoices", "Second approval, up to the person's approval limit."], ar: ["اعتماد الفواتير الكبيرة", "الاعتماد الثاني حتى حد اعتماد الشخص."] },
  { code: "invoice.retry-posting", group: "invoices", en: ["Retry ERP posting", "Send an invoice to the ERP again after a failure."], ar: ["إعادة الترحيل", "إعادة إرسال فاتورة للنظام المحاسبي بعد فشل."] },
  { code: "audit.read", group: "audit", en: ["Read audit log", "See who did what and when."], ar: ["قراءة سجل التدقيق", "رؤية من فعل ماذا ومتى."] },
  { code: "audit.export", group: "audit", en: ["Export audit log", "Download the audit trail as CSV."], ar: ["تصدير سجل التدقيق", "تنزيل سجل التدقيق بصيغة CSV."] },
  { code: "admin.users", group: "admin", en: ["Manage users", "Add users, assign roles and limits, reset passwords."], ar: ["إدارة المستخدمين", "إضافة مستخدمين وتعيين الأدوار والحدود وإعادة كلمات المرور."] },
  { code: "admin.roles", group: "admin", en: ["Manage roles", "Create roles and change what they allow."], ar: ["إدارة الأدوار", "إنشاء أدوار وتغيير ما تسمح به."] },
  { code: "admin.rules", group: "admin", en: ["Manage validation rules", "Thresholds, tolerances, turning checks on or off."], ar: ["إدارة قواعد التحقق", "الحدود والهوامش وتفعيل الفحوصات أو إيقافها."] },
  { code: "admin.suppliers", group: "admin", en: ["Manage suppliers", "Supplier settings and bank-account changes."], ar: ["إدارة الموردين", "إعدادات الموردين وتغيير الحسابات البنكية."] },
  { code: "admin.system", group: "admin", en: ["System health", "Background jobs and retries."], ar: ["حالة النظام", "مهام الخلفية وإعادة المحاولة."] },
];

export function permissionLabel(code: string, lang: "en" | "ar"): string {
  const p = PERMISSIONS.find((x) => x.code === code);
  return p ? p[lang][0] : code;
}
