"use client";

import { useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { History, Lock } from "lucide-react";
import clsx from "clsx";
import { api, put } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";
import { Card, ErrorNote, Modal, PageHeader, Toast } from "@/components/ui";

type Severity = "Error" | "Warning";
interface Rule { code: string; category: string; nameEn: string; nameAr: string; descriptionEn: string; descriptionAr: string; locked: boolean; enabled: boolean; severity: Severity | null }
interface Settings {
  approvalThreshold: number; maxInvoiceAgeDays: number; requirePurchaseOrder: boolean; allowedVatRates: number[];
  companyTrns: string[]; unitPricePct: number; unitPriceAbs: number; quantityOverPct: number;
}
interface RulesView { version: number; updatedAt: string | null; updatedBy: string | null; reason: string | null; settings: Settings; rules: Rule[] }
interface Version { version: number; createdAt: string; createdBy: string; reason: string }

/** Editable copy of the settings (strings while typing; percentages shown as %). */
interface Draft {
  approvalThreshold: string; maxInvoiceAgeDays: string; requirePurchaseOrder: boolean; vatRates: string; companyTrns: string;
  pricePct: string; priceAbs: string; qtyPct: string; rules: Record<string, { enabled: boolean; severity: Severity | null }>;
}

const pct = (v: number) => String(+(v * 100).toFixed(4));

function toDraft(v: RulesView): Draft {
  return {
    approvalThreshold: String(v.settings.approvalThreshold),
    maxInvoiceAgeDays: String(v.settings.maxInvoiceAgeDays),
    requirePurchaseOrder: v.settings.requirePurchaseOrder,
    vatRates: v.settings.allowedVatRates.map(pct).join(", "),
    companyTrns: v.settings.companyTrns.join("\n"),
    pricePct: pct(v.settings.unitPricePct),
    priceAbs: String(v.settings.unitPriceAbs),
    qtyPct: pct(v.settings.quantityOverPct),
    rules: Object.fromEntries(v.rules.map((r) => [r.code, { enabled: r.enabled, severity: r.severity }])),
  };
}

export default function RulesPage() {
  const { t, lang, label, dateTime, money } = useI18n();
  const { me } = useAuth();
  const canEdit = !!me?.permissions.includes("admin.rules");
  const qc = useQueryClient();
  const view = useQuery({ queryKey: ["admin", "rules"], queryFn: () => api<RulesView>("/admin/rules") });
  const history = useQuery({ queryKey: ["admin", "rules", "history"], queryFn: () => api<Version[]>("/admin/rules/history") });

  const [draft, setDraft] = useState<Draft | null>(null);
  const [saving, setSaving] = useState(false);
  const [toast, setToast] = useState<string | null>(null);

  useEffect(() => { if (view.data) setDraft(toDraft(view.data)); }, [view.data]);

  const dirty = useMemo(() => !!draft && !!view.data && JSON.stringify(draft) !== JSON.stringify(toDraft(view.data)), [draft, view.data]);
  const byCategory = useMemo(() => {
    const m = new Map<string, Rule[]>();
    for (const r of view.data?.rules ?? []) m.set(r.category, [...(m.get(r.category) ?? []), r]);
    return [...m.entries()];
  }, [view.data]);

  if (!draft || !view.data) return <div className="p-6 text-slate-500">{t("common.loading")}</div>;

  const field = (label: string, key: "approvalThreshold" | "maxInvoiceAgeDays" | "pricePct" | "priceAbs" | "qtyPct", hint?: string) => (
    <label className="block">
      <span className="mb-1 block text-sm text-slate-600">{label}</span>
      <input className="input" dir="ltr" inputMode="decimal" value={draft[key]} onChange={(e) => setDraft({ ...draft, [key]: e.target.value })} />
      {hint && <span className="mt-1 block text-xs text-slate-500">{hint}</span>}
    </label>
  );

  return (
    <div className="mx-auto max-w-7xl p-4 lg:p-6">
      <PageHeader title={t("page.rules.title")} intro={t("page.rules.intro")}
        actions={canEdit && <button className="btn-primary" disabled={!dirty} onClick={() => setSaving(true)}>{t("adm.save")}</button>} />
      {!canEdit && <p className="mb-4 rounded bg-slate-100 px-3 py-2 text-sm text-slate-600">{t("adm.readOnly")}</p>}

      <p className="mb-4 text-xs text-slate-500">
        {t("rules.version")} {view.data.version || "—"} · {view.data.version ? `${dateTime(view.data.updatedAt)} ${t("rules.by")} ${view.data.updatedBy}` : t("rules.default")}
      </p>

      <fieldset disabled={!canEdit} className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_22rem]">
        <div className="space-y-4">
          <Card title={t("rules.settings")}>
            <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              {field(t("rules.approvalThreshold"), "approvalThreshold", t("rules.approvalThreshold.hint"))}
              {field(t("rules.pricePct"), "pricePct", t("rules.pricePct.hint"))}
              {field(t("rules.priceAbs"), "priceAbs")}
              {field(t("rules.qtyPct"), "qtyPct")}
              {field(t("rules.maxAge"), "maxInvoiceAgeDays")}
              <label className="block">
                <span className="mb-1 block text-sm text-slate-600">{t("rules.vatRates")}</span>
                <input className="input" dir="ltr" value={draft.vatRates} onChange={(e) => setDraft({ ...draft, vatRates: e.target.value })} />
                <span className="mt-1 block text-xs text-slate-500">{t("rules.vatRates.hint")}</span>
              </label>
              <label className="flex items-start gap-2 sm:col-span-2 lg:col-span-1">
                <input type="checkbox" className="mt-1" checked={draft.requirePurchaseOrder} onChange={(e) => setDraft({ ...draft, requirePurchaseOrder: e.target.checked })} />
                <span><span className="block text-sm text-slate-700">{t("rules.requirePo")}</span><span className="text-xs text-slate-500">{t("rules.requirePo.hint")}</span></span>
              </label>
              <label className="block sm:col-span-2">
                <span className="mb-1 block text-sm text-slate-600">{t("rules.companyTrns")}</span>
                <textarea className="input h-20 font-mono" dir="ltr" value={draft.companyTrns} onChange={(e) => setDraft({ ...draft, companyTrns: e.target.value })} />
                <span className="mt-1 block text-xs text-slate-500">{t("rules.companyTrns.hint")}</span>
              </label>
            </div>
          </Card>

          <Card title={t("rules.list")}>
            {byCategory.map(([category, rules]) => (
              <div key={category} className="mb-4 last:mb-0">
                <h3 className="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">{label("rules.cat.", category)}</h3>
                <ul className="divide-y divide-slate-100">
                  {rules.map((r) => {
                    const d = draft.rules[r.code]!;
                    return (
                      <li key={r.code} className={clsx("flex flex-wrap items-center gap-3 py-2.5", !d.enabled && "opacity-60")}>
                        <span className="w-16 font-mono text-xs text-slate-500" dir="ltr">{r.code}</span>
                        <div className="min-w-0 flex-1">
                          <p className="text-sm font-medium">{lang === "ar" ? r.nameAr : r.nameEn}</p>
                          <p className="text-xs text-slate-500">{lang === "ar" ? r.descriptionAr : r.descriptionEn}</p>
                        </div>
                        {r.locked ? (
                          <span className="flex items-center gap-1 rounded-full bg-slate-100 px-2 py-0.5 text-xs text-slate-600" title={t("rules.lockedHint")}>
                            <Lock size={12} /> {t("rules.locked")}
                          </span>
                        ) : (
                          <>
                            <select className="input w-40 py-1 text-xs" value={d.severity ?? ""} disabled={!d.enabled} aria-label={t("rules.severity")}
                              onChange={(e) => setDraft({ ...draft, rules: { ...draft.rules, [r.code]: { ...d, severity: (e.target.value || null) as Severity | null } } })}>
                              <option value="">{t("rules.sev.default")}</option>
                              <option value="Error">{t("rules.sev.Error")}</option>
                              <option value="Warning">{t("rules.sev.Warning")}</option>
                            </select>
                            <label className="flex items-center gap-1.5 text-xs">
                              <input type="checkbox" checked={d.enabled}
                                onChange={(e) => setDraft({ ...draft, rules: { ...draft.rules, [r.code]: { ...d, enabled: e.target.checked } } })} />
                              {t("rules.enabled")}
                            </label>
                          </>
                        )}
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))}
          </Card>
        </div>

        <Card title={t("rules.history")} className="h-fit">
          <ul className="space-y-3">
            {(history.data ?? []).map((v) => (
              <li key={v.version} className="flex gap-2 text-sm">
                <History size={14} className="mt-1 shrink-0 text-slate-400" />
                <div>
                  <p className="font-medium">{t("rules.version")} {v.version}</p>
                  <p className="text-xs text-slate-500">{dateTime(v.createdAt)} · {v.createdBy}</p>
                  <p className="text-xs text-slate-700" dir="auto">{v.reason}</p>
                </div>
              </li>
            ))}
            {(history.data ?? []).length === 0 && <li className="text-sm text-slate-500">{t("rules.default")} · {money(view.data.settings.approvalThreshold, "AED")}</li>}
          </ul>
        </Card>
      </fieldset>

      {saving && (
        <SaveDialog draft={draft} onClose={() => setSaving(false)} onSaved={async () => {
          setSaving(false);
          await qc.invalidateQueries({ queryKey: ["admin", "rules"] });
          setToast(t("adm.saved"));
        }} />
      )}
      {toast && <Toast message={toast} onDone={() => setToast(null)} />}
    </div>
  );
}

function SaveDialog({ draft, onClose, onSaved }: { draft: Draft; onClose: () => void; onSaved: () => void }) {
  const { t } = useI18n();
  const [reason, setReason] = useState("");
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const save = async () => {
    setBusy(true);
    setError(null);
    const num = (s: string) => Number(s.replace(/,/g, "").trim());
    try {
      await put("/admin/rules", {
        settings: {
          approvalThreshold: num(draft.approvalThreshold),
          maxInvoiceAgeDays: num(draft.maxInvoiceAgeDays),
          requirePurchaseOrder: draft.requirePurchaseOrder,
          allowedVatRates: draft.vatRates.split(",").map((x) => x.trim()).filter(Boolean).map((x) => Math.round(Number(x) * 100) / 10_000),
          companyTrns: draft.companyTrns.split(/\s+/).map((x) => x.trim()).filter(Boolean),
          unitPricePct: Math.round(num(draft.pricePct) * 100) / 10_000,
          unitPriceAbs: num(draft.priceAbs),
          quantityOverPct: Math.round(num(draft.qtyPct) * 100) / 10_000,
        },
        rules: Object.fromEntries(Object.entries(draft.rules).map(([code, r]) => [code, { enabled: r.enabled, severity: r.severity }])),
        reason: reason.trim(),
      });
      onSaved();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={t("rules.saveTitle")} onClose={onClose}
      footer={<>
        <button className="btn-secondary" onClick={onClose}>{t("adm.cancel")}</button>
        <button className="btn-primary" disabled={reason.trim().length < 10 || busy} onClick={() => void save()}>{t("adm.save")}</button>
      </>}>
      <label className="block">
        <span className="mb-1 block text-sm text-slate-600">{t("adm.reason")}</span>
        <textarea className="input h-24" value={reason} onChange={(e) => setReason(e.target.value)} autoFocus />
        <span className="mt-1 block text-xs text-slate-500">{t("adm.reasonHint")}</span>
      </label>
      <div className="mt-3"><ErrorNote error={error} /></div>
    </Modal>
  );
}
