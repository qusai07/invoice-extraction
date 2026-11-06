"use client";

import { Suspense, useEffect, useState } from "react";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { ArrowLeft, Check, Copy, Download, KeyRound, Loader2, ShieldCheck, ShieldOff, Smartphone } from "lucide-react";
import { api, post } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";
import { ErrorNote } from "@/components/ui";
import { QrCode } from "@/components/qr-code";

interface Status { enabled: boolean; required: boolean; available: boolean; recoveryCodesLeft: number; enabledAt: string | null }
interface Setup { secret: string; otpAuthUri: string }

function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") && !next.startsWith("/account/security") ? next : "/dashboard";
}

const digits = (s: string) => s.replace(/\D/g, "");

/** Two-step sign-in: set up (QR → first code → recovery codes), new recovery codes, turn off. */
function SecurityInner() {
  const { t, dateTime } = useI18n();
  const { me, loading, reload } = useAuth();
  const router = useRouter();
  const next = safeNext(useSearchParams().get("next"));

  const [status, setStatus] = useState<Status | null>(null);
  const [setup, setSetup] = useState<Setup | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [saved, setSaved] = useState(false);
  const [code, setCode] = useState("");
  const [password, setPassword] = useState("");
  const [mode, setMode] = useState<"idle" | "renew" | "disable">("idle");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!loading && !me) router.replace("/login");
  }, [loading, me, router]);

  const load = async () => setStatus(await api<Status>("/auth/mfa"));
  useEffect(() => {
    if (me) void load().catch(setError);
  }, [me]);

  const run = async (fn: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try { await fn(); } catch (e) { setError(e); } finally { setBusy(false); }
  };

  const start = () => run(async () => { setSetup(await post<Setup>("/auth/mfa/setup")); setCode(""); });
  const confirm = () => run(async () => {
    const r = await post<{ recoveryCodes: string[] }>("/auth/mfa/enable", { code });
    setCodes(r.recoveryCodes);
    setSetup(null);
    setCode("");
    await load();
  });
  const renew = () => run(async () => {
    const r = await post<{ recoveryCodes: string[] }>("/auth/mfa/recovery-codes", { code });
    setCodes(r.recoveryCodes);
    setSaved(false);
    setMode("idle");
    setCode("");
    await load();
  });
  const disable = () => run(async () => {
    await post("/auth/mfa/disable", { password, code });
    setMode("idle");
    setCode("");
    setPassword("");
    await load();
    reload();
  });
  const finish = () => {
    setCodes(null);
    reload(); // lifts the "set up first" restriction
    if (me?.mfaSetupRequired) router.replace(next);
  };

  const codesText = codes ? `InvoiceFlow recovery codes (${me?.email ?? ""})\n\n${codes.join("\n")}\n\nEach code works once.\n` : "";
  const download = () => {
    const url = URL.createObjectURL(new Blob([codesText], { type: "text/plain" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = "invoiceflow-recovery-codes.txt";
    a.click();
    URL.revokeObjectURL(url);
  };

  if (!me || !status) {
    return <p className="p-8 text-slate-500">{error ? <ErrorNote error={error} /> : t("common.loading")}</p>;
  }

  const groupedSecret = setup?.secret.match(/.{1,4}/g)?.join(" ") ?? "";

  return (
    <main className="flex min-h-screen items-start justify-center p-4 sm:items-center">
      <div className="card w-full max-w-lg space-y-4 p-6">
        {!me.mfaSetupRequired && (
          <Link href={next} className="inline-flex items-center gap-1 text-xs text-slate-500 hover:text-slate-800">
            <ArrowLeft size={12} className="rtl:rotate-180" /> {t("mfa.back")}
          </Link>
        )}
        <div className="flex items-center gap-3">
          <h1 className="flex items-center gap-2 text-lg font-semibold"><ShieldCheck size={20} className="text-brand-600" /> {t("mfa.title")}</h1>
          <span className={status.enabled ? "rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700"
            : "rounded-full bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600"}>
            {status.enabled ? t("mfa.on") : t("mfa.off")}
          </span>
        </div>
        <p className="text-sm text-slate-600">{t("mfa.intro")}</p>

        {me.mfaSetupRequired && !codes && (
          <p className="rounded bg-amber-50 px-3 py-2 text-sm text-amber-800">{t("mfa.required")}</p>
        )}

        {/* ---------- recovery codes, shown once ---------- */}
        {codes && (
          <section className="space-y-3 rounded-lg border border-emerald-200 bg-emerald-50 p-4">
            <p className="flex items-center gap-2 font-medium text-emerald-900"><Check size={16} /> {t("mfa.codes.title")}</p>
            <p className="text-sm text-emerald-900">{t("mfa.codes.body")}</p>
            <ul className="grid grid-cols-2 gap-1.5 rounded-md bg-white p-3 font-mono text-sm" dir="ltr">
              {codes.map((c) => <li key={c}>{c}</li>)}
            </ul>
            <div className="flex flex-wrap gap-2">
              <button className="btn-secondary px-2 py-1 text-xs" onClick={() => { void navigator.clipboard.writeText(codesText); setCopied(true); }}>
                <Copy size={14} /> {copied ? t("mfa.codes.copied") : t("mfa.codes.copy")}
              </button>
              <button className="btn-secondary px-2 py-1 text-xs" onClick={download}><Download size={14} /> {t("mfa.codes.download")}</button>
            </div>
            <label className="flex items-center gap-2 text-sm text-emerald-900">
              <input type="checkbox" checked={saved} onChange={(e) => setSaved(e.target.checked)} /> {t("mfa.codes.saved")}
            </label>
            <button className="btn-primary" disabled={!saved} onClick={finish}>{t("mfa.codes.done")}</button>
          </section>
        )}

        {/* ---------- not on: set up ---------- */}
        {!status.enabled && !codes && (
          !status.available ? (
            <p className="rounded bg-slate-100 px-3 py-2 text-sm text-slate-600">{t("mfa.unavailable")}</p>
          ) : !setup ? (
            <section className="space-y-3">
              <ol className="list-decimal space-y-1 ps-5 text-sm text-slate-700">
                <li>{t("mfa.step1")}</li>
                <li>{t("mfa.step2")}</li>
                <li>{t("mfa.step3")}</li>
              </ol>
              <button className="btn-primary" disabled={busy} onClick={() => void start()}>
                {busy ? <Loader2 size={16} className="animate-spin" /> : <Smartphone size={16} />} {t("mfa.start")}
              </button>
            </section>
          ) : (
            <section className="space-y-4">
              <div className="flex flex-col items-center gap-3 sm:flex-row sm:items-start">
                <QrCode value={setup.otpAuthUri} label={t("mfa.qrLabel")} />
                <div className="space-y-2 text-sm">
                  <p className="text-slate-700">{t("mfa.scan")}</p>
                  <p className="text-xs text-slate-500">{t("mfa.manual")}</p>
                  <p className="select-all break-all rounded bg-slate-100 px-2 py-1 font-mono text-sm tracking-wider" dir="ltr">{groupedSecret}</p>
                </div>
              </div>
              <label className="block">
                <span className="mb-1 block text-sm text-slate-600">{t("mfa.enterCode")}</span>
                <input className="input w-44 text-center font-mono text-lg tracking-[0.4em]" dir="ltr" inputMode="numeric" autoComplete="one-time-code"
                  autoFocus maxLength={7} placeholder="123456" value={code} onChange={(e) => setCode(e.target.value.replace(/[^\d ]/g, ""))} />
              </label>
              <div className="flex gap-2">
                <button className="btn-primary" disabled={busy || digits(code).length !== 6} onClick={() => void confirm()}>
                  {busy ? <Loader2 size={16} className="animate-spin" /> : <Check size={16} />} {t("mfa.confirm")}
                </button>
                <button className="btn-secondary" disabled={busy} onClick={() => { setSetup(null); setCode(""); setError(null); }}>{t("adm.cancel")}</button>
              </div>
            </section>
          )
        )}

        {/* ---------- on: manage ---------- */}
        {status.enabled && !codes && (
          <section className="space-y-3 text-sm">
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
              {status.enabledAt && <><dt className="text-slate-500">{t("mfa.since")}</dt><dd>{dateTime(status.enabledAt)}</dd></>}
              <dt className="text-slate-500">{t("mfa.codesLeft")}</dt>
              <dd className={status.recoveryCodesLeft <= 3 ? "font-medium text-amber-700" : ""}>
                {status.recoveryCodesLeft}{status.recoveryCodesLeft <= 3 && ` · ${t("mfa.codesLow")}`}
              </dd>
            </dl>

            {mode === "idle" ? (
              <div className="flex flex-wrap gap-2">
                <button className="btn-secondary" onClick={() => { setMode("renew"); setError(null); }}><KeyRound size={16} /> {t("mfa.renew")}</button>
                {!status.required && (
                  <button className="btn-danger" onClick={() => { setMode("disable"); setError(null); }}><ShieldOff size={16} /> {t("mfa.disable")}</button>
                )}
              </div>
            ) : (
              <div className="space-y-3 rounded-lg border border-slate-200 p-3">
                <p className="text-slate-700">{mode === "renew" ? t("mfa.renewHint") : t("mfa.disableHint")}</p>
                {mode === "disable" && (
                  <label className="block">
                    <span className="mb-1 block text-slate-600">{t("pwd.current")}</span>
                    <input className="input" type="password" autoComplete="current-password" dir="ltr" value={password} onChange={(e) => setPassword(e.target.value)} />
                  </label>
                )}
                <label className="block">
                  <span className="mb-1 block text-slate-600">{t("mfa.login.code")}</span>
                  <input className="input w-44 text-center font-mono text-lg tracking-[0.4em]" dir="ltr" inputMode="numeric" autoComplete="one-time-code"
                    maxLength={7} placeholder="123456" value={code} onChange={(e) => setCode(e.target.value.replace(/[^\d ]/g, ""))} />
                </label>
                <div className="flex gap-2">
                  <button className={mode === "renew" ? "btn-primary" : "btn-danger"}
                    disabled={busy || digits(code).length !== 6 || (mode === "disable" && !password)}
                    onClick={() => void (mode === "renew" ? renew() : disable())}>
                    {busy && <Loader2 size={16} className="animate-spin" />} {mode === "renew" ? t("mfa.renew") : t("mfa.disable")}
                  </button>
                  <button className="btn-secondary" onClick={() => { setMode("idle"); setCode(""); setPassword(""); setError(null); }}>{t("adm.cancel")}</button>
                </div>
              </div>
            )}
            {status.required && <p className="text-xs text-slate-500">{t("mfa.requiredNote")}</p>}
          </section>
        )}

        <ErrorNote error={error} />
      </div>
    </main>
  );
}

export default function SecurityPage() {
  return (
    <Suspense>
      <SecurityInner />
    </Suspense>
  );
}
