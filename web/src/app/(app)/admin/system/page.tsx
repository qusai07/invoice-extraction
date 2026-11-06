"use client";

import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Clock, FileText, RotateCcw, Users } from "lucide-react";
import { api, post } from "@/lib/api";
import { useI18n } from "@/lib/i18n";
import { Card, ErrorNote, PageHeader, Toast } from "@/components/ui";

interface SystemView {
  jobs: { status: string; step: string; count: number }[];
  oldestPendingSeconds: number;
  failed: { id: number; documentId: string; fileName: string; step: string; attempts: number; lastError: string | null; completedAt: string | null }[];
  documents: number;
  activeUsers: number;
}

export default function SystemPage() {
  const { t, dateTime } = useI18n();
  const qc = useQueryClient();
  const q = useQuery({ queryKey: ["admin", "system"], queryFn: () => api<SystemView>("/admin/system"), refetchInterval: 10_000 });
  const [toast, setToast] = useState<{ message: string; kind: "info" | "error" } | null>(null);

  const retry = async (id: number) => {
    try {
      await post(`/admin/system/jobs/${id}/retry`);
      await qc.invalidateQueries({ queryKey: ["admin", "system"] });
      setToast({ message: t("adm.saved"), kind: "info" });
    } catch (e) {
      setToast({ message: e instanceof Error ? e.message : String(e), kind: "error" });
    }
  };

  const d = q.data;
  return (
    <div className="mx-auto max-w-7xl p-4 lg:p-6">
      <PageHeader title={t("page.system.title")} intro={t("page.system.intro")} />
      <ErrorNote error={q.error} />
      {d && (
        <>
          <div className="mb-4 grid gap-3 sm:grid-cols-3">
            <Tile icon={FileText} label={t("sys.documents")} value={d.documents.toString()} />
            <Tile icon={Users} label={t("sys.users")} value={d.activeUsers.toString()} />
            <Tile icon={Clock} label={t("sys.oldest")} value={d.oldestPendingSeconds > 0 ? `${d.oldestPendingSeconds} ${t("sys.seconds")}` : "—"} />
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            <Card title={t("sys.jobs")}>
              <table className="w-full text-sm">
                <thead className="text-xs text-slate-500">
                  <tr><th className="py-1 text-start font-normal">{t("sys.step")}</th><th className="py-1 text-start font-normal">{t("sys.status")}</th><th className="py-1 text-end font-normal">{t("sys.count")}</th></tr>
                </thead>
                <tbody>
                  {d.jobs.map((j) => (
                    <tr key={`${j.step}-${j.status}`} className="border-t border-slate-100">
                      <td className="py-1.5">{j.step}</td>
                      <td className="py-1.5">{j.status}</td>
                      <td className="py-1.5 text-end tabular-nums">{j.count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Card>

            <Card title={t("sys.failed")}>
              {d.failed.length === 0 ? (
                <p className="flex items-center gap-2 text-sm text-emerald-700"><CheckCircle2 size={16} /> {t("sys.noFailed")}</p>
              ) : (
                <ul className="divide-y divide-slate-100">
                  {d.failed.map((f) => (
                    <li key={f.id} className="flex items-start gap-3 py-2 text-sm">
                      <div className="min-w-0 flex-1">
                        <p className="font-medium">{f.fileName} · {f.step}</p>
                        <p className="break-words text-xs text-red-700" dir="ltr">{f.lastError}</p>
                        <p className="text-xs text-slate-500">{dateTime(f.completedAt)}</p>
                      </div>
                      <button className="btn-secondary px-2 py-1 text-xs" onClick={() => void retry(f.id)}><RotateCcw size={14} /> {t("sys.retry")}</button>
                    </li>
                  ))}
                </ul>
              )}
            </Card>
          </div>
        </>
      )}
      {toast && <Toast message={toast.message} kind={toast.kind} onDone={() => setToast(null)} />}
    </div>
  );
}

function Tile({ icon: Icon, label, value }: { icon: typeof Clock; label: string; value: string }) {
  return (
    <div className="card p-4">
      <p className="flex items-center gap-1.5 text-xs font-medium text-slate-500"><Icon size={14} /> {label}</p>
      <p className="mt-1 text-2xl font-semibold tabular-nums">{value}</p>
    </div>
  );
}
