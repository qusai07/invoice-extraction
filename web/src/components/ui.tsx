"use client";

import clsx from "clsx";
import { useEffect, useRef, useState } from "react";
import type { InvoiceStatus, Severity } from "@/lib/types";
import { useI18n } from "@/lib/i18n";

const STATUS_STYLE: Partial<Record<InvoiceStatus, string>> = {
  NeedsReview: "bg-amber-100 text-amber-800",
  PendingApproval: "bg-violet-100 text-violet-800",
  Posted: "bg-emerald-100 text-emerald-800",
  Approved: "bg-emerald-50 text-emerald-700",
  PostingFailed: "bg-red-100 text-red-800",
  Rejected: "bg-slate-200 text-slate-700",
  Duplicate: "bg-slate-200 text-slate-700",
};

export function StatusBadge({ status }: { status: InvoiceStatus }) {
  const { status: label } = useI18n();
  return (
    <span className={clsx("inline-block whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-medium", STATUS_STYLE[status] ?? "bg-sky-100 text-sky-800")}>
      {label(status)}
    </span>
  );
}

export function RuleChip({ code, severity = "Error" }: { code: string; severity?: Severity }) {
  const { rule } = useI18n();
  return (
    <span
      title={code}
      className={clsx(
        "inline-flex items-center gap-1 whitespace-nowrap rounded px-1.5 py-0.5 text-xs",
        severity === "Error" && "bg-red-50 text-red-700 ring-1 ring-red-200",
        severity === "Warning" && "bg-amber-50 text-amber-800 ring-1 ring-amber-200",
        severity === "Info" && "bg-sky-50 text-sky-800 ring-1 ring-sky-200",
      )}
    >
      {/* Name first; the code is a small reference (it looked like a PO number when shown first). */}
      <span className="font-medium">{rule(code)}</span>
      <span className="ltr-value font-mono text-[10px] opacity-60">{code}</span>
    </span>
  );
}

export function Toast({ message, kind = "info", onDone }: { message: string; kind?: "info" | "error"; onDone: () => void }) {
  useEffect(() => {
    const id = setTimeout(onDone, kind === "error" ? 8000 : 5000); // problems need longer to read
    return () => clearTimeout(id);
  }, [onDone, kind]);
  return (
    <div
      role="status"
      className={clsx(
        "fixed bottom-4 end-4 z-50 max-w-[min(28rem,calc(100vw-2rem))] rounded-md px-4 py-2 text-sm shadow-lg",
        kind === "error" ? "bg-red-700 text-white" : "bg-slate-800 text-white",
      )}
    >
      {message}
    </div>
  );
}

/** Title + one-line explanation at the top of every page, so first-time users know what the page is for. */
export function PageHeader({ title, intro, actions }: { title: string; intro?: string; actions?: React.ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-start gap-3">
      <div className="min-w-0 flex-1">
        <h1 className="text-xl font-semibold text-slate-900">{title}</h1>
        {intro && <p className="mt-0.5 max-w-3xl text-sm text-slate-500">{intro}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}

export function Card({ title, children, className, actions }: { title?: string; children: React.ReactNode; className?: string; actions?: React.ReactNode }) {
  return (
    <section className={clsx("card p-4", className)}>
      {(title || actions) && (
        <div className="mb-3 flex items-center gap-2">
          {title && <h2 className="text-sm font-semibold text-slate-700">{title}</h2>}
          {actions && <div className="ms-auto">{actions}</div>}
        </div>
      )}
      {children}
    </section>
  );
}

/** Generic dialog. Closes on Escape and backdrop click. */
export function Modal({ title, children, onClose, footer, wide }: {
  title: string; children: React.ReactNode; onClose: () => void; footer?: React.ReactNode; wide?: boolean;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/30 p-4" onClick={onClose}>
      <div role="dialog" aria-modal="true" aria-label={title}
        className={clsx("card flex max-h-[90vh] w-full flex-col", wide ? "max-w-2xl" : "max-w-md")} onClick={(e) => e.stopPropagation()}>
        <h2 className="border-b border-slate-100 px-5 py-3 font-semibold">{title}</h2>
        <div className="overflow-y-auto px-5 py-4">{children}</div>
        {footer && <div className="flex justify-end gap-2 border-t border-slate-100 px-5 py-3">{footer}</div>}
      </div>
    </div>
  );
}

export function ErrorNote({ error }: { error: unknown }) {
  if (!error) return null;
  return <p role="alert" className="rounded bg-red-50 px-3 py-2 text-sm text-red-700">{error instanceof Error ? error.message : String(error)}</p>;
}

/** Modal asking for a reason (override / reject). Enforces the API's minimum length client-side too. */
export function ReasonDialog({
  title, hint, confirmLabel, danger, onConfirm, onClose,
}: {
  title: string;
  hint?: string;
  confirmLabel: string;
  danger?: boolean;
  onConfirm: (reason: string) => Promise<void>;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLTextAreaElement>(null);

  useEffect(() => ref.current?.focus(), []);

  const valid = reason.trim().length >= 10;

  return (
    <div className="fixed inset-0 z-40 flex items-center justify-center bg-black/30 p-4" onClick={onClose}>
      <div role="dialog" aria-modal="true" aria-label={title} className="card w-full max-w-md p-5" onClick={(e) => e.stopPropagation()}>
        <h2 className="mb-1 font-semibold">{title}</h2>
        {hint && <p className="mb-3 text-sm text-slate-600">{hint}</p>}
        <label className="mb-1 block text-sm text-slate-600" htmlFor="reason">{t("inv.reason")}</label>
        <textarea
          id="reason"
          ref={ref}
          className="input h-24"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          onKeyDown={(e) => e.key === "Escape" && onClose()}
        />
        {error && <p className="mt-2 text-sm text-red-700">{error}</p>}
        <div className="mt-4 flex justify-end gap-2">
          <button className="btn-secondary" onClick={onClose} disabled={busy}>{t("common.cancel")}</button>
          <button
            className={danger ? "btn-danger" : "btn-primary"}
            disabled={!valid || busy}
            onClick={async () => {
              setBusy(true);
              setError(null);
              try {
                await onConfirm(reason.trim());
                onClose();
              } catch (e) {
                setError(e instanceof Error ? e.message : String(e));
              } finally {
                setBusy(false);
              }
            }}
          >
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
