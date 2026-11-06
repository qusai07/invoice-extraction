"use client";

import { Suspense } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { RefreshCw } from "lucide-react";
import clsx from "clsx";
import { api } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useI18n, type I18nKey } from "@/lib/i18n";
import type { InvoiceStatus, InvoiceSummary } from "@/lib/types";
import { PageHeader, RuleChip, StatusBadge } from "@/components/ui";
import { UploadButton } from "@/components/upload";

const TABS: { key: string; status: InvoiceStatus | null; label: I18nKey }[] = [
  { key: "review", status: "NeedsReview", label: "queue.tab.review" },
  { key: "approval", status: "PendingApproval", label: "queue.tab.approval" },
  { key: "failed", status: "PostingFailed", label: "queue.tab.failed" },
  { key: "all", status: null, label: "queue.tab.all" },
];

function useInvoices(status: InvoiceStatus | null) {
  return useQuery({
    queryKey: ["invoices", status ?? "all"],
    queryFn: () => api<InvoiceSummary[]>(`/invoices${status ? `?status=${status}` : "?take=100"}`),
    refetchInterval: 5_000,
  });
}

function QueueInner() {
  const { t, money, dateTime } = useI18n();
  const { can } = useAuth();
  const router = useRouter();
  const params = useSearchParams();
  const tab = TABS.find((x) => x.key === params.get("tab")) ?? TABS[0]!;

  const current = useInvoices(tab.status);
  // Counts for the tab badges (cheap: same queries, cached).
  const review = useInvoices("NeedsReview");
  const approval = useInvoices("PendingApproval");
  const failed = useInvoices("PostingFailed");
  const counts: Record<string, number | undefined> = {
    review: review.data?.length, approval: approval.data?.length, failed: failed.data?.length,
  };

  return (
    <div className="mx-auto max-w-7xl p-4">
      <PageHeader
        title={t("page.invoices.title")}
        intro={t("page.invoices.intro")}
        actions={<>
          <button className="btn-secondary px-2" onClick={() => void current.refetch()} title={t("common.refresh")} aria-label={t("common.refresh")}>
            <RefreshCw size={16} className={clsx(current.isFetching && "animate-spin")} />
          </button>
          {can("submit") && <UploadButton />}
        </>}
      />

      <div className="mb-3 flex gap-1 border-b border-slate-200">
        {TABS.map((x) => (
          <Link
            key={x.key}
            href={`/queue?tab=${x.key}`}
            className={clsx(
              "-mb-px border-b-2 px-3 py-2 text-sm",
              x.key === tab.key ? "border-brand-500 font-medium text-brand-600" : "border-transparent text-slate-600 hover:text-slate-900",
            )}
          >
            {t(x.label)}
            {counts[x.key] ? <span className="ms-1.5 rounded-full bg-slate-200 px-1.5 text-xs">{counts[x.key]}</span> : null}
          </Link>
        ))}
      </div>

      {current.isLoading ? (
        <p className="p-6 text-slate-500">{t("common.loading")}</p>
      ) : current.error ? (
        <p className="p-6 text-red-700">{t("common.error")}: {(current.error as Error).message}</p>
      ) : (current.data ?? []).length === 0 ? (
        <p className="p-10 text-center text-slate-500">{t("queue.empty")}</p>
      ) : (
        <div className="card overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-slate-50 text-start text-xs uppercase text-slate-500">
              <tr>
                <Th>{t("queue.received")}</Th>
                <Th>{t("queue.supplier")}</Th>
                <Th>{t("queue.invoice")}</Th>
                <Th end>{t("queue.total")}</Th>
                <Th>{t("queue.status")}</Th>
                <Th>{t("queue.issues")}</Th>
                <Th>{t("queue.by")}</Th>
              </tr>
            </thead>
            <tbody>
              {current.data!.map((inv) => (
                <tr
                  key={inv.id}
                  tabIndex={0}
                  className="cursor-pointer border-t border-slate-100 hover:bg-brand-50 focus:bg-brand-50 focus:outline-none"
                  onClick={() => router.push(`/invoices/${inv.id}`)}
                  onKeyDown={(e) => e.key === "Enter" && router.push(`/invoices/${inv.id}`)}
                >
                  <Td className="whitespace-nowrap text-slate-500">{dateTime(inv.receivedAt)}</Td>
                  <Td>
                    <div className="font-medium">{inv.supplierName ?? "—"}</div>
                    <div className="text-xs text-slate-500">{inv.fileName}</div>
                  </Td>
                  <Td><span className="ltr-value">{inv.invoiceNumber ?? "—"}</span></Td>
                  <Td end className="whitespace-nowrap"><span className="ltr-value">{money(inv.total, inv.currency)}</span></Td>
                  <Td><StatusBadge status={inv.status} /></Td>
                  <Td>
                    <div className="flex flex-wrap gap-1">
                      {inv.errorCodes.map((c) => <RuleChip key={c} code={c} />)}
                    </div>
                  </Td>
                  <Td className="text-slate-500">{inv.submittedBy}</Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function Th({ children, end }: { children: React.ReactNode; end?: boolean }) {
  return <th className={clsx("px-3 py-2 font-medium", end ? "text-end" : "text-start")}>{children}</th>;
}

function Td({ children, end, className }: { children: React.ReactNode; end?: boolean; className?: string }) {
  return <td className={clsx("px-3 py-2 align-top", end && "text-end", className)}>{children}</td>;
}

export default function QueuePage() {
  return (
    <Suspense>
      <QueueInner />
    </Suspense>
  );
}
