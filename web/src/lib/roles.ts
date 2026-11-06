/**
 * Display names for the built-in roles (stored in the database by their fixed English key).
 * Custom roles created by an admin are shown exactly as named.
 */
const BUILT_IN: Record<string, { en: [string, string]; ar: [string, string] }> = {
  SuperAdmin: {
    en: ["Super admin", "Full access: every permission, including ones added later. Locked."],
    ar: ["المسؤول الأعلى", "صلاحيات كاملة: كل الصلاحيات، بما فيها ما يُضاف لاحقاً. مقفل."],
  },
  Submitter: {
    en: ["Submitter", "Uploads invoices and follows their status."],
    ar: ["مُقدِّم الفواتير", "يرفع الفواتير ويتابع حالتها."],
  },
  Clerk: {
    en: ["AP clerk", "Accounts-payable clerk: reviews and corrects exceptions."],
    ar: ["موظف الحسابات الدائنة", "يراجع الفواتير المتوقفة ويصحّح بياناتها."],
  },
  Supervisor: {
    en: ["AP supervisor", "Everything a clerk does, plus override and approval."],
    ar: ["مشرف الحسابات الدائنة", "كل ما يقوم به الموظف، إضافة إلى التجاوز والاعتماد."],
  },
  FinanceAdmin: {
    en: ["Finance manager", "Approves large invoices and reads the audit trail."],
    ar: ["المدير المالي", "يعتمد الفواتير الكبيرة ويطّلع على سجل التدقيق."],
  },
  Auditor: {
    en: ["Auditor", "Read-only access to invoices and the audit trail."],
    ar: ["مدقق", "اطلاع فقط على الفواتير وسجل التدقيق."],
  },
  SystemAdmin: {
    en: ["System administrator", "Manages users, roles, rules, suppliers and the system. Does not process invoices."],
    ar: ["مسؤول النظام", "يدير المستخدمين والأدوار والقواعد والموردين والنظام. لا يعالج الفواتير."],
  },
};

export function roleName(name: string, lang: "en" | "ar"): string {
  return BUILT_IN[name]?.[lang][0] ?? name;
}

/** Built-in description in the current language unless an admin changed it; custom roles keep their own text. */
export function roleDescription(name: string, stored: string, lang: "en" | "ar"): string {
  const builtIn = BUILT_IN[name];
  if (!builtIn) return stored;
  // If the admin edited the description, show theirs (it no longer equals the shipped English text).
  const shippedEn = SHIPPED_EN[name];
  return !stored || stored === shippedEn ? builtIn[lang][1] : stored;
}

/** The English descriptions seeded by the API (InvoiceFlow.Application.Identity.BuiltInRoles). */
const SHIPPED_EN: Record<string, string> = {
  SuperAdmin: "Full access: every permission, including ones added later. Locked.",
  Submitter: "Uploads invoices and follows their status.",
  Clerk: "Accounts-payable clerk: reviews and corrects exceptions.",
  Supervisor: "AP supervisor: everything a clerk does, plus override and approval.",
  FinanceAdmin: "Finance manager: approves large invoices, reads the audit trail.",
  Auditor: "Read-only access to invoices and the audit trail.",
  SystemAdmin: "Manages users, roles, rules, suppliers and the system. Does not process invoices.",
};

/** The role that always has every permission; its permissions can't be edited. */
export const SUPER_ADMIN = "SuperAdmin";

export function isBuiltInRole(name: string): boolean {
  return name in BUILT_IN;
}
