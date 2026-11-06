"use client";

import { use, useCallback, useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Check, Loader2, RotateCcw, Save, ShieldAlert, X } from "lucide-react";
import clsx from "clsx";
import { api, post, put } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";
import { PROCESSING, emptyInvoice, type InvoiceDetails, type ValidationReport } from "@/lib/types";
import { DocumentViewer, type Highlight } from "@/components/document-viewer";
import { Findings } from "@/components/findings";
import { History } from "@/components/history";
import { InvoiceForm, fromForm, toForm, type FormState } from "@/components/invoice-form";
import { ReasonDialog, StatusBadge, Toast } from "@/components/ui";

export default function InvoicePage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  const { t, money, dateTime } = useI18n();
  const { me, can } = useAuth();
  const qc = useQueryClient();

  const { data, error, isLoading } = useQuery({
    queryKey: ["invoice", id],
    queryFn: () => api<InvoiceDetails>(`/invoices/${id}`),
    // Poll while the pipeline is working on it.
    refetchInterval: (q) => (q.state.data && PROCESSING.includes(q.state.data.document.status) ? 2_000 : false),
  });

  const [form, setForm] = useState<FormState | null>(null);
  const [dirty, setDirty] = useState(false);
  const [invalid, setInvalid] = useState<Set<string>>(new Set());
  const [highlight, setHighlight] = useState<Highlight | null>(null);
  const [tab, setTab] = useState<"data" | "history">("data");
  const [dialog, setDialog] = useState<"override" | "reject" | null>(null);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState<{ message: string; kind: "info" | "error" } | null>(null);

  // (Re)load the form from the server unless the reviewer has unsaved edits.
  const serverVersion = data ? `${data.document.statusChangedAt}|${data.corrections.length}` : "";
  useEffect(() => {
    if (data && !dirty) setForm(toForm(data.invoice ?? emptyInvoice()));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- keyed by server version on purpose
  }, [serverVersion]);

  // Warn before leaving with unsaved changes.
  useEffect(() => {
    if (!dirty) return;
    const handler = (e: BeforeUnloadEvent) => e.preventDefault();
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [dirty]);

  const doc = data?.document;
  const status = doc?.status;
  const editable = status === "NeedsReview" && can("review");
  const isFourEyesBlocked = !!doc && !!me && doc.resolvedBy === me.id;

  const errorFields = useMemo(() => {
    const s = new Set<string>();
    for (const f of data?.validation?.findings ?? []) if (f.severity === "Error" && f.field) s.add(f.field);
    return s;
  }, [data?.validation]);

  const refresh = useCallback(async () => {
    await qc.invalidateQueries({ queryKey: ["invoice", id] });
    await qc.invalidateQueries({ queryKey: ["invoices"] });
  }, [qc, id]);

  const act = useCallback(async (fn: () => Promise<unknown>, done?: string) => {
    setBusy(true);
    try {
      await fn();
      setDirty(false);
      await refresh();
      if (done) setToast({ message: done, kind: "info" });
    } catch (e) {
      setToast({ message: e instanceof Error ? e.message : String(e), kind: "error" });
      throw e;
    } finally {
      setBusy(false);
    }
  }, [refresh]);

  const save = useCallback(async () => {
    if (!form || !editable) return;
    const parsed = fromForm(form);
    if ("invalid" in parsed) {
      setInvalid(new Set(parsed.invalid));
      setToast({ message: t("inv.invalidNumber"), kind: "error" });
      return;
    }
    setInvalid(new Set());
    await act(async () => {
      const report = await put<ValidationReport>(`/invoices/${id}/data`, parsed.invoice);
      setToast({ message: `${t("inv.findings")}: ${report.outcome}`, kind: report.outcome === "NeedsReview" ? "error" : "info" });
    }).catch(() => undefined);
  }, [form, editable, act, id, t]);

  // Ctrl/Cmd+S saves.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
        e.preventDefault();
        void save();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [save]);

  const jumpTo = (field: string) => {
    setTab("data");
    const el = document.getElementById(`f-${field}`) ?? document.getElementById(`f-${field}.description`);
    el?.focus();
    el?.scrollIntoView({ block: "center", behavior: "smooth" });
  };

  if (isLoading) return <p className="p-6 text-slate-500">{t("common.loading")}</p>;
  if (error || !data || !doc) return <p className="p-6 text-red-700">{t("common.error")}: {(error as Error | null)?.message}</p>;

  const processing = PROCESSING.includes(doc.status);
  const extraction = data.extraction;

  return (
    <div className="grid h-[calc(100vh-3rem)] grid-cols-1 lg:grid-cols-[minmax(0,5fr)_minmax(0,6fr)]">
      {/* ---------------- document ---------------- */}
      <section className="hidden h-full min-h-0 border-e border-slate-200 lg:block">
        <DocumentViewer documentId={id} highlight={highlight} />
      </section>

      {/* ---------------- review panel ---------------- */}
      <section className="flex min-h-0 flex-col">
        <div className="border-b border-slate-200 bg-white px-4 py-3">
          <Link href="/queue" className="mb-2 inline-flex items-center gap-1 text-xs text-slate-500 hover:text-slate-800">
            <ArrowLeft size={12} className="rtl:rotate-180" /> {t("inv.back")}
          </Link>
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="text-lg font-semibold">{doc.supplierName ?? doc.fileName}</h1>
            <StatusBadge status={doc.status} />
            {dirty && <span className="text-xs text-amber-700">● {t("inv.unsaved")}</span>}
            <span className="ms-auto text-lg font-semibold ltr-value tabular-nums">{money(doc.total, doc.currency)}</span>
          </div>
          <div className="mt-1 flex flex-wrap gap-x-4 text-xs text-slate-500">
            <span>{doc.fileName}</span>
            <span>{dateTime(doc.receivedAt)} · {doc.submittedBy}</span>
            {extraction && (
              <span>
                {t("inv.extractedBy")} {extraction.usage.extractor}
                {extraction.usage.model && extraction.usage.model !== "none" ? ` (${extraction.usage.model})` : ""} · {t("inv.cost")} ${extraction.usage.costUsd.toFixed(4)}
              </span>
            )}
            {doc.resolvedBy && <span>{t("inv.resolvedBy")}: {doc.resolvedBy}</span>}
            {doc.approvedBy && <span>{t("inv.approvedBy")}: {doc.approvedBy}</span>}
            {doc.erpReference && <span>{t("inv.erp")}: <span className="ltr-value font-mono">{doc.erpReference}</span></span>}
          </div>

          {/* actions */}
          <div className="mt-3 flex flex-wrap items-center gap-2">
            {status === "NeedsReview" && can("review") && (
              <button className="btn-primary" disabled={busy || !dirty} onClick={() => void save()}>
                {busy ? <Loader2 size={16} className="animate-spin" /> : <Save size={16} />} {busy ? t("inv.saving") : t("inv.save")}
              </button>
            )}
            {status === "PendingApproval" && can("approve") && (
              <button
                className="btn-primary"
                disabled={busy || isFourEyesBlocked}
                title={isFourEyesBlocked ? t("inv.fourEyes") : undefined}
                onClick={() => void act(() => post(`/invoices/${id}/approve`)).catch(() => undefined)}
              >
                <Check size={16} /> {t("inv.approve")}
              </button>
            )}
            {status === "NeedsReview" && can("override") && (
              <button className="btn-secondary" disabled={busy || dirty} onClick={() => setDialog("override")}>
                <ShieldAlert size={16} /> {t("inv.override")}
              </button>
            )}
            {(status === "NeedsReview" || status === "PendingApproval") && can("reject") && (
              <button className="btn-danger" disabled={busy} onClick={() => setDialog("reject")}>
                <X size={16} /> {t("inv.reject")}
              </button>
            )}
            {status === "PostingFailed" && can("retry") && (
              <button className="btn-secondary" disabled={busy} onClick={() => void act(() => post(`/invoices/${id}/retry-posting`)).catch(() => undefined)}>
                <RotateCcw size={16} /> {t("inv.retry")}
              </button>
            )}
            {status === "PendingApproval" && isFourEyesBlocked && <span className="text-xs text-amber-700">{t("inv.fourEyes")}</span>}
          </div>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto px-4 py-3">
          {processing && (
            <p className="mb-3 flex items-center gap-2 rounded bg-sky-50 px-3 py-2 text-sm text-sky-800">
              <Loader2 size={16} className="animate-spin" /> {t("inv.processing")}
            </p>
          )}
          {!editable && !processing && status === "NeedsReview" && (
            <p className="mb-3 rounded bg-slate-100 px-3 py-2 text-sm text-slate-600">{t("inv.readOnly")}</p>
          )}

          {data.validation && (
            <div className="card mb-4 p-3">
              <h2 className="mb-2 text-sm font-semibold text-slate-700">{t("inv.findings")}</h2>
              <Findings findings={data.validation.findings} onSelect={jumpTo} />
            </div>
          )}

          <div className="mb-3 flex gap-1 border-b border-slate-200 text-sm">
            {(["data", "history"] as const).map((x) => (
              <button
                key={x}
                className={clsx("-mb-px border-b-2 px-3 py-1.5", tab === x ? "border-brand-500 font-medium text-brand-600" : "border-transparent text-slate-600")}
                onClick={() => setTab(x)}
              >
                {t(x === "data" ? "inv.tab.data" : "inv.tab.history")}
              </button>
            ))}
          </div>

          {tab === "data" && form && (
            <InvoiceForm
              form={form}
              onChange={(f) => { setForm(f); setDirty(true); }}
              editable={editable && !busy}
              errorFields={errorFields}
              invalidFields={invalid}
              evidence={extraction?.evidence ?? []}
              lineMatches={dirty ? [] : data.validation?.lineMatches ?? []}
              onHighlight={setHighlight}
            />
          )}
          {tab === "history" && <History audit={data.audit} corrections={data.corrections} />}
        </div>
      </section>

      {dialog && (
        <ReasonDialog
          title={t(dialog === "override" ? "inv.overrideTitle" : "inv.rejectTitle")}
          hint={dialog === "override" ? t("inv.overrideHint") : undefined}
          confirmLabel={t("inv.confirm")}
          danger={dialog === "reject"}
          onClose={() => setDialog(null)}
          onConfirm={(reason) => act(() => post(`/invoices/${id}/${dialog}`, { reason }))}
        />
      )}
      {toast && <Toast message={toast.message} kind={toast.kind} onDone={() => setToast(null)} />}
    </div>
  );
}
