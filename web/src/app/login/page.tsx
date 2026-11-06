"use client";

import { Suspense, useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { Eye, EyeOff, Loader2, LogIn } from "lucide-react";
import { AUTH_MODE, AuthFailure, DEMO_USERS, useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";

function safeNext(next: string | null): string {
  // Only same-site paths: never redirect to another origin after sign-in.
  return next && next.startsWith("/") && !next.startsWith("//") ? next : "/dashboard";
}

function LoginInner() {
  const { t, toggle } = useI18n();
  const { me, signInDev, login } = useAuth();
  const router = useRouter();
  const params = useSearchParams();
  const next = safeNext(params.get("next"));

  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [showPassword, setShowPassword] = useState(false);
  const [capsLock, setCapsLock] = useState(false);
  // Arabic/Persian letters in the password usually mean the keyboard layout is on Arabic by mistake.
  const arabicLayout = /[؀-ۿ]/.test(password);
  const [error, setError] = useState<"credentials" | "tooMany" | "server" | null>(null);

  // Dev convenience for scripted checks: /login?devUser=clara&devRoles=Clerk
  useEffect(() => {
    const user = params.get("devUser");
    if (AUTH_MODE === "dev" && user) signInDev({ user, roles: (params.get("devRoles") ?? "").split(",").filter(Boolean) });
  }, [params, signInDev]);

  useEffect(() => {
    if (me) router.replace(me.mustChangePassword ? `/account/password?next=${encodeURIComponent(next)}` : next);
  }, [me, next, router]);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await login(email.trim(), password);
    } catch (err) {
      const status = err instanceof AuthFailure ? err.status : 0;
      // Only a real "wrong credentials" answer clears the password; a server problem keeps it so the user can retry.
      if (status === 401) {
        setError("credentials");
        setPassword("");
      } else {
        setError(status === 429 ? "tooMany" : "server");
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="flex min-h-screen items-center justify-center p-4">
      <div className="card w-full max-w-sm p-6">
        <div className="mb-6 flex items-center justify-between">
          <h1 className="text-xl font-semibold text-brand-500">{t("appName")}</h1>
          <button className="text-sm text-slate-500 hover:text-slate-800" onClick={toggle}>{t("common.language")}</button>
        </div>

        {AUTH_MODE === "session" ? (
          <form onSubmit={submit} className="space-y-3" noValidate>
            <h2 className="font-medium">{t("auth.title")}</h2>
            <label className="block">
              <span className="mb-1 block text-sm text-slate-600">{t("auth.email")}</span>
              <input className="input" type="email" autoComplete="username" dir="ltr" required autoFocus
                value={email} onChange={(e) => setEmail(e.target.value)} />
            </label>
            <label className="block">
              <span className="mb-1 block text-sm text-slate-600">{t("auth.password")}</span>
              <span className="relative block">
                <input className="input pe-9" type={showPassword ? "text" : "password"} autoComplete="current-password" dir="ltr" required
                  value={password} onChange={(e) => setPassword(e.target.value)}
                  onKeyUp={(e) => setCapsLock(e.getModifierState("CapsLock"))}
                  onKeyDown={(e) => setCapsLock(e.getModifierState("CapsLock"))} />
                <button type="button" className="absolute inset-y-0 end-0 flex items-center px-2 text-slate-500 hover:text-slate-800"
                  onClick={() => setShowPassword((s) => !s)} aria-label={t(showPassword ? "auth.hidePassword" : "auth.showPassword")}
                  title={t(showPassword ? "auth.hidePassword" : "auth.showPassword")}>
                  {showPassword ? <EyeOff size={16} /> : <Eye size={16} />}
                </button>
              </span>
              {capsLock && <span className="mt-1 block text-xs text-amber-700">{t("auth.capsLock")}</span>}
              {arabicLayout && <span className="mt-1 block text-xs text-amber-700">{t("auth.arabicLayout")}</span>}
            </label>
            {error && (
              <p role="alert" data-testid="login-error" className={error === "server"
                ? "rounded bg-amber-50 px-3 py-2 text-sm text-amber-800"
                : "rounded bg-red-50 px-3 py-2 text-sm text-red-700"}>
                {t(error === "credentials" ? "auth.failed" : error === "tooMany" ? "auth.tooMany" : "auth.unavailable")}
              </p>
            )}
            <button type="submit" className="btn-primary w-full justify-center" disabled={busy || !email || !password}>
              {busy ? <Loader2 size={16} className="animate-spin" /> : <LogIn size={16} />} {t("auth.signIn")}
            </button>
          </form>
        ) : (
          <>
            <h2 className="font-medium">{t("auth.dev")}</h2>
            <p className="mb-4 text-sm text-slate-500">{t("auth.devHint")}</p>
            <ul className="space-y-2">
              {DEMO_USERS.map((u) => (
                <li key={u.user}>
                  <button className="btn-secondary w-full justify-between" onClick={() => signInDev(u)}>
                    <span className="font-medium capitalize">{u.user}</span>
                    <span className="text-xs text-slate-500">{u.roles.join(", ")}</span>
                  </button>
                </li>
              ))}
            </ul>
          </>
        )}
      </div>
    </main>
  );
}

export default function LoginPage() {
  return (
    <Suspense>
      <LoginInner />
    </Suspense>
  );
}
