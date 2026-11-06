import type { NextConfig } from "next";

/** The .NET API. The browser only talks to this Next.js app; /api/* is proxied (no CORS needed). */
const apiBase = process.env.API_BASE_URL ?? "http://localhost:5080";
const isProd = process.env.NODE_ENV === "production";

// Microsoft sign-in endpoints are allowed only when Entra ID SSO is configured (ADR-0009 slice 2).
const entra = process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID ? " https://login.microsoftonline.com" : "";

const csp = [
  "default-src 'self'",
  // Next.js inlines its bootstrap script; dev mode also needs eval for fast refresh.
  `script-src 'self' 'unsafe-inline'${isProd ? "" : " 'unsafe-eval'"}`,
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' blob: data:",
  "font-src 'self'",
  "worker-src 'self' blob:", // pdf.js worker
  // blob: — pdf.js reads the original document from an object URL created after an authenticated fetch.
  `connect-src 'self' blob:${entra}`,
  `frame-src ${entra.trim() || "'none'"}`,
  "frame-ancestors 'none'",
  "base-uri 'self'",
  "form-action 'self'",
].join("; ");

const nextConfig: NextConfig = {
  // A second dev instance (e.g. for screenshots) needs its own build folder: NEXT_DIST_DIR=.next-shots
  distDir: process.env.NEXT_DIST_DIR ?? ".next",
  output: "standalone",
  poweredByHeader: false,
  reactStrictMode: true,
  webpack(config) {
    // pdf.js has an optional Node-only "canvas" dependency; the browser build never needs it.
    config.resolve.alias = { ...config.resolve.alias, canvas: false };
    return config;
  },
  async rewrites() {
    return [{ source: "/api/:path*", destination: `${apiBase}/api/:path*` }];
  },
  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          { key: "Content-Security-Policy", value: csp },
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "strict-origin-when-cross-origin" },
          { key: "Permissions-Policy", value: "camera=(), microphone=(), geolocation=()" },
        ],
      },
    ];
  },
};

export default nextConfig;
