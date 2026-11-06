"use client";

import { Suspense, useEffect, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { KeyRound, Loader2 } from "lucide-react";
import { AuthFailure, useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";

const MIN = 12;

function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") ? next : "/dashboard";
}

function ChangePasswordInner() {
  const { t } = useI18n();
  const { me, loading, changePassword } = useAuth();
  const router = useRouter();
  const next = safeNext(useSearchParams().get("next"));

  const [current, setCurrent] = useState("");
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);

  useEffect(() => {
    if (!loading && !me) router.replace("/login");
  }, [loading, me, router]);

  const mismatch = confirm.length > 0 && password !== confirm;
  const valid = current.length > 0 && password.length >= MIN && password === confirm;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setErrors([]);
    try {
      await changePassword(current, password);
      router.replace(next);
    } catch (err) {
      setErrors(err instanceof AuthFailure && err.problems.length ? err.problems : [err instanceof Error ? err.message : String(err)]);
    } finally {
      setBusy(false);
    }
  }

  if (!me) return <p className="p-8 text-slate-500">{t("common.loading")}</p>;

  return (
    <main className="flex min-h-screen items-center justify-center p-4">
      <form onSubmit={submit} className="card w-full max-w-sm space-y-3 p-6" noValidate>
        <h1 className="flex items-center gap-2 text-lg font-semibold"><KeyRound size={18} /> {t("pwd.title")}</h1>
        {me.mustChangePassword && <p className="rounded bg-amber-50 px-3 py-2 text-sm text-amber-800">{t("pwd.required")}</p>}
        <p className="text-sm text-slate-600">{t("pwd.hint")}</p>

        <label className="block">
          <span className="mb-1 block text-sm text-slate-600">{t("pwd.current")}</span>
          <input className="input" type="password" autoComplete="current-password" dir="ltr" value={current} onChange={(e) => setCurrent(e.target.value)} />
        </label>
        <label className="block">
          <span className="mb-1 block text-sm text-slate-600">{t("pwd.new")}</span>
          <input className="input" type="password" autoComplete="new-password" dir="ltr" value={password} onChange={(e) => setPassword(e.target.value)} />
          <span className={`mt-1 block text-xs ${password.length >= MIN ? "text-emerald-700" : "text-slate-500"}`}>
            {password.length}/{MIN}+
          </span>
        </label>
        <label className="block">
          <span className="mb-1 block text-sm text-slate-600">{t("pwd.confirm")}</span>
          <input className="input" type="password" autoComplete="new-password" dir="ltr" value={confirm} onChange={(e) => setConfirm(e.target.value)} />
          {mismatch && <span className="mt-1 block text-xs text-red-700">{t("pwd.mismatch")}</span>}
        </label>

        {errors.length > 0 && (
          <ul role="alert" className="list-disc rounded bg-red-50 py-2 pe-3 ps-6 text-sm text-red-700">
            {errors.map((e) => <li key={e}>{e}</li>)}
          </ul>
        )}

        <button type="submit" className="btn-primary w-full justify-center" disabled={!valid || busy}>
          {busy && <Loader2 size={16} className="animate-spin" />} {t("pwd.save")}
        </button>
      </form>
    </main>
  );
}

export default function ChangePasswordPage() {
  return (
    <Suspense>
      <ChangePasswordInner />
    </Suspense>
  );
}
