"use client";

import { useState } from "react";
import { useI18n } from "@/lib/i18n";

export interface DayPoint { day: string; straightThrough: number; neededReview: number }

// Categorical slots 1 and 2 of the validated reference palette (dataviz skill; all six checks pass on light surface).
const SERIES = [
  { key: "straightThrough" as const, color: "#2a78d6", label: "dash.chart.straight" as const },
  { key: "neededReview" as const, color: "#eb6834", label: "dash.chart.review" as const },
];

const W = 640, H = 200, PAD_L = 28, PAD_B = 22, PAD_T = 8;

/** One stacked segment: square bottom (anchored), 4px rounded top on the topmost segment; 2px gap handled by caller. */
function Segment({ x, w, top, bottom, color, roundTop }: { x: number; w: number; top: number; bottom: number; color: string; roundTop: boolean }) {
  const h = Math.max(0, bottom - top);
  if (h === 0) return null;
  if (!roundTop || h <= 4) return <rect x={x} y={top} width={w} height={h} fill={color} />;
  return (
    <path
      d={`M${x},${bottom} V${top + 4} Q${x},${top} ${x + 4},${top} H${x + w - 4} Q${x + w},${top} ${x + w},${top + 4} V${bottom} Z`}
      fill={color}
    />
  );
}

/** Stacked bars per day: straight-through vs needed a person. Hover a day for exact numbers; table view for accessibility. */
export function FlowChart({ data }: { data: DayPoint[] }) {
  const { t, lang } = useI18n();
  const [hover, setHover] = useState<number | null>(null);
  const [table, setTable] = useState(false);

  const max = Math.max(1, ...data.map((d) => d.straightThrough + d.neededReview));
  const niceMax = max <= 5 ? 5 : Math.ceil(max / 5) * 5;
  const plotW = W - PAD_L, plotH = H - PAD_B - PAD_T;
  const slot = plotW / data.length;
  const barW = Math.min(22, slot * 0.6);
  const y = (v: number) => PAD_T + plotH - (v / niceMax) * plotH;
  const fmtDay = (iso: string) => new Date(`${iso}T00:00:00`).toLocaleDateString(lang === "ar" ? "ar-AE-u-nu-latn" : "en-AE", { day: "numeric", month: "short" });
  const total = data.reduce((s, d) => s + d.straightThrough + d.neededReview, 0);

  return (
    <div>
      <div className="mb-2 flex flex-wrap items-center gap-4 text-xs text-slate-600">
        {SERIES.map((s) => (
          <span key={s.key} className="flex items-center gap-1.5">
            <span className="inline-block h-2.5 w-2.5 rounded-sm" style={{ background: s.color }} /> {t(s.label)}
          </span>
        ))}
        <button className="ms-auto text-brand-600 hover:underline" onClick={() => setTable((v) => !v)}>{t("dash.chart.table")}</button>
      </div>

      {total === 0 ? (
        <p className="py-10 text-center text-sm text-slate-500">{t("dash.chart.empty")}</p>
      ) : table ? (
        <table className="w-full text-sm">
          <thead className="text-xs text-slate-500">
            <tr><th className="py-1 text-start font-normal">{t("dash.chart.day")}</th>
              {SERIES.map((s) => <th key={s.key} className="py-1 text-end font-normal">{t(s.label)}</th>)}</tr>
          </thead>
          <tbody>
            {data.map((d) => (
              <tr key={d.day} className="border-t border-slate-100">
                <td className="py-1">{fmtDay(d.day)}</td>
                <td className="py-1 text-end tabular-nums">{d.straightThrough}</td>
                <td className="py-1 text-end tabular-nums">{d.neededReview}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <div className="relative" dir="ltr">
          <svg viewBox={`0 0 ${W} ${H}`} className="h-auto w-full" role="img" aria-label={t("dash.chart.title")}>
            {[0, niceMax / 2, niceMax].map((v) => (
              <g key={v}>
                <line x1={PAD_L} x2={W} y1={y(v)} y2={y(v)} stroke="#e5e7eb" strokeWidth={1} />
                <text x={PAD_L - 6} y={y(v) + 3} textAnchor="end" fontSize={10} fill="#64748b">{v}</text>
              </g>
            ))}
            {data.map((d, i) => {
              const x = PAD_L + i * slot + (slot - barW) / 2;
              const a = d.straightThrough, b = d.neededReview;
              const yA = y(a), yAB = y(a + b);
              return (
                <g key={d.day} onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
                  {/* hit target larger than the marks */}
                  <rect x={PAD_L + i * slot} y={PAD_T} width={slot} height={plotH} fill={hover === i ? "#f1f5f9" : "transparent"} />
                  {a > 0 && <Segment x={x} w={barW} top={yA} bottom={y(0)} color={SERIES[0]!.color} roundTop={b === 0} />}
                  {b > 0 && <Segment x={x} w={barW} top={yAB} bottom={a > 0 ? yA - 2 : y(0)} color={SERIES[1]!.color} roundTop />}
                  {i % 2 === 1 && <text x={x + barW / 2} y={H - 6} textAnchor="middle" fontSize={10} fill="#64748b">{fmtDay(d.day)}</text>}
                </g>
              );
            })}
          </svg>
          {hover !== null && data[hover] && (
            <div className="pointer-events-none absolute top-0 rounded-md bg-slate-900 px-2.5 py-1.5 text-xs text-white shadow"
              style={{ left: `${((PAD_L + hover * slot + slot / 2) / W) * 100}%`, transform: "translateX(-50%)" }}>
              <p className="font-medium">{fmtDay(data[hover].day)}</p>
              {SERIES.map((s) => (
                <p key={s.key} className="flex items-center gap-1.5">
                  <span className="inline-block h-2 w-2 rounded-sm" style={{ background: s.color }} />
                  {t(s.label)}: <span className="tabular-nums">{data[hover]![s.key]}</span>
                </p>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
