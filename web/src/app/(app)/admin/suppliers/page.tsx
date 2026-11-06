"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Info } from "lucide-react";
import { api } from "@/lib/api";
import { useI18n } from "@/lib/i18n";
import { Card, ErrorNote, PageHeader } from "@/components/ui";

interface Supplier {
  id: string; name: string; nameAr: string | null; trn: string | null; iban: string | null; defaultCurrency: string;
  requiresGoodsReceipt: boolean; isActive: boolean; openPurchaseOrders: number;
}

export default function SuppliersPage() {
  const { t, lang } = useI18n();
  const [search, setSearch] = useState("");
  const q = useQuery({ queryKey: ["admin", "suppliers"], queryFn: () => api<Supplier[]>("/admin/suppliers") });
  const list = (q.data ?? []).filter((s) => !search || `${s.name} ${s.nameAr ?? ""} ${s.trn ?? ""}`.toLowerCase().includes(search.toLowerCase()));

  return (
    <div className="mx-auto max-w-7xl p-4 lg:p-6">
      <PageHeader title={t("page.suppliers.title")} intro={t("page.suppliers.intro")} />
      <p className="mb-4 flex gap-2 rounded-md bg-sky-50 px-3 py-2 text-sm text-sky-900"><Info size={16} className="mt-0.5 shrink-0" /> {t("sup.note")}</p>
      <Card>
        <input className="input mb-3 max-w-xs" placeholder={t("adm.search")} value={search} onChange={(e) => setSearch(e.target.value)} />
        <ErrorNote error={q.error} />
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-xs uppercase text-slate-500">
              <tr className="border-b border-slate-100">
                <th className="py-2 text-start font-medium">{t("sup.name")}</th>
                <th className="py-2 text-start font-medium">{t("sup.trn")}</th>
                <th className="py-2 text-start font-medium">{t("sup.iban")}</th>
                <th className="py-2 text-start font-medium">{t("sup.currency")}</th>
                <th className="py-2 text-start font-medium">{t("sup.threeWay")}</th>
                <th className="py-2 text-end font-medium">{t("sup.openPos")}</th>
              </tr>
            </thead>
            <tbody>
              {list.map((s) => (
                <tr key={s.id} className="border-b border-slate-50">
                  <td className="py-2.5">
                    <p className="font-medium">{lang === "ar" && s.nameAr ? s.nameAr : s.name}</p>
                    <p className="text-xs text-slate-500">{lang === "ar" ? s.name : s.nameAr}</p>
                  </td>
                  <td className="py-2.5 font-mono text-xs ltr-value">{s.trn ?? "—"}</td>
                  <td className="py-2.5 font-mono text-xs ltr-value">{s.iban ?? "—"}</td>
                  <td className="py-2.5">{s.defaultCurrency}</td>
                  <td className="py-2.5">{s.requiresGoodsReceipt ? t("adm.yes") : t("adm.no")}</td>
                  <td className="py-2.5 text-end tabular-nums">{s.openPurchaseOrders}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}
