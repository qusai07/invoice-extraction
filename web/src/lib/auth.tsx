"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { Me } from "./types";

/**
 * Sign-in modes (NEXT_PUBLIC_AUTH_MODE):
 *  - "session" (default): InvoiceFlow accounts. The short-lived access token lives in memory only (never storage);
 *    the refresh token is an httpOnly cookie the browser sends to /api/auth/refresh. A reload restores the session.
 *  - "dev": pick a demo user; requests carry X-Dev-User / X-Dev-Roles (API must run with Auth:Mode=Dev).
 * Who the user is and what they may do always comes from the API (GET /api/me / the session response).
 */
export const AUTH_MODE: "session" | "dev" = process.env.NEXT_PUBLIC_AUTH_MODE === "dev" ? "dev" : "session";

const CSRF = { "X-InvoiceFlow": "1" };

// ---------------- dev mode ----------------

const DEV_KEY = "invoiceflow.devUser";

export interface DevUser { user: string; roles: string[] }

export const DEMO_USERS: DevUser[] = [
  { user: "clara", roles: ["Clerk"] },
  { user: "sam", roles: ["Supervisor"] },
  { user: "fatima", roles: ["FinanceAdmin"] },
  { user: "sue", roles: ["Submitter"] },
  { user: "aaron", roles: ["Auditor"] },
  { user: "ada", roles: ["SystemAdmin"] },
];

function readDevUser(): DevUser | null {
  try {
    const raw = sessionStorage.getItem(DEV_KEY);
    return raw ? (JSON.parse(raw) as DevUser) : null;
  } catch {
    return null;
  }
}

export function setDevUser(u: DevUser | null) {
  try {
    if (u) sessionStorage.setItem(DEV_KEY, JSON.stringify(u));
    else sessionStorage.removeItem(DEV_KEY);
  } catch {
    /* storage blocked: sign-in lasts until reload */
  }
}

// ---------------- session mode ----------------

interface SessionResponse { accessToken: string; expiresAt: string; user: Me }

let accessToken: string | null = null;
let accessExpires = 0;
let refreshing: Promise<Me | null> | null = null;
let onChange: ((me: Me | null) => void) | null = null;

function accept(s: SessionResponse): Me {
  accessToken = s.accessToken;
  accessExpires = Date.parse(s.expiresAt);
  onChange?.(s.user);
  return s.user;
}

function clearSession() {
  accessToken = null;
  accessExpires = 0;
  onChange?.(null);
}

/** One refresh at a time per tab: ten parallel 401s cause one refresh, not ten. */
export function refreshSession(): Promise<Me | null> {
  if (!refreshing) {
    refreshing = (async () => {
      for (let attempt = 0; attempt < 2; attempt++) {
        const r = await fetch("/api/auth/refresh", { method: "POST", headers: CSRF, credentials: "same-origin" });
        if (r.ok) return accept((await r.json()) as SessionResponse);
        if (r.status !== 409) break; // 409 = another tab rotated the token a moment ago: retry once
      }
      clearSession();
      return null;
    })().finally(() => { refreshing = null; });
  }
  return refreshing;
}

/** Headers that authenticate an API request in the current mode (refreshing the access token when it's about to expire). */
export async function authHeaders(): Promise<Record<string, string>> {
  if (AUTH_MODE === "dev") {
    const u = readDevUser();
    return u ? { "X-Dev-User": u.user, "X-Dev-Roles": u.roles.join(",") } : {};
  }
  if (!accessToken || Date.now() > accessExpires - 30_000) await refreshSession();
  return accessToken ? { Authorization: `Bearer ${accessToken}` } : {};
}

// ---------------- permissions (UI hints; the API enforces) ----------------

export type Permission = "submit" | "review" | "reject" | "override" | "approve" | "retry" | "auditExport" | "admin";

const PERMISSION: Record<Exclude<Permission, "admin">, string> = {
  submit: "invoice.submit",
  review: "invoice.correct",
  reject: "invoice.reject",
  override: "invoice.override",
  approve: "invoice.approve",
  retry: "invoice.retry-posting",
  auditExport: "audit.export",
};

interface AuthState {
  me: Me | null;
  loading: boolean;
  can: (p: Permission) => boolean;
  /** Resolves with a challenge when the account uses two-step sign-in; then call loginWithCode. */
  login: (email: string, password: string) => Promise<{ challenge: string } | void>;
  loginWithCode: (challenge: string, code: string) => Promise<void>;
  signInDev: (u: DevUser) => void;
  changePassword: (current: string, next: string) => Promise<void>;
  signOut: () => Promise<void>;
  signOutEverywhere: () => Promise<void>;
  reload: () => void;
}

export class AuthFailure extends Error {
  /** status: 401 wrong credentials · 429 too many attempts · 0/5xx server not reachable. */
  constructor(message: string, public problems: string[] = [], public status = 0) {
    super(message);
  }
}

async function problem(r: Response): Promise<AuthFailure> {
  try {
    const p = (await r.json()) as { detail?: string; title?: string; errors?: Record<string, string[]> };
    const problems = p.errors ? Object.values(p.errors).flat() : [];
    return new AuthFailure(p.detail ?? problems[0] ?? p.title ?? r.statusText, problems, r.status);
  } catch {
    return new AuthFailure(r.statusText, [], r.status);
  }
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [loading, setLoading] = useState(true);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    onChange = setMe;
    return () => { onChange = null; };
  }, []);

  // Restore the session on load (session mode: refresh cookie → access token; dev mode: /api/me).
  useEffect(() => {
    let cancelled = false;
    (async () => {
      setLoading(true);
      try {
        if (AUTH_MODE === "session") {
          const user = await refreshSession();
          if (!cancelled) setMe(user);
        } else {
          const headers = await authHeaders();
          const r = Object.keys(headers).length ? await fetch("/api/me", { headers }) : null;
          if (!cancelled) setMe(r?.ok ? ((await r.json()) as Me) : null);
        }
      } catch {
        if (!cancelled) setMe(null);
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [version]);

  const can = useCallback((p: Permission) => {
    if (!me) return false;
    if (p === "admin") return me.permissions.some((x) => x.startsWith("admin."));
    return me.permissions.includes(PERMISSION[p]);
  }, [me]);

  const value = useMemo<AuthState>(() => ({
    me,
    loading,
    can,
    reload: () => setVersion((v) => v + 1),
    signInDev: (u) => { setDevUser(u); setVersion((v) => v + 1); },
    login: async (email, password) => {
      let r: Response;
      try {
        r = await fetch("/api/auth/login", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          credentials: "same-origin",
          body: JSON.stringify({ email, password }),
        });
      } catch {
        throw new AuthFailure("network", [], 0);
      }
      if (!r.ok) throw await problem(r);
      const body = (await r.json()) as SessionResponse | { codeRequired: true; challenge: string };
      if ("codeRequired" in body) return { challenge: body.challenge };
      accept(body);
    },
    loginWithCode: async (challenge, code) => {
      let r: Response;
      try {
        r = await fetch("/api/auth/login/code", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          credentials: "same-origin",
          body: JSON.stringify({ challenge, code }),
        });
      } catch {
        throw new AuthFailure("network", [], 0);
      }
      if (!r.ok) throw await problem(r);
      accept((await r.json()) as SessionResponse);
    },
    changePassword: async (current, next) => {
      const r = await fetch("/api/auth/change-password", {
        method: "POST",
        headers: { "Content-Type": "application/json", ...(await authHeaders()) },
        credentials: "same-origin",
        body: JSON.stringify({ currentPassword: current, newPassword: next }),
      });
      if (!r.ok) throw await problem(r);
      accept((await r.json()) as SessionResponse);
    },
    signOut: async () => {
      if (AUTH_MODE === "dev") { setDevUser(null); setMe(null); return; }
      await fetch("/api/auth/logout", { method: "POST", headers: CSRF, credentials: "same-origin" }).catch(() => undefined);
      clearSession();
    },
    signOutEverywhere: async () => {
      await fetch("/api/auth/logout-everywhere", { method: "POST", headers: { ...CSRF, ...(await authHeaders()) }, credentials: "same-origin" });
      clearSession();
    },
  }), [me, loading, can]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

/** Where a signed-in user must go first: change a temporary password, then set up two-step sign-in if required. */
export function requiredAccountStep(me: Me, next: string): string | null {
  if (me.mustChangePassword) return `/account/password?next=${encodeURIComponent(next)}`;
  if (me.mfaSetupRequired) return `/account/security?next=${encodeURIComponent(next)}`;
  return null;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth outside AuthProvider");
  return ctx;
}
