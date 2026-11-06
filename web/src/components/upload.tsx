"use client";

import { useRef, useState } from "react";
import { Upload } from "lucide-react";
import clsx from "clsx";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "@/lib/api";
import { useI18n } from "@/lib/i18n";
import { Toast } from "./ui";

const ACCEPT = ["application/pdf", "image/png", "image/jpeg"];

/** Upload button + drop zone. Files are sent one by one; each returns 202 and is processed in the background. */
export function UploadButton() {
  const { t } = useI18n();
  const qc = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [busy, setBusy] = useState(false);
  const [toast, setToast] = useState<{ message: string; kind: "info" | "error" } | null>(null);

  async function send(files: FileList | File[]) {
    const list = Array.from(files).filter((f) => ACCEPT.includes(f.type));
    if (list.length === 0) return;
    setBusy(true);
    let ok = 0;
    const errors: string[] = [];
    for (const file of list) {
      const form = new FormData();
      form.append("file", file);
      try {
        await api("/invoices", { method: "POST", body: form });
        ok++;
      } catch (e) {
        errors.push(`${file.name}: ${e instanceof Error ? e.message : String(e)}`);
      }
    }
    setBusy(false);
    await qc.invalidateQueries({ queryKey: ["invoices"] });
    setToast(errors.length > 0
      ? { message: `${t("upload.failed")}: ${errors.join("; ")}`, kind: "error" }
      : { message: `${ok} ${t("upload.done")}`, kind: "info" });
  }

  return (
    <>
      <div
        onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => { e.preventDefault(); setDragging(false); void send(e.dataTransfer.files); }}
        className={clsx("flex items-center gap-3 rounded-md border border-dashed px-3 py-1.5", dragging ? "border-brand-500 bg-brand-50" : "border-slate-300")}
      >
        <span className="hidden text-xs text-slate-500 md:inline">{t("upload.drop")}</span>
        <button className="btn-primary" disabled={busy} onClick={() => input.current?.click()}>
          <Upload size={16} /> {t("upload.button")}
        </button>
        <input
          ref={input}
          type="file"
          accept={ACCEPT.join(",")}
          multiple
          hidden
          onChange={(e) => { if (e.target.files) void send(e.target.files); e.target.value = ""; }}
        />
      </div>
      {toast && <Toast message={toast.message} kind={toast.kind} onDone={() => setToast(null)} />}
    </>
  );
}
