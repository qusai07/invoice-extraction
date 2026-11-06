"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { InvoiceStatus } from "./types";
import { appAr, appEn } from "./i18n-app";

export type Lang = "en" | "ar";

const baseEn = {
  appName: "InvoiceFlow",
  "nav.queue": "Review queue", "nav.all": "All invoices",
  "auth.title": "Sign in", "auth.dev": "Development sign-in", "auth.devHint": "Pick a demo user. Each has different permissions.",
  "auth.entra": "Sign in with Microsoft", "auth.signOut": "Sign out", "auth.noRole": "Your account has no InvoiceFlow role. Ask an administrator.",
  "auth.email": "Email", "auth.password": "Password", "auth.signIn": "Sign in",
  "auth.failed": "Incorrect email or password.",
  "auth.tooMany": "Too many sign-in attempts from this device. Wait a minute and try again.",
  "auth.showPassword": "Show password", "auth.hidePassword": "Hide password",
  "auth.capsLock": "Caps Lock is on.",
  "auth.arabicLayout": "The password contains Arabic letters. Is the keyboard set to Arabic? Switch to English (Alt+Shift) and type it again.",
  "auth.unavailable": "The server can't be reached right now. Check that the InvoiceFlow API is running, then try again.",
  "auth.signOutEverywhere": "Sign out on all devices", "auth.changePassword": "Change password", "auth.account": "Account",
  "pwd.title": "Change password", "pwd.required": "For your security, choose a new password before you continue.",
  "pwd.hint": "Use at least 12 characters. A short sentence works well, e.g. three or four unrelated words.",
  "pwd.current": "Current password", "pwd.new": "New password", "pwd.confirm": "Repeat new password",
  "pwd.mismatch": "The passwords don't match.", "pwd.save": "Save new password",
  "common.loading": "Loading…", "common.error": "Something went wrong", "common.cancel": "Cancel", "common.close": "Close",
  "common.refresh": "Refresh", "common.none": "None", "common.language": "عربي",
  "queue.tab.review": "Needs review", "queue.tab.approval": "Pending approval", "queue.tab.failed": "Posting failed", "queue.tab.all": "All",
  "queue.empty": "Nothing waiting here.", "queue.received": "Received", "queue.file": "Document", "queue.supplier": "Supplier",
  "queue.invoice": "Invoice no.", "queue.total": "Total", "queue.status": "Status", "queue.issues": "Issues", "queue.by": "Submitted by",
  "upload.button": "Upload invoices", "upload.drop": "Drop PDF, PNG or JPEG files here", "upload.done": "uploaded. Processing in the background.",
  "upload.failed": "Upload failed",
  "inv.back": "Back to queue", "inv.processing": "Processing… this page updates automatically.",
  "inv.tab.data": "Invoice data", "inv.tab.history": "History",
  "inv.findings": "Checks", "inv.noFindings": "All checks passed.",
  "inv.header": "Header", "inv.lines": "Line items", "inv.addLine": "Add line", "inv.removeLine": "Remove line",
  "inv.save": "Save & re-check", "inv.saving": "Checking…", "inv.override": "Override…", "inv.approve": "Approve",
  "inv.reject": "Reject…", "inv.retry": "Retry posting", "inv.reason": "Reason (at least 10 characters)",
  "inv.overrideTitle": "Approve despite failing checks", "inv.overrideHint": "This is recorded in the audit trail with your name. Above the approval limit a second person must still approve.",
  "inv.rejectTitle": "Reject invoice", "inv.confirm": "Confirm",
  "inv.fourEyes": "You corrected or overrode this invoice, so a different person must approve it.",
  "inv.readOnly": "Read-only: this invoice is not waiting for review, or your role can't change it.",
  "inv.unsaved": "Unsaved changes", "inv.invalidNumber": "Not a valid number",
  "inv.evidence": "Show on document", "inv.page": "Page", "inv.poLine": "PO line", "inv.noMatch": "No PO match",
  "inv.suggest": "Closest", "inv.erp": "ERP reference", "inv.approvedBy": "Approved by", "inv.resolvedBy": "Resolved by",
  "inv.extractedBy": "Extracted by", "inv.cost": "cost", "inv.expected": "expected", "inv.actual": "found",
  "inv.corrections": "Corrections", "inv.audit": "Audit trail", "inv.noCorrections": "No corrections.",
  "inv.zoomIn": "Zoom in", "inv.zoomOut": "Zoom out", "inv.download": "Open original",
  "inv.imageNoHighlight": "Highlighting works on PDFs with a text layer. For images, check the page shown.",
  "field.supplierName": "Supplier", "field.supplierTrn": "Supplier TRN", "field.customerTrn": "Customer TRN",
  "field.invoiceNumber": "Invoice number", "field.invoiceDate": "Invoice date", "field.dueDate": "Due date",
  "field.poNumber": "PO number", "field.currency": "Currency", "field.subtotal": "Subtotal", "field.vatTotal": "VAT",
  "field.total": "Total", "field.iban": "IBAN", "field.sku": "Item code", "field.description": "Description",
  "field.quantity": "Qty", "field.unitPrice": "Unit price", "field.lineTotal": "Amount", "field.vatRate": "VAT %",
};

const en = { ...baseEn, ...appEn };

type Key = keyof typeof en;

const baseAr: Record<keyof typeof baseEn, string> = {
  appName: "InvoiceFlow",
  "nav.queue": "قائمة المراجعة", "nav.all": "كل الفواتير",
  "auth.title": "تسجيل الدخول", "auth.dev": "دخول بيئة التطوير", "auth.devHint": "اختر مستخدماً تجريبياً. لكل منهم صلاحيات مختلفة.",
  "auth.entra": "الدخول بحساب Microsoft", "auth.signOut": "تسجيل الخروج", "auth.noRole": "لا يملك حسابك أي دور في InvoiceFlow. تواصل مع المسؤول.",
  "auth.email": "البريد الإلكتروني", "auth.password": "كلمة المرور", "auth.signIn": "تسجيل الدخول",
  "auth.failed": "البريد الإلكتروني أو كلمة المرور غير صحيحة.",
  "auth.tooMany": "محاولات دخول كثيرة من هذا الجهاز. انتظر دقيقة ثم حاول مجدداً.",
  "auth.showPassword": "إظهار كلمة المرور", "auth.hidePassword": "إخفاء كلمة المرور",
  "auth.capsLock": "مفتاح الأحرف الكبيرة (Caps Lock) مفعّل.",
  "auth.arabicLayout": "تحتوي كلمة المرور على أحرف عربية. هل لوحة المفاتيح على العربية؟ بدّل إلى الإنجليزية (Alt+Shift) وأعد كتابتها.",
  "auth.unavailable": "تعذّر الوصول إلى الخادم حالياً. تأكد من تشغيل واجهة InvoiceFlow البرمجية ثم حاول مجدداً.",
  "auth.signOutEverywhere": "تسجيل الخروج من جميع الأجهزة", "auth.changePassword": "تغيير كلمة المرور", "auth.account": "الحساب",
  "pwd.title": "تغيير كلمة المرور", "pwd.required": "لحمايتك، اختر كلمة مرور جديدة قبل المتابعة.",
  "pwd.hint": "استخدم 12 حرفاً على الأقل. الجملة القصيرة خيار جيد، مثل ثلاث أو أربع كلمات غير مترابطة.",
  "pwd.current": "كلمة المرور الحالية", "pwd.new": "كلمة المرور الجديدة", "pwd.confirm": "أعد كتابة كلمة المرور الجديدة",
  "pwd.mismatch": "كلمتا المرور غير متطابقتين.", "pwd.save": "حفظ كلمة المرور الجديدة",
  "common.loading": "جارٍ التحميل…", "common.error": "حدث خطأ ما", "common.cancel": "إلغاء", "common.close": "إغلاق",
  "common.refresh": "تحديث", "common.none": "لا يوجد", "common.language": "English",
  "queue.tab.review": "بحاجة لمراجعة", "queue.tab.approval": "بانتظار الاعتماد", "queue.tab.failed": "فشل الترحيل", "queue.tab.all": "الكل",
  "queue.empty": "لا يوجد ما ينتظر هنا.", "queue.received": "تاريخ الاستلام", "queue.file": "المستند", "queue.supplier": "المورد",
  "queue.invoice": "رقم الفاتورة", "queue.total": "الإجمالي", "queue.status": "الحالة", "queue.issues": "الملاحظات", "queue.by": "مُرسلة من",
  "upload.button": "رفع فواتير", "upload.drop": "أفلت ملفات PDF أو PNG أو JPEG هنا", "upload.done": "تم الرفع. تجري المعالجة في الخلفية.",
  "upload.failed": "فشل الرفع",
  "inv.back": "العودة للقائمة", "inv.processing": "جارٍ المعالجة… تتحدث هذه الصفحة تلقائياً.",
  "inv.tab.data": "بيانات الفاتورة", "inv.tab.history": "السجل",
  "inv.findings": "الفحوصات", "inv.noFindings": "نجحت جميع الفحوصات.",
  "inv.header": "الرأس", "inv.lines": "البنود", "inv.addLine": "إضافة بند", "inv.removeLine": "حذف البند",
  "inv.save": "حفظ وإعادة الفحص", "inv.saving": "جارٍ الفحص…", "inv.override": "تجاوز…", "inv.approve": "اعتماد",
  "inv.reject": "رفض…", "inv.retry": "إعادة الترحيل", "inv.reason": "السبب (10 أحرف على الأقل)",
  "inv.overrideTitle": "الاعتماد رغم فشل الفحوصات", "inv.overrideHint": "يُسجَّل هذا في سجل التدقيق باسمك. فوق حد الاعتماد يلزم اعتماد شخص آخر.",
  "inv.rejectTitle": "رفض الفاتورة", "inv.confirm": "تأكيد",
  "inv.fourEyes": "قمت بتصحيح هذه الفاتورة أو تجاوزها، لذا يجب أن يعتمدها شخص آخر.",
  "inv.readOnly": "للقراءة فقط: الفاتورة ليست بانتظار المراجعة أو أن دورك لا يسمح بالتعديل.",
  "inv.unsaved": "تغييرات غير محفوظة", "inv.invalidNumber": "رقم غير صالح",
  "inv.evidence": "إظهار في المستند", "inv.page": "صفحة", "inv.poLine": "بند أمر الشراء", "inv.noMatch": "لا يطابق أمر الشراء",
  "inv.suggest": "الأقرب", "inv.erp": "مرجع النظام المحاسبي", "inv.approvedBy": "اعتمدها", "inv.resolvedBy": "عالجها",
  "inv.extractedBy": "استخرجها", "inv.cost": "التكلفة", "inv.expected": "المتوقع", "inv.actual": "الموجود",
  "inv.corrections": "التصحيحات", "inv.audit": "سجل التدقيق", "inv.noCorrections": "لا توجد تصحيحات.",
  "inv.zoomIn": "تكبير", "inv.zoomOut": "تصغير", "inv.download": "فتح الأصل",
  "inv.imageNoHighlight": "يعمل التظليل على ملفات PDF التي تحتوي نصاً. للصور، راجع الصفحة المذكورة.",
  "field.supplierName": "المورد", "field.supplierTrn": "الرقم الضريبي للمورد", "field.customerTrn": "الرقم الضريبي للعميل",
  "field.invoiceNumber": "رقم الفاتورة", "field.invoiceDate": "تاريخ الفاتورة", "field.dueDate": "تاريخ الاستحقاق",
  "field.poNumber": "رقم أمر الشراء", "field.currency": "العملة", "field.subtotal": "المجموع الفرعي", "field.vatTotal": "الضريبة",
  "field.total": "الإجمالي", "field.iban": "الآيبان", "field.sku": "رمز الصنف", "field.description": "الوصف",
  "field.quantity": "الكمية", "field.unitPrice": "سعر الوحدة", "field.lineTotal": "المبلغ", "field.vatRate": "الضريبة %",
};

const ar: Record<Key, string> = { ...baseAr, ...appAr };

const STATUS: Record<InvoiceStatus, [string, string]> = {
  Received: ["Received", "مستلمة"], Preprocessing: ["Processing", "قيد المعالجة"], Extracting: ["Reading", "جارٍ القراءة"],
  Extracted: ["Read", "تمت القراءة"], Validating: ["Checking", "جارٍ الفحص"], NeedsReview: ["Needs review", "بحاجة لمراجعة"],
  PendingApproval: ["Pending approval", "بانتظار الاعتماد"], Approved: ["Approved", "معتمدة"], Posting: ["Posting", "جارٍ الترحيل"],
  Posted: ["Posted", "مُرحّلة"], PostingFailed: ["Posting failed", "فشل الترحيل"], Rejected: ["Rejected", "مرفوضة"],
  Duplicate: ["Duplicate", "مكررة"], Closed: ["Closed", "مغلقة"],
};

/** Short names for rule codes (the full message comes from the API in both languages). */
const RULES: Record<string, [string, string]> = {
  "EXT-001": ["Unreadable value", "قيمة غير مقروءة"], "EXT-002": ["Not an invoice", "ليست فاتورة"],
  "FMT-001": ["Missing field", "حقل ناقص"], "FMT-002": ["TRN format", "صيغة الرقم الضريبي"], "FMT-003": ["Date", "التاريخ"],
  "CUS-001": ["Wrong customer", "عميل خاطئ"], "ARI-001": ["Lines ≠ subtotal", "البنود ≠ المجموع"],
  "ARI-002": ["Qty × price", "الكمية × السعر"], "ARI-003": ["Total", "الإجمالي"], "VAT-001": ["VAT amount", "مبلغ الضريبة"],
  "VAT-002": ["VAT without TRN", "ضريبة بلا رقم ضريبي"], "SUP-001": ["Unknown supplier", "مورد غير معروف"],
  "SUP-002": ["IBAN changed", "تغيّر الآيبان"], "SUP-003": ["TRN mismatch", "اختلاف الرقم الضريبي"],
  "DUP-001": ["Duplicate file", "ملف مكرر"], "DUP-002": ["Duplicate invoice", "فاتورة مكررة"],
  "PO-001": ["Purchase order missing or not found", "أمر الشراء مفقود أو غير موجود"], "PO-002": ["Line not on PO", "بند خارج أمر الشراء"],
  "PO-003": ["Price vs PO", "السعر مقابل أمر الشراء"], "PO-004": ["Qty over PO", "كمية تتجاوز أمر الشراء"],
  "PO-005": ["Currency vs PO", "العملة مقابل أمر الشراء"], "GRN-001": ["Not received", "غير مستلمة"],
  "APR-001": ["Needs 2nd approver", "يلزم اعتماد ثانٍ"],
};

interface I18n {
  lang: Lang;
  dir: "ltr" | "rtl";
  t: (k: Key) => string;
  status: (s: InvoiceStatus) => string;
  rule: (code: string) => string;
  /** Translate a value that comes from the API (event name, job step…): key `prefix + value`, else the raw value. */
  label: (prefix: string, value: string) => string;
  toggle: () => void;
  money: (v: number | null | undefined, currency?: string | null) => string;
  date: (iso: string | null | undefined) => string;
  dateTime: (iso: string | null | undefined) => string;
}

const I18nContext = createContext<I18n | null>(null);
const LANG_KEY = "invoiceflow.lang";

export function I18nProvider({ children }: { children: React.ReactNode }) {
  const [lang, setLang] = useState<Lang>("en");

  useEffect(() => {
    try {
      if (localStorage.getItem(LANG_KEY) === "ar") setLang("ar");
    } catch {
      /* storage blocked */
    }
  }, []);

  useEffect(() => {
    document.documentElement.lang = lang;
    document.documentElement.dir = lang === "ar" ? "rtl" : "ltr";
  }, [lang]);

  const toggle = useCallback(() => {
    setLang((l) => {
      const next = l === "en" ? "ar" : "en";
      try { localStorage.setItem(LANG_KEY, next); } catch { /* ignore */ }
      return next;
    });
  }, []);

  const value = useMemo<I18n>(() => {
    // Latin digits in both languages: finance teams compare numbers with the ERP and documents.
    const locale = lang === "ar" ? "ar-AE-u-nu-latn" : "en-AE";
    const moneyFmt = new Intl.NumberFormat(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const dateFmt = new Intl.DateTimeFormat(locale, { year: "numeric", month: "short", day: "numeric" });
    const dateTimeFmt = new Intl.DateTimeFormat(locale, { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
    return {
      lang,
      dir: lang === "ar" ? "rtl" : "ltr",
      t: (k) => (lang === "ar" ? ar[k] : en[k]),
      status: (s) => STATUS[s]?.[lang === "ar" ? 1 : 0] ?? s,
      rule: (code) => RULES[code]?.[lang === "ar" ? 1 : 0] ?? code,
      label: (prefix, value) => {
        const dict: Record<string, string> = lang === "ar" ? ar : en;
        return dict[prefix + value] ?? value;
      },
      toggle,
      money: (v, currency) => (v == null ? "—" : `${moneyFmt.format(v)}${currency ? ` ${currency}` : ""}`),
      date: (iso) => (iso ? dateFmt.format(new Date(iso.length === 10 ? `${iso}T00:00:00` : iso)) : "—"),
      dateTime: (iso) => (iso ? dateTimeFmt.format(new Date(iso)) : "—"),
    };
  }, [lang, toggle]);

  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18n {
  const ctx = useContext(I18nContext);
  if (!ctx) throw new Error("useI18n outside I18nProvider");
  return ctx;
}

export type { Key as I18nKey };
