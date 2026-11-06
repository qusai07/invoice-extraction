"use client";

import { Plus, Search, Trash2 } from "lucide-react";
import clsx from "clsx";
import type { FieldEvidence, Invoice, InvoiceLine, LineMatch } from "@/lib/types";
import { useI18n, type I18nKey } from "@/lib/i18n";
import type { Highlight } from "./document-viewer";

/** Everything is a string while editing; converted (and validated) on save. */
export type HeaderKey = Exclude<keyof Invoice, "lines">;
export type LineKey = keyof InvoiceLine;
export interface FormState {
  header: Record<HeaderKey, string>;
  lines: Record<LineKey, string>[];
}

const HEADER: { key: HeaderKey; type?: "date" | "number"; ltr?: boolean }[] = [
  { key: "supplierName" },
  { key: "supplierTrn", ltr: true },
  { key: "invoiceNumber", ltr: true },
  { key: "invoiceDate", type: "date" },
  { key: "dueDate", type: "date" },
  { key: "poNumber", ltr: true },
  { key: "currency", ltr: true },
  { key: "customerTrn", ltr: true },
  { key: "iban", ltr: true },
];
const TOTALS: HeaderKey[] = ["subtotal", "vatTotal", "total"];
const NUMERIC_HEADER = new Set<HeaderKey>(["subtotal", "vatTotal", "total"]);
const LINE_COLS: { key: LineKey; numeric?: boolean; wide?: boolean }[] = [
  { key: "sku" },
  { key: "description", wide: true },
  { key: "quantity", numeric: true },
  { key: "unitPrice", numeric: true },
  { key: "vatRate", numeric: true },
  { key: "lineTotal", numeric: true },
];

const str = (v: string | number | null | undefined) => (v == null ? "" : String(v));
const pct = (r: number | null) => (r == null ? "" : String(+(r * 100).toFixed(4)));
/** Money in inputs: always 2 decimals ("13.50"), more only if the value really has more. */
const amount = (v: number | null) => (v == null ? "" : Math.abs(v * 100 - Math.round(v * 100)) < 1e-9 ? v.toFixed(2) : String(v));
const MONEY = new Set<string>(["subtotal", "vatTotal", "total"]);

export function toForm(inv: Invoice): FormState {
  const header = {} as Record<HeaderKey, string>;
  for (const k of [...HEADER.map((h) => h.key), ...TOTALS]) {
    const v = inv[k];
    header[k] = MONEY.has(k) ? amount(v as number | null) : str(v);
  }
  return {
    header,
    lines: inv.lines.map((l) => ({
      sku: str(l.sku), description: str(l.description), quantity: str(l.quantity),
      unitPrice: amount(l.unitPrice), lineTotal: amount(l.lineTotal), vatRate: pct(l.vatRate),
    })),
  };
}

function parseNumber(s: string): number | null | "invalid" {
  const v = s.trim().replace(/,/g, "");
  if (v === "") return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : "invalid";
}

/** Form → Invoice. Returns the paths of invalid numbers instead if any. */
export function fromForm(f: FormState): { invoice: Invoice } | { invalid: string[] } {
  const invalid: string[] = [];
  const num = (path: string, s: string) => {
    const n = parseNumber(s);
    if (n === "invalid") { invalid.push(path); return null; }
    return n;
  };
  const text = (s: string) => (s.trim() === "" ? null : s.trim());

  const invoice: Invoice = {
    supplierName: text(f.header.supplierName), supplierTrn: text(f.header.supplierTrn), customerTrn: text(f.header.customerTrn),
    invoiceNumber: text(f.header.invoiceNumber), invoiceDate: text(f.header.invoiceDate), dueDate: text(f.header.dueDate),
    poNumber: text(f.header.poNumber), currency: text(f.header.currency)?.toUpperCase() ?? null, iban: text(f.header.iban),
    subtotal: num("subtotal", f.header.subtotal), vatTotal: num("vatTotal", f.header.vatTotal), total: num("total", f.header.total),
    lines: f.lines.map((l, i) => {
      const rate = num(`lines[${i}].vatRate`, l.vatRate);
      return {
        sku: text(l.sku), description: text(l.description),
        quantity: num(`lines[${i}].quantity`, l.quantity), unitPrice: num(`lines[${i}].unitPrice`, l.unitPrice),
        lineTotal: num(`lines[${i}].lineTotal`, l.lineTotal),
        vatRate: rate == null ? null : Math.round(rate * 100) / 10_000, // "5" (%) → 0.05
      };
    }),
  };
  return invalid.length > 0 ? { invalid } : { invoice };
}

export function InvoiceForm({
  form, onChange, editable, errorFields, invalidFields, evidence, lineMatches, onHighlight, afterHeader,
}: {
  form: FormState;
  onChange: (f: FormState) => void;
  editable: boolean;
  errorFields: Set<string>;
  invalidFields: Set<string>;
  evidence: FieldEvidence[];
  lineMatches: LineMatch[];
  onHighlight: (h: Highlight | null) => void;
  /** Extra help shown under the header fields (e.g. the PO picker). */
  afterHeader?: React.ReactNode;
}) {
  const { t, money } = useI18n();
  const ev = (path: string) => evidence.find((e) => e.field === path);
  const focus = (path: string) => {
    const e = ev(path);
    onHighlight(e ? { page: e.page, text: e.sourceText } : null);
  };

  const setHeader = (k: HeaderKey, v: string) => onChange({ ...form, header: { ...form.header, [k]: v } });
  const setLine = (i: number, k: LineKey, v: string) =>
    onChange({ ...form, lines: form.lines.map((l, j) => (j === i ? { ...l, [k]: v } : l)) });

  const field = (path: string, label: string, value: string, set: (v: string) => void, opts: { type?: string; ltr?: boolean; numeric?: boolean } = {}) => {
    const hasEvidence = !!ev(path);
    return (
      <label key={path} className="block">
        <span className="mb-0.5 flex items-center gap-1 text-xs text-slate-500">
          {label}
          {hasEvidence && <Search size={11} className="text-brand-500" aria-label={t("inv.evidence")} />}
        </span>
        <input
          id={`f-${path}`}
          type={opts.type === "date" ? "date" : "text"}
          inputMode={opts.numeric ? "decimal" : undefined}
          dir={opts.ltr || opts.numeric || opts.type === "date" ? "ltr" : undefined}
          className={clsx(
            "input",
            opts.numeric && "text-end tabular-nums",
            errorFields.has(path) && "border-red-400 bg-red-50",
            invalidFields.has(path) && "border-red-600 ring-1 ring-red-600",
          )}
          value={value}
          disabled={!editable}
          onFocus={() => focus(path)}
          onChange={(e) => set(e.target.value)}
          aria-invalid={invalidFields.has(path) || errorFields.has(path)}
          title={invalidFields.has(path) ? t("inv.invalidNumber") : undefined}
        />
      </label>
    );
  };

  return (
    <div className="space-y-5">
      <section>
        <h3 className="mb-2 text-sm font-semibold text-slate-700">{t("inv.header")}</h3>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
          {HEADER.map((h) => field(h.key, t(`field.${h.key}` as I18nKey), form.header[h.key], (v) => setHeader(h.key, v), { type: h.type, ltr: h.ltr }))}
        </div>
        {afterHeader && <div className="mt-3">{afterHeader}</div>}
      </section>

      <section>
        <div className="mb-2 flex items-center">
          <h3 className="text-sm font-semibold text-slate-700">{t("inv.lines")}</h3>
          {editable && (
            <button
              className="btn-secondary ms-auto px-2 py-1 text-xs"
              onClick={() => onChange({ ...form, lines: [...form.lines, { sku: "", description: "", quantity: "", unitPrice: "", lineTotal: "", vatRate: "5" }] })}
            >
              <Plus size={14} /> {t("inv.addLine")}
            </button>
          )}
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-xs text-slate-500">
              <tr>
                <th className="w-6 px-1 text-start">#</th>
                {LINE_COLS.map((c) => (
                  <th key={c.key} className={clsx("px-1 py-1 font-normal", c.numeric ? "text-end" : "text-start", c.wide && "min-w-48")}>
                    {t(`field.${c.key}` as I18nKey)}
                  </th>
                ))}
                <th className="px-1 text-start font-normal">{t("inv.poLine")}</th>
                {editable && <th />}
              </tr>
            </thead>
            <tbody>
              {form.lines.map((line, i) => {
                const match = lineMatches.find((m) => m.invoiceLineIndex === i);
                const rowError = errorFields.has(`lines[${i}]`);
                return (
                  <tr key={i} className={clsx("border-t border-slate-100 align-top", rowError && "bg-red-50")}>
                    <td className="px-1 py-1.5 text-slate-400">{i + 1}</td>
                    {LINE_COLS.map((c) => {
                      const path = `lines[${i}].${c.key}`;
                      return (
                        <td key={c.key} className="px-1 py-1">
                          <input
                            id={`f-${path}`}
                            dir={c.numeric || c.key === "sku" ? "ltr" : undefined}
                            inputMode={c.numeric ? "decimal" : undefined}
                            className={clsx(
                              "input",
                              c.numeric && "w-24 text-end tabular-nums",
                              (errorFields.has(path) || rowError) && "border-red-400",
                              invalidFields.has(path) && "border-red-600 ring-1 ring-red-600",
                            )}
                            value={line[c.key]}
                            disabled={!editable}
                            onFocus={() => focus(path)}
                            onChange={(e) => setLine(i, c.key, e.target.value)}
                            aria-label={`${t(`field.${c.key}` as I18nKey)} ${i + 1}`}
                          />
                        </td>
                      );
                    })}
                    <td className="px-1 py-1.5 text-xs">
                      {match?.poLine ? (
                        <span className="text-slate-600">
                          <span className="ltr-value font-mono">#{match.poLine.lineNo}</span>{" "}
                          <span className="ltr-value">{match.poLine.quantity - match.poLine.quantityInvoiced} × {money(match.poLine.unitPrice)}</span>
                        </span>
                      ) : match ? (
                        <span className="text-red-700">
                          {t("inv.noMatch")}
                          {match.suggestions[0] && (
                            <span className="block text-slate-500">{t("inv.suggest")}: #{match.suggestions[0].lineNo} {match.suggestions[0].description}</span>
                          )}
                        </span>
                      ) : null}
                    </td>
                    {editable && (
                      <td className="px-1 py-1">
                        <button
                          className="text-slate-400 hover:text-red-600"
                          title={t("inv.removeLine")}
                          onClick={() => onChange({ ...form, lines: form.lines.filter((_, j) => j !== i) })}
                        >
                          <Trash2 size={14} />
                        </button>
                      </td>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <section className="ms-auto grid max-w-sm grid-cols-1 gap-2">
        {TOTALS.map((k) => field(k, t(`field.${k}` as I18nKey), form.header[k], (v) => setHeader(k, v), { numeric: NUMERIC_HEADER.has(k) }))}
      </section>
    </div>
  );
}
