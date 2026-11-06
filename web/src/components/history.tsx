"use client";

import { ArrowRight } from "lucide-react";
import type { AuditEvent, Correction } from "@/lib/types";
import { useI18n } from "@/lib/i18n";
import { StatusBadge } from "./ui";

export function History({ audit, corrections }: { audit: AuditEvent[]; corrections: Correction[] }) {
  const { t, dateTime } = useI18n();

  return (
    <div className="space-y-6">
      <section>
        <h3 className="mb-2 text-sm font-semibold text-slate-700">{t("inv.corrections")}</h3>
        {corrections.length === 0 ? (
          <p className="text-sm text-slate-500">{t("inv.noCorrections")}</p>
        ) : (
          <table className="w-full text-sm">
            <tbody>
              {corrections.map((c, i) => (
                <tr key={i} className="border-t border-slate-100">
                  <td className="py-1 pe-2 font-mono text-xs ltr-value">{c.field}</td>
                  <td className="py-1 pe-2">
                    <span className="ltr-value text-red-700 line-through">{c.oldValue ?? "∅"}</span>{" "}
                    <ArrowRight size={12} className="inline rtl:rotate-180" />{" "}
                    <span className="ltr-value text-emerald-700">{c.newValue ?? "∅"}</span>
                  </td>
                  <td className="py-1 pe-2 text-slate-600">{c.userId}</td>
                  <td className="whitespace-nowrap py-1 text-xs text-slate-500">{dateTime(c.at)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section>
        <h3 className="mb-2 text-sm font-semibold text-slate-700">{t("inv.audit")}</h3>
        <ol className="relative space-y-3 border-s border-slate-200 ps-4">
          {audit.map((e, i) => (
            <li key={i} className="text-sm">
              <span className="absolute -start-1.5 mt-1.5 h-3 w-3 rounded-full border border-white bg-slate-300" />
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{e.actor}</span>
                {e.toStatus ? (
                  <>
                    {e.fromStatus && <StatusBadge status={e.fromStatus} />}
                    {e.fromStatus && <ArrowRight size={12} className="rtl:rotate-180" />}
                    <StatusBadge status={e.toStatus} />
                  </>
                ) : (
                  <span className="rounded bg-slate-100 px-1.5 text-xs">{e.action}</span>
                )}
                <span className="text-xs text-slate-500">{dateTime(e.at)}</span>
              </div>
              {e.details && <p className="mt-0.5 text-slate-600">{e.details}</p>}
            </li>
          ))}
        </ol>
      </section>
    </div>
  );
}
