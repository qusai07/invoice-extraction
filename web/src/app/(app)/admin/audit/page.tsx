"use client";

import { useState } from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { Download } from "lucide-react";
import clsx from "clsx";
import { api, blobUrl } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";
import type { InvoiceStatus } from "@/lib/types";
import { Card, ErrorNote, PageHeader, StatusBadge } from "@/components/ui";

interface InvoiceEvent {
  id: number; at: string; actor: string; action: string; fromStatus: InvoiceStatus | null; toStatus: InvoiceStatus | null;
  details: string | null; documentId: string; fileName: string; supplierName: string | null; invoiceNumber: string | null;
}
interface SecurityEvent { id: number; at: string; event: string; email: string | null; actor: string | null; ipAddress: string | null; details: string | null }

export default function AuditPage() {
  const { t, dateTime } = useI18n();
  const { me } = useAuth();
  const canInvoices = !!me?.permissions.includes("audit.read");
  const canExport = !!me?.permissions.includes("audit.export");
  const [tab, setTab] = useState<"invoices" | "security">(canInvoices ? "invoices" : "security");
  const [actor, setActor] = useState("");

  const invoices = useQuery({
    queryKey: ["audit", "events", actor], enabled: tab === "invoices" && canInvoices,
    queryFn: () => api<InvoiceEvent[]>(`/audit/events?take=200${actor ? `&actor=${encodeURIComponent(actor)}` : ""}`),
  });
  const security = useQuery({ queryKey: ["audit", "security"], enabled: tab === "security", queryFn: () => api<SecurityEvent[]>("/admin/security-events?take=200") });

  const exportCsv = async () => {
    const now = new Date();
    const from = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1)).toISOString().slice(0, 10);
    const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() + 1)).toISOString().slice(0, 10);
    const { url } = await blobUrl(`/audit/export?from=${from}&to=${to}`);
    const a = document.createElement("a");
    a.href = url;
    a.download = `invoiceflow-audit-${from}-${to}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const filteredSecurity = (security.data ?? []).filter((e) => !actor || `${e.actor ?? ""} ${e.email ?? ""}`.toLowerCase().includes(actor.toLowerCase()));

  return (
    <div className="mx-auto max-w-7xl p-4 lg:p-6">
      <PageHeader title={t("page.audit.title")} intro={t("page.audit.intro")}
        actions={canExport && <button className="btn-secondary" onClick={() => void exportCsv()}><Download size={16} /> {t("audit.export")}</button>} />

      <div className="mb-3 flex flex-wrap items-center gap-3">
        <div className="flex gap-1 border-b border-slate-200">
          {canInvoices && <Tab active={tab === "invoices"} onClick={() => setTab("invoices")}>{t("audit.invoices")}</Tab>}
          <Tab active={tab === "security"} onClick={() => setTab("security")}>{t("audit.security")}</Tab>
        </div>
        <input className="input ms-auto max-w-xs" placeholder={t("audit.filter")} value={actor} onChange={(e) => setActor(e.target.value)} />
      </div>

      <Card>
        <ErrorNote error={tab === "invoices" ? invoices.error : security.error} />
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-xs uppercase text-slate-500">
              <tr className="border-b border-slate-100">
                <th className="py-2 text-start font-medium">{t("audit.when")}</th>
                <th className="py-2 text-start font-medium">{t("audit.who")}</th>
                <th className="py-2 text-start font-medium">{t("audit.what")}</th>
                {tab === "invoices" && <th className="py-2 text-start font-medium">{t("audit.document")}</th>}
                <th className="py-2 text-start font-medium">{t("audit.details")}</th>
              </tr>
            </thead>
            <tbody>
              {tab === "invoices"
                ? (invoices.data ?? []).map((e) => (
                  <tr key={e.id} className="border-b border-slate-50 align-top">
                    <td className="whitespace-nowrap py-2 text-xs text-slate-500">{dateTime(e.at)}</td>
                    <td className="py-2">{e.actor}</td>
                    <td className="py-2">{e.toStatus ? <StatusBadge status={e.toStatus} /> : <span className="rounded bg-slate-100 px-1.5 text-xs">{e.action}</span>}</td>
                    <td className="py-2"><Link className="text-brand-600 hover:underline" href={`/invoices/${e.documentId}`}>{e.supplierName ?? e.fileName}</Link>
                      {e.invoiceNumber && <span className="block text-xs text-slate-500 ltr-value">{e.invoiceNumber}</span>}</td>
                    <td className="py-2 text-xs text-slate-600">{e.details}</td>
                  </tr>
                ))
                : filteredSecurity.map((e) => (
                  <tr key={e.id} className="border-b border-slate-50 align-top">
                    <td className="whitespace-nowrap py-2 text-xs text-slate-500">{dateTime(e.at)}</td>
                    <td className="py-2"><span dir="ltr">{e.actor ?? e.email ?? "—"}</span>
                      {e.ipAddress && <span className="block text-xs text-slate-400" dir="ltr">{e.ipAddress}</span>}</td>
                    <td className="py-2"><span className={clsx("rounded px-1.5 py-0.5 text-xs",
                      /Failed|Lock|Reuse/.test(e.event) ? "bg-red-50 text-red-700" : "bg-slate-100")}>{e.event}</span>
                      {e.email && e.actor && <span className="block text-xs text-slate-500" dir="ltr">{e.email}</span>}</td>
                    <td className="max-w-md break-words py-2 font-mono text-[11px] text-slate-600" dir="ltr">{e.details}</td>
                  </tr>
                ))}
            </tbody>
          </table>
        </div>
      </Card>
    </div>
  );
}

function Tab({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button onClick={onClick} className={clsx("-mb-px border-b-2 px-3 py-2 text-sm",
      active ? "border-brand-500 font-medium text-brand-600" : "border-transparent text-slate-600 hover:text-slate-900")}>{children}</button>
  );
}
