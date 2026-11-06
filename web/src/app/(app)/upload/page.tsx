"use client";

import { useRef, useState } from "react";
import Link from "next/link";
import { useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, FileUp, Loader2, ShieldCheck, Sparkles, XCircle } from "lucide-react";
import clsx from "clsx";
import { api } from "@/lib/api";
import { useI18n } from "@/lib/i18n";
import { Card, PageHeader } from "@/components/ui";

const ACCEPT = ["application/pdf", "image/png", "image/jpeg"];

interface Item { key: string; name: string; state: "sending" | "ok" | "error"; id?: string; error?: string }

export default function UploadPage() {
  const { t } = useI18n();
  const qc = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);
  const [items, setItems] = useState<Item[]>([]);

  async function send(files: FileList | File[]) {
    for (const file of Array.from(files)) {
      const key = `${file.name}-${Date.now()}-${Math.random()}`;
      setItems((list) => [{ key, name: file.name, state: "sending" }, ...list]);
      const form = new FormData();
      form.append("file", file);
      try {
        const r = await api<{ id: string }>("/invoices", { method: "POST", body: form });
        setItems((list) => list.map((i) => (i.key === key ? { ...i, state: "ok", id: r.id } : i)));
      } catch (e) {
        setItems((list) => list.map((i) => (i.key === key ? { ...i, state: "error", error: e instanceof Error ? e.message : String(e) } : i)));
      }
    }
    await qc.invalidateQueries({ queryKey: ["invoices"] });
    await qc.invalidateQueries({ queryKey: ["dashboard"] });
  }

  return (
    <div className="mx-auto max-w-5xl p-4 lg:p-6">
      <PageHeader title={t("page.upload.title")} intro={t("page.upload.intro")} />

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="lg:col-span-2">
          <div
            onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => { e.preventDefault(); setDragging(false); void send(e.dataTransfer.files); }}
            className={clsx("flex flex-col items-center justify-center rounded-xl border-2 border-dashed bg-white px-6 py-14 text-center transition-colors",
              dragging ? "border-brand-500 bg-brand-50" : "border-slate-300")}
          >
            <FileUp size={40} className="mb-3 text-brand-500" />
            <p className="text-base font-medium text-slate-800">{t("up.drop")}</p>
            <p className="my-2 text-sm text-slate-400">{t("up.or")}</p>
            <button className="btn-primary" onClick={() => input.current?.click()}>{t("up.choose")}</button>
            <p className="mt-3 text-xs text-slate-500">{t("up.limits")}</p>
            <input ref={input} type="file" accept={ACCEPT.join(",")} multiple hidden
              onChange={(e) => { if (e.target.files) void send(e.target.files); e.target.value = ""; }} />
          </div>

          {items.length > 0 && (
            <Card title={t("up.uploaded")} className="mt-4">
              <ul className="divide-y divide-slate-100">
                {items.map((i) => (
                  <li key={i.key} className="flex items-center gap-3 py-2 text-sm">
                    {i.state === "sending" ? <Loader2 size={16} className="animate-spin text-slate-400" />
                      : i.state === "ok" ? <CheckCircle2 size={16} className="text-emerald-600" />
                      : <XCircle size={16} className="text-red-600" />}
                    <span className="min-w-0 flex-1 truncate">{i.name}</span>
                    <span className={clsx("text-xs", i.state === "error" ? "text-red-700" : "text-slate-500")}>
                      {i.state === "error" ? i.error : t(i.state === "ok" ? "up.status.ok" : "up.status.sending")}
                    </span>
                    {i.id && <Link href={`/invoices/${i.id}`} className="text-brand-600 hover:underline">{t("up.view")}</Link>}
                  </li>
                ))}
              </ul>
            </Card>
          )}
        </div>

        <Card title={t("up.next")}>
          <ol className="space-y-4">
            {[
              { icon: ShieldCheck, k: "up.next.1" as const },
              { icon: Sparkles, k: "up.next.2" as const },
              { icon: CheckCircle2, k: "up.next.3" as const },
            ].map(({ icon: Icon, k }) => (
              <li key={k} className="flex gap-3 text-sm text-slate-700">
                <Icon size={18} className="mt-0.5 shrink-0 text-brand-500" /> {t(k)}
              </li>
            ))}
          </ol>
        </Card>
      </div>
    </div>
  );
}
