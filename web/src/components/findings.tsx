"use client";

import { AlertTriangle, CheckCircle2, Info, XCircle } from "lucide-react";
import type { Finding, Severity } from "@/lib/types";
import { useI18n } from "@/lib/i18n";
import { RuleChip } from "./ui";

const ORDER: Severity[] = ["Error", "Warning", "Info"];
const ICON = { Error: XCircle, Warning: AlertTriangle, Info } as const;
const COLOR = { Error: "text-red-600", Warning: "text-amber-600", Info: "text-sky-600" } as const;

/** Validation findings, errors first. Clicking one jumps to the field it is about. */
export function Findings({ findings, onSelect }: { findings: Finding[]; onSelect: (field: string) => void }) {
  const { t, lang } = useI18n();

  if (findings.length === 0) {
    return (
      <p className="flex items-center gap-2 text-sm text-emerald-700">
        <CheckCircle2 size={16} /> {t("inv.noFindings")}
      </p>
    );
  }

  const sorted = [...findings].sort((a, b) => ORDER.indexOf(a.severity) - ORDER.indexOf(b.severity));

  return (
    <ul className="space-y-1.5">
      {sorted.map((f, i) => {
        const Icon = ICON[f.severity];
        return (
          <li key={i}>
            <button
              className="flex w-full items-start gap-2 rounded px-2 py-1.5 text-start text-sm hover:bg-slate-50 disabled:cursor-default disabled:hover:bg-transparent"
              disabled={!f.field}
              onClick={() => f.field && onSelect(f.field)}
            >
              <Icon size={16} className={`mt-0.5 shrink-0 ${COLOR[f.severity]}`} />
              <span className="flex-1">
                <span className="mb-0.5 block"><RuleChip code={f.ruleCode} severity={f.severity} /></span>
                <span>{lang === "ar" ? f.messageAr : f.messageEn}</span>
                {(f.expected || f.actual) && f.severity !== "Info" && (
                  <span className="mt-0.5 block text-xs text-slate-500">
                    {f.expected && <>{t("inv.expected")}: <span className="ltr-value font-mono">{f.expected}</span> </>}
                    {f.actual && <>· {t("inv.actual")}: <span className="ltr-value font-mono">{f.actual}</span></>}
                  </span>
                )}
              </span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}
