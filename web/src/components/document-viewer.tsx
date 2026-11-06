"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { ExternalLink, ZoomIn, ZoomOut } from "lucide-react";
import type { PDFDocumentProxy } from "pdfjs-dist";
import { blobUrl } from "@/lib/api";
import { useI18n } from "@/lib/i18n";

export interface Highlight {
  page: number | null;
  text: string;
}

/** Same normalization as the C# side: Arabic-Indic digits → Latin, Arabic separators, no spaces, lower case. */
export function normalizeForMatch(s: string): string {
  let out = "";
  for (const ch of s) {
    const c = ch.charCodeAt(0);
    if (c >= 0x0660 && c <= 0x0669) out += String(c - 0x0660);
    else if (c >= 0x06f0 && c <= 0x06f9) out += String(c - 0x06f0);
    else if (ch === "٫") out += ".";
    else if (ch === "٬" || ch === "،") out += ",";
    else if (!/\s/.test(ch) && !/[‎‏؜]/.test(ch)) out += ch.toLowerCase();
  }
  return out;
}

/** Original document: PDF (pdf.js, with text-based highlighting) or image. */
export function DocumentViewer({ documentId, highlight }: { documentId: string; highlight: Highlight | null }) {
  const { t } = useI18n();
  const [file, setFile] = useState<{ url: string; type: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [scale, setScale] = useState(1.25);

  useEffect(() => {
    let url: string | null = null;
    let cancelled = false;
    blobUrl(`/invoices/${documentId}/file`)
      .then((f) => {
        url = f.url;
        if (cancelled) URL.revokeObjectURL(f.url);
        else setFile(f);
      })
      .catch((e: unknown) => !cancelled && setError(e instanceof Error ? e.message : String(e)));
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [documentId]);

  return (
    <div className="flex h-full flex-col">
      <div className="flex items-center gap-2 border-b border-slate-200 bg-white px-3 py-1.5 text-sm">
        <button className="btn-secondary px-2 py-1" title={t("inv.zoomOut")} onClick={() => setScale((s) => Math.max(0.5, s - 0.25))}><ZoomOut size={14} /></button>
        <span className="w-12 text-center text-slate-500">{Math.round(scale * 100)}%</span>
        <button className="btn-secondary px-2 py-1" title={t("inv.zoomIn")} onClick={() => setScale((s) => Math.min(3, s + 0.25))}><ZoomIn size={14} /></button>
        {highlight?.page && <span className="ms-2 text-xs text-slate-500">{t("inv.page")} {highlight.page}</span>}
        {file && (
          <a className="ms-auto flex items-center gap-1 text-brand-600 hover:underline" href={file.url} target="_blank" rel="noreferrer">
            <ExternalLink size={14} /> {t("inv.download")}
          </a>
        )}
      </div>
      <div className="flex-1 overflow-auto bg-slate-200 p-3">
        {error ? (
          <p className="text-red-700">{error}</p>
        ) : !file ? (
          <p className="text-slate-500">{t("common.loading")}</p>
        ) : file.type === "application/pdf" ? (
          <PdfDocument url={file.url} scale={scale} highlight={highlight} />
        ) : (
          <>
            {highlight && <p className="mb-2 rounded bg-amber-50 px-2 py-1 text-xs text-amber-800">{t("inv.imageNoHighlight")}</p>}
            {/* eslint-disable-next-line @next/next/no-img-element -- blob: URL of the original document */}
            <img src={file.url} alt="" style={{ width: `${scale * 60}%` }} className="mx-auto bg-white shadow" />
          </>
        )}
      </div>
    </div>
  );
}

function PdfDocument({ url, scale, highlight }: { url: string; scale: number; highlight: Highlight | null }) {
  const [pdf, setPdf] = useState<PDFDocumentProxy | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    let loaded: PDFDocumentProxy | null = null;
    (async () => {
      try {
        const pdfjs = await import("pdfjs-dist");
        pdfjs.GlobalWorkerOptions.workerSrc = new URL("pdfjs-dist/build/pdf.worker.min.mjs", import.meta.url).toString();
        loaded = await pdfjs.getDocument({ url, isEvalSupported: false }).promise;
        if (!cancelled) setPdf(loaded);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      }
    })();
    return () => {
      cancelled = true;
      void loaded?.destroy();
    };
  }, [url]);

  if (error) return <p className="text-red-700">{error}</p>;
  if (!pdf) return null;

  return (
    <div className="flex flex-col items-center gap-3">
      {Array.from({ length: pdf.numPages }, (_, i) => (
        <PdfPage key={i} pdf={pdf} pageNumber={i + 1} scale={scale} highlight={highlight?.page === i + 1 || (highlight && !highlight.page && i === 0) ? highlight : null} />
      ))}
    </div>
  );
}

interface Rect { left: number; top: number; width: number; height: number }

function PdfPage({ pdf, pageNumber, scale, highlight }: { pdf: PDFDocumentProxy; pageNumber: number; scale: number; highlight: Highlight | null }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const container = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ width: 0, height: 0 });
  const [items, setItems] = useState<{ norm: string; rect: Rect }[]>([]);

  useEffect(() => {
    let cancelled = false;
    let task: { cancel: () => void } | null = null;
    (async () => {
      const pdfjs = await import("pdfjs-dist");
      const page = await pdf.getPage(pageNumber);
      const viewport = page.getViewport({ scale });
      const c = canvas.current;
      if (!c || cancelled) return;

      // Sharp on high-DPI screens.
      const ratio = window.devicePixelRatio || 1;
      c.width = Math.floor(viewport.width * ratio);
      c.height = Math.floor(viewport.height * ratio);
      c.style.width = `${viewport.width}px`;
      c.style.height = `${viewport.height}px`;
      setSize({ width: viewport.width, height: viewport.height });
      const ctx = c.getContext("2d")!;
      ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
      const render = page.render({ canvasContext: ctx, viewport });
      task = render;
      await render.promise.catch(() => undefined);

      const text = await page.getTextContent();
      if (cancelled) return;
      setItems(
        text.items.flatMap((item) => {
          if (!("str" in item) || !item.str.trim()) return [];
          const tx = pdfjs.Util.transform(viewport.transform, item.transform) as number[];
          const h = Math.hypot(tx[2]!, tx[3]!);
          return [{ norm: normalizeForMatch(item.str), rect: { left: tx[4]!, top: tx[5]! - h, width: item.width * scale, height: h } }];
        }),
      );
    })();
    return () => {
      cancelled = true;
      task?.cancel();
    };
  }, [pdf, pageNumber, scale]);

  const rects = useMemo(() => {
    if (!highlight) return [];
    const target = normalizeForMatch(highlight.text);
    if (target.length === 0) return [];
    // Exact run first; else runs that contain the value or are a meaningful part of it (values split across runs).
    const exact = items.filter((i) => i.norm === target);
    if (exact.length > 0) return exact.map((i) => i.rect);
    return items.filter((i) => i.norm.includes(target) || (i.norm.length >= 3 && target.includes(i.norm))).map((i) => i.rect);
  }, [items, highlight]);

  useEffect(() => {
    if (highlight && container.current) {
      const first = rects[0];
      if (first) container.current.parentElement?.parentElement?.scrollTo({ top: container.current.offsetTop + first.top - 120, behavior: "smooth" });
      else container.current.scrollIntoView({ behavior: "smooth", block: "start" });
    }
  }, [highlight, rects]);

  return (
    <div ref={container} className="relative bg-white shadow" style={{ width: size.width || undefined, height: size.height || undefined }}>
      <canvas ref={canvas} />
      {rects.map((r, i) => (
        <div
          key={i}
          className="pointer-events-none absolute rounded-sm bg-yellow-300/40 ring-2 ring-amber-500"
          style={{ left: r.left - 2, top: r.top - 2, width: r.width + 4, height: r.height + 4 }}
        />
      ))}
    </div>
  );
}
