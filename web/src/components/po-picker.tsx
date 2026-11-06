"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ClipboardList } from "lucide-react";
import clsx from "clsx";
import { api } from "@/lib/api";
import { useI18n } from "@/lib/i18n";

interface PoOption {
  poNumber: string; currency: string; total: number; matchingLines: number; invoiceLines: number;
  lines: { sku: string | null; description: string; remaining: number; unitPrice: number }[];
}

/**
 * Shown when the PO number is missing or wrong: the supplier's open purchase orders, best match first,
 * so the reviewer picks a real PO instead of guessing. The rules still check the choice on save.
 */
export function PoPicker({ invoiceId, current, onPick }: { invoiceId: string; current: string; onPick: (po: string) => void }) {
  const { t, money } = useI18n();
  const [open, setOpen] = useState<string | null>(null);
  const { data, isLoading, error } = useQuery({
    queryKey: ["invoice", invoiceId, "purchase-orders"],
    queryFn: () => api<PoOption[]>(`/invoices/${invoiceId}/purchase-orders`),
  });

  return (
    <div className="rounded-lg border border-slate-200 bg-slate-50 p-3 text-sm">
      <p className="mb-2 flex items-center gap-2 font-medium text-slate-800">
        <ClipboardList size={16} className="text-brand-600" /> {t("po.title")}
      </p>
      {isLoading && <p className="text-slate-500">{t("common.loading")}</p>}
      {error && <p className="text-red-700">{(error as Error).message}</p>}
      {data && data.length === 0 && <p className="text-slate-600">{t("po.none")}</p>}
      {data && data.length > 0 && (
        <ul className="space-y-1.5">
          {data.map((po) => {
            const chosen = po.poNumber.toLowerCase() === current.trim().toLowerCase();
            return (
              <li key={po.poNumber} className={clsx("rounded-md border bg-white", chosen ? "border-brand-500" : "border-slate-200")}>
                <div className="flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2">
                  <button className="flex items-center gap-1 font-mono font-medium ltr-value" dir="ltr"
                    onClick={() => setOpen(open === po.poNumber ? null : po.poNumber)} aria-expanded={open === po.poNumber}>
                    <ChevronDown size={14} className={clsx("transition-transform", open !== po.poNumber && "-rotate-90 rtl:rotate-90")} />
                    {po.poNumber}
                  </button>
                  <span className={clsx("rounded-full px-2 py-0.5 text-xs",
                    po.matchingLines === 0 ? "bg-slate-100 text-slate-600"
                      : po.matchingLines === po.invoiceLines ? "bg-emerald-50 text-emerald-700" : "bg-amber-50 text-amber-800")}>
                    {t("po.matching").replace("{n}", String(po.matchingLines)).replace("{total}", String(po.invoiceLines))}
                  </span>
                  <span className="text-xs text-slate-500 ltr-value">{money(po.total, po.currency)}</span>
                  <button className="btn-secondary ms-auto px-2 py-1 text-xs" disabled={chosen} onClick={() => onPick(po.poNumber)}>
                    {chosen ? t("po.chosen") : t("po.use")}
                  </button>
                </div>
                {open === po.poNumber && (
                  <table className="w-full border-t border-slate-100 text-xs">
                    <thead className="text-slate-500">
                      <tr>
                        <th className="px-3 py-1 text-start font-normal">{t("field.sku")}</th>
                        <th className="px-3 py-1 text-start font-normal">{t("field.description")}</th>
                        <th className="px-3 py-1 text-end font-normal">{t("po.remaining")}</th>
                        <th className="px-3 py-1 text-end font-normal">{t("field.unitPrice")}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {po.lines.map((l, i) => (
                        <tr key={i} className="border-t border-slate-50">
                          <td className="px-3 py-1 font-mono ltr-value">{l.sku ?? "—"}</td>
                          <td className="px-3 py-1" dir="auto">{l.description}</td>
                          <td className="px-3 py-1 text-end tabular-nums">{l.remaining}</td>
                          <td className="px-3 py-1 text-end tabular-nums ltr-value">{l.unitPrice.toFixed(2)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </li>
            );
          })}
        </ul>
      )}
      <p className="mt-2 text-xs text-slate-500">{t("po.hint")}</p>
    </div>
  );
}
