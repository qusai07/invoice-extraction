"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { AlertTriangle, ArrowRight, CheckCircle2, CircleCheckBig, FileSearch, Gauge, Hourglass, Inbox, Send, Upload, Wallet } from "lucide-react";
import { api } from "@/lib/api";
import { personLabel } from "@/lib/people";
import { useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";
import type { InvoiceStatus } from "@/lib/types";
import { Card, PageHeader, StatusBadge } from "@/components/ui";
import { FlowChart, type DayPoint } from "@/components/flow-chart";

interface Dashboard {
  receivedToday: number; received7Days: number; received30Days: number; decided30Days: number; touchless30Days: number;
  needsReview: number; pendingApproval: number; postingFailed: number; processing: number; posted: number;
  postedValue30Days: { currency: string; total: number }[];
  approvableByMe: number; myApprovalLimit: number | null; myApprovalUnlimited: boolean;
  series: DayPoint[];
  activity: { at: string; actor: string; actorName: string | null; action: string; toStatus: InvoiceStatus | null; documentId: string; fileName: string; supplierName: string | null }[];
}

export default function DashboardPage() {
  const { t, lang, money, dateTime } = useI18n();
  const { me, can } = useAuth();
  const { data, isLoading } = useQuery({ queryKey: ["dashboard"], queryFn: () => api<Dashboard>("/dashboard"), refetchInterval: 15_000 });

  if (isLoading || !data) return <div className="p-6 text-slate-500">{t("common.loading")}</div>;

  const touchlessPct = data.decided30Days > 0 ? Math.round((data.touchless30Days / data.decided30Days) * 100) : null;
  const attention = [
    can("review") && data.needsReview > 0 && { href: "/queue?tab=review", icon: FileSearch, tone: "amber", count: data.needsReview, title: t("dash.toReview"), hint: t("dash.toReviewHint") },
    can("approve") && data.approvableByMe > 0 && { href: "/queue?tab=approval", icon: CircleCheckBig, tone: "violet", count: data.approvableByMe, title: t("dash.toApprove"), hint: t("dash.toApproveHint") },
    can("retry") && data.postingFailed > 0 && { href: "/queue?tab=failed", icon: AlertTriangle, tone: "red", count: data.postingFailed, title: t("dash.failed"), hint: t("dash.failedHint") },
  ].filter(Boolean) as { href: string; icon: typeof Inbox; tone: "amber" | "violet" | "red"; count: number; title: string; hint: string }[];

  return (
    <div className="mx-auto max-w-7xl p-4 lg:p-6">
      <PageHeader
        title={`${t("dash.hello")}${lang === "ar" ? "، " : ", "}${me?.name ?? ""}`}
        intro={t("page.dashboard.intro")}
        actions={can("submit") && <Link href="/upload" className="btn-primary"><Upload size={16} /> {t("nav.upload")}</Link>}
      />

      {/* What needs me */}
      <h2 className="mb-2 text-sm font-semibold text-slate-700">{t("dash.attention")}</h2>
      {attention.length === 0 ? (
        <p className="card mb-6 flex items-center gap-2 p-4 text-sm text-emerald-700"><CheckCircle2 size={18} /> {t("dash.allClear")}</p>
      ) : (
        <div className="mb-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {attention.map((a) => <AttentionCard key={a.href} {...a} />)}
        </div>
      )}

      {/* KPIs */}
      <div className="mb-6 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Stat icon={Inbox} label={t("dash.kpi.received")} value={data.received7Days.toString()} sub={`${data.receivedToday} ${t("dash.kpi.today")}`} />
        <Stat icon={Gauge} label={t("dash.kpi.touchless")} value={touchlessPct === null ? "—" : `${touchlessPct}%`} sub={t("dash.kpi.touchlessHint")} />
        <Stat icon={Send} label={t("dash.kpi.posted")} value={data.postedValue30Days.length ? data.postedValue30Days.map((p) => money(p.total, p.currency)).join(" · ") : money(0)} sub={`${data.posted}`} />
        <Stat icon={Hourglass} label={t("dash.kpi.inProgress")} value={data.processing.toString()} />
      </div>

      <div className="grid gap-4 xl:grid-cols-3">
        <Card title={t("dash.chart.title")} className="xl:col-span-2">
          <FlowChart data={data.series} />
        </Card>

        <div className="space-y-4">
          {can("approve") && (
            <Card title={t("dash.limit")}>
              <p className="flex items-center gap-2 text-lg font-semibold">
                <Wallet size={18} className="text-slate-400" />
                <span className="ltr-value">{data.myApprovalUnlimited ? t("dash.unlimited") : data.myApprovalLimit != null ? money(data.myApprovalLimit, "AED") : t("dash.noLimit")}</span>
              </p>
            </Card>
          )}
          <Card title={t("dash.activity")}>
            {data.activity.length === 0 ? (
              <p className="text-sm text-slate-500">{t("dash.activity.empty")}</p>
            ) : (
              <ul className="space-y-2.5">
                {data.activity.map((a, i) => (
                  <li key={i} className="text-sm">
                    <Link href={`/invoices/${a.documentId}`} className="flex flex-wrap items-center gap-1.5 hover:underline">
                      <span className="font-medium">{a.supplierName ?? a.fileName}</span>
                      {a.toStatus && <StatusBadge status={a.toStatus} />}
                    </Link>
                    <p className="text-xs text-slate-500"><span dir="auto">{personLabel(a.actor, a.actorName, t)}</span> · {dateTime(a.at)}</p>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>
      </div>

      {data.received30Days === 0 && (
        <Card title={t("dash.how.title")} className="mt-4">
          <ol className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            {(["dash.how.1", "dash.how.2", "dash.how.3", "dash.how.4"] as const).map((k, i) => (
              <li key={k} className="flex gap-3 rounded-md bg-slate-50 p-3 text-sm text-slate-700">
                <span className="flex h-6 w-6 shrink-0 items-center justify-center rounded-full bg-brand-500 text-xs font-semibold text-white">{i + 1}</span>
                {t(k)}
              </li>
            ))}
          </ol>
        </Card>
      )}
    </div>
  );
}

const TONE = {
  amber: "border-amber-200 bg-amber-50 text-amber-900",
  violet: "border-violet-200 bg-violet-50 text-violet-900",
  red: "border-red-200 bg-red-50 text-red-900",
};

function AttentionCard({ href, icon: Icon, tone, count, title, hint }: { href: string; icon: typeof Inbox; tone: keyof typeof TONE; count: number; title: string; hint: string }) {
  const { t } = useI18n();
  return (
    <Link href={href} className={`group flex items-start gap-3 rounded-lg border p-4 transition-shadow hover:shadow-sm ${TONE[tone]}`}>
      <Icon size={20} className="mt-0.5 shrink-0" />
      <div className="min-w-0 flex-1">
        <p className="flex items-baseline gap-2"><span className="text-2xl font-semibold tabular-nums">{count}</span> <span className="font-medium">{title}</span></p>
        <p className="mt-0.5 text-sm opacity-80">{hint}</p>
      </div>
      <span className="flex items-center gap-1 self-center text-sm font-medium">{t("dash.open")} <ArrowRight size={14} className="rtl:rotate-180" /></span>
    </Link>
  );
}

function Stat({ icon: Icon, label, value, sub }: { icon: typeof Inbox; label: string; value: string; sub?: string }) {
  return (
    <div className="card p-4">
      <p className="flex items-center gap-1.5 text-xs font-medium text-slate-500"><Icon size={14} /> {label}</p>
      <p className="mt-1 text-2xl font-semibold tabular-nums text-slate-900 ltr-value">{value}</p>
      {sub && <p className="mt-0.5 text-xs text-slate-500">{sub}</p>}
    </div>
  );
}
