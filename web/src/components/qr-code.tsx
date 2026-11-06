"use client";

import { useMemo } from "react";
import QRCode from "qrcode";

/**
 * A QR code drawn as SVG from the library's module matrix: no images, no injected HTML (fits the strict CSP).
 * Always dark-on-white with a quiet zone, whatever the theme, so phone cameras read it.
 */
export function QrCode({ value, size = 192, label }: { value: string; size?: number; label: string }) {
  const { path, count } = useMemo(() => {
    const qr = QRCode.create(value, { errorCorrectionLevel: "M" });
    const n = qr.modules.size;
    let d = "";
    for (let y = 0; y < n; y++) {
      for (let x = 0; x < n; x++) {
        if (qr.modules.get(y, x)) d += `M${x + 4} ${y + 4}h1v1h-1z`;
      }
    }
    return { path: d, count: n + 8 };
  }, [value]);

  return (
    <svg role="img" aria-label={label} width={size} height={size} viewBox={`0 0 ${count} ${count}`} shapeRendering="crispEdges"
      className="rounded-md border border-slate-200">
      <rect width={count} height={count} fill="#ffffff" />
      <path d={path} fill="#000000" />
    </svg>
  );
}
