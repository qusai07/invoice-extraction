"use client";

import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Copy, Eye, EyeOff, KeyRound, LockOpen, LogOut, Pencil, ShieldCheck, ShieldOff, UserPlus, Wand2 } from "lucide-react";
import clsx from "clsx";
import { api, post, put } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";
import { Card, ErrorNote, Modal, PageHeader, Toast } from "@/components/ui";
import { roleDescription, roleName } from "@/lib/roles";

interface RoleRef { id: string; name: string }
interface User {
  id: string; email: string; displayName: string; isActive: boolean; roles: RoleRef[]; approvalLimit: number | null; approvalUnlimited: boolean;
  lastLoginAt: string | null; lockedUntil: string | null; mustChangePassword: boolean; createdAt: string; hasPassword: boolean;
  mfaEnabled: boolean;
}
interface Role { id: string; name: string; description: string; isSystem: boolean; permissions: string[]; userCount: number }

type LimitMode = "none" | "amount" | "unlimited";
interface Form { email: string; displayName: string; roleIds: string[]; limitMode: LimitMode; limit: string; isActive: boolean }

export default function UsersPage() {
  const { t, lang, money, dateTime } = useI18n();
  const { me } = useAuth();
  const qc = useQueryClient();
  const users = useQuery({ queryKey: ["admin", "users"], queryFn: () => api<User[]>("/admin/users") });
  const roles = useQuery({ queryKey: ["admin", "roles"], queryFn: () => api<Role[]>("/admin/roles") });

  const [search, setSearch] = useState("");
  const [editing, setEditing] = useState<User | "new" | null>(null);
  const [temp, setTemp] = useState<{ email: string; password: string; mustChange?: boolean } | null>(null);
  const [pwFor, setPwFor] = useState<User | null>(null);
  const [toast, setToast] = useState<{ message: string; kind: "info" | "error" } | null>(null);

  const refresh = () => qc.invalidateQueries({ queryKey: ["admin"] });
  const act = async (fn: () => Promise<unknown>, done: string) => {
    try { await fn(); await refresh(); setToast({ message: done, kind: "info" }); }
    catch (e) { setToast({ message: e instanceof Error ? e.message : String(e), kind: "error" }); }
  };

  const list = (users.data ?? []).filter((u) =>
    !search || `${u.displayName} ${u.email} ${u.roles.map((r) => r.name).join(" ")}`.toLowerCase().includes(search.toLowerCase()));

  return (
    <div className="mx-auto max-w-7xl p-4 lg:p-6">
      <PageHeader title={t("page.users.title")} intro={t("page.users.intro")}
        actions={<button className="btn-primary" onClick={() => setEditing("new")}><UserPlus size={16} /> {t("users.add")}</button>} />

      <Card>
        <input className="input mb-3 max-w-xs" placeholder={t("adm.search")} value={search} onChange={(e) => setSearch(e.target.value)} />
        <ErrorNote error={users.error} />
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="text-xs uppercase text-slate-500">
              <tr className="border-b border-slate-100">
                <th className="py-2 text-start font-medium">{t("users.name")}</th>
                <th className="py-2 text-start font-medium">{t("users.roles")}</th>
                <th className="py-2 text-start font-medium">{t("users.limit")}</th>
                <th className="py-2 text-start font-medium">{t("users.status")}</th>
                <th className="py-2 text-start font-medium">{t("users.lastLogin")}</th>
                <th className="py-2 text-end font-medium">{t("adm.actions")}</th>
              </tr>
            </thead>
            <tbody>
              {list.map((u) => {
                const self = u.id === me?.id;
                return (
                  <tr key={u.id} className="border-b border-slate-50 align-top">
                    <td className="py-2.5">
                      <p className="font-medium"><bdi dir="auto">{u.displayName}</bdi> {self && <span className="text-xs font-normal text-slate-400">({t("users.you")})</span>}</p>
                      <p className="text-xs text-slate-500" dir="ltr">{u.email}</p>
                    </td>
                    <td className="py-2.5">
                      <div className="flex flex-wrap gap-1">
                        {u.roles.map((r) => <span key={r.id} className="rounded bg-slate-100 px-1.5 py-0.5 text-xs">{roleName(r.name, lang)}</span>)}
                      </div>
                    </td>
                    <td className="py-2.5 ltr-value">
                      {u.approvalUnlimited ? t("users.limit.unlimited") : u.approvalLimit != null ? money(u.approvalLimit, "AED") : <span className="text-slate-400">—</span>}
                    </td>
                    <td className="py-2.5">
                      <div className="flex flex-wrap gap-1">
                        <Pill tone={u.isActive ? "green" : "slate"}>{u.isActive ? t("users.active") : t("users.inactive")}</Pill>
                        {u.lockedUntil && <Pill tone="red">{t("users.locked")}</Pill>}
                        {u.mustChangePassword && u.isActive && <Pill tone="amber">{t("users.mustChange")}</Pill>}
                        {u.hasPassword && (u.mfaEnabled
                          ? <Pill tone="green"><ShieldCheck size={11} className="-mt-0.5 me-0.5 inline" />{t("mfa.on.short")}</Pill>
                          : <Pill tone="slate">{t("mfa.off.short")}</Pill>)}
                      </div>
                    </td>
                    <td className="py-2.5 text-xs text-slate-500">{u.lastLoginAt ? dateTime(u.lastLoginAt) : t("adm.never")}</td>
                    <td className="py-2.5">
                      <div className="flex justify-end gap-1">
                        <IconButton title={t("adm.edit")} onClick={() => setEditing(u)}><Pencil size={14} /></IconButton>
                        {u.hasPassword && !self && (
                          <IconButton title={t("users.pw.action")} onClick={() => setPwFor(u)}><KeyRound size={14} /></IconButton>
                        )}
                        {!self && (
                          <IconButton title={t("users.signOut")} onClick={() => confirm(t("users.confirmSignOut")) &&
                            void act(() => post(`/admin/users/${u.id}/sign-out`), t("adm.saved"))}><LogOut size={14} /></IconButton>
                        )}
                        {u.mfaEnabled && !self && (
                          <IconButton title={t("mfa.reset")} onClick={() => confirm(t("mfa.resetConfirm")) &&
                            void act(() => post(`/admin/users/${u.id}/reset-mfa`), t("mfa.resetDone"))}><ShieldOff size={14} /></IconButton>
                        )}
                        {u.lockedUntil && (
                          <IconButton title={t("users.unlock")} onClick={() => void act(() => post(`/admin/users/${u.id}/unlock`), t("adm.saved"))}><LockOpen size={14} /></IconButton>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>

      {editing && (
        <UserDialog
          user={editing === "new" ? null : editing}
          roles={roles.data ?? []}
          self={editing !== "new" && editing.id === me?.id}
          onClose={() => setEditing(null)}
          onSaved={async (created) => {
            setEditing(null);
            await refresh();
            if (created) setTemp(created);
            else setToast({ message: t("adm.saved"), kind: "info" });
          }}
        />
      )}

      {pwFor && (
        <PasswordDialog user={pwFor} onClose={() => setPwFor(null)}
          onDone={async (result) => { setPwFor(null); await refresh(); setTemp(result); }} />
      )}

      {temp && (
        <Modal title={t("users.temp.title")} onClose={() => setTemp(null)} footer={<button className="btn-primary" onClick={() => setTemp(null)}>{t("common.close")}</button>}>
          <p className="mb-3 text-sm text-slate-600">{temp.mustChange === false ? t("users.pw.doneKeep") : t("users.temp.body")}</p>
          <p className="mb-1 text-xs text-slate-500" dir="ltr">{temp.email}</p>
          <div className="flex items-center gap-2 rounded-md bg-slate-100 px-3 py-2">
            <code className="flex-1 text-lg tracking-wider" dir="ltr">{temp.password}</code>
            <button className="btn-secondary px-2 py-1 text-xs" onClick={() => void navigator.clipboard.writeText(temp.password)}>
              <Copy size={14} /> {t("adm.copy")}
            </button>
          </div>
        </Modal>
      )}

      {toast && <Toast message={toast.message} kind={toast.kind} onDone={() => setToast(null)} />}
    </div>
  );
}

function UserDialog({ user, roles, self, onClose, onSaved }: {
  user: User | null; roles: Role[]; self: boolean; onClose: () => void; onSaved: (created?: { email: string; password: string }) => void;
}) {
  const { t, lang } = useI18n();
  const [form, setForm] = useState<Form>(() => ({
    email: user?.email ?? "",
    displayName: user?.displayName ?? "",
    roleIds: user?.roles.map((r) => r.id) ?? [],
    limitMode: user?.approvalUnlimited ? "unlimited" : user?.approvalLimit != null ? "amount" : "none",
    limit: user?.approvalLimit?.toString() ?? "",
    isActive: user?.isActive ?? true,
  }));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const save = async () => {
    setBusy(true);
    setError(null);
    const body = {
      email: form.email.trim(),
      displayName: form.displayName.trim(),
      roleIds: form.roleIds,
      approvalLimit: form.limitMode === "amount" ? Number(form.limit) : null,
      approvalUnlimited: form.limitMode === "unlimited",
      isActive: form.isActive,
    };
    try {
      if (user) {
        await put(`/admin/users/${user.id}`, body);
        onSaved();
      } else {
        const r = await post<{ id: string; temporaryPassword: string }>("/admin/users", body);
        onSaved({ email: body.email, password: r.temporaryPassword });
      }
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const valid = form.displayName.trim() && form.email.includes("@") && (form.limitMode !== "amount" || Number(form.limit) >= 0 && form.limit !== "");

  return (
    <Modal wide title={user ? t("users.edit") : t("users.new")} onClose={onClose}
      footer={<>
        <button className="btn-secondary" onClick={onClose}>{t("adm.cancel")}</button>
        <button className="btn-primary" disabled={!valid || busy} onClick={() => void save()}>{user ? t("adm.save") : t("adm.create")}</button>
      </>}>
      <div className="space-y-4">
        {self && <p className="rounded bg-amber-50 px-3 py-2 text-sm text-amber-800">{t("users.selfNote")}</p>}
        <div className="grid gap-3 sm:grid-cols-2">
          <label className="block">
            <span className="mb-1 block text-sm text-slate-600">{t("users.name")}</span>
            <input className="input" dir="auto" value={form.displayName} onChange={(e) => setForm({ ...form, displayName: e.target.value })} />
          </label>
          <label className="block">
            <span className="mb-1 block text-sm text-slate-600">{t("users.email")}</span>
            <input className="input" type="email" dir="ltr" disabled={!!user} value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} />
          </label>
        </div>

        <fieldset disabled={self}>
          <legend className="mb-1 text-sm text-slate-600">{t("users.roles")}</legend>
          <div className="grid gap-2 sm:grid-cols-2">
            {roles.map((r) => (
              <label key={r.id} className={clsx("flex cursor-pointer gap-2 rounded-md border p-2.5 text-sm",
                form.roleIds.includes(r.id) ? "border-brand-500 bg-brand-50" : "border-slate-200 hover:bg-slate-50")}>
                <input type="checkbox" className="mt-0.5" checked={form.roleIds.includes(r.id)}
                  onChange={(e) => setForm({ ...form, roleIds: e.target.checked ? [...form.roleIds, r.id] : form.roleIds.filter((x) => x !== r.id) })} />
                <span><span className="font-medium">{roleName(r.name, lang)}</span><span className="block text-xs text-slate-500" dir="auto">{roleDescription(r.name, r.description, lang)}</span></span>
              </label>
            ))}
          </div>
        </fieldset>

        <fieldset disabled={self}>
          <legend className="mb-1 text-sm text-slate-600">{t("users.limit")}</legend>
          <div className="flex flex-wrap gap-2">
            {(["none", "amount", "unlimited"] as const).map((m) => (
              <label key={m} className={clsx("flex cursor-pointer items-center gap-2 rounded-md border px-3 py-1.5 text-sm",
                form.limitMode === m ? "border-brand-500 bg-brand-50" : "border-slate-200")}>
                <input type="radio" name="limit" checked={form.limitMode === m} onChange={() => setForm({ ...form, limitMode: m })} />
                {t(`users.limit.${m}`)}
              </label>
            ))}
            {form.limitMode === "amount" && (
              <input className="input w-40" dir="ltr" inputMode="decimal" placeholder="250000" value={form.limit}
                onChange={(e) => setForm({ ...form, limit: e.target.value.replace(/[^\d.]/g, "") })} />
            )}
          </div>
          <p className="mt-1 text-xs text-slate-500">{t("users.limit.hint")}</p>
        </fieldset>

        {user && !self && (
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} />
            {t("users.active")}
          </label>
        )}
        <ErrorNote error={error} />
      </div>
    </Modal>
  );
}

function PasswordDialog({ user, onClose, onDone }: {
  user: User; onClose: () => void; onDone: (result: { email: string; password: string; mustChange: boolean }) => void;
}) {
  const { t } = useI18n();
  const [mode, setMode] = useState<"generate" | "type">("generate");
  const [password, setPassword] = useState("");
  const [confirmPw, setConfirmPw] = useState("");
  const [show, setShow] = useState(false);
  const [mustChange, setMustChange] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  const tooShort = mode === "type" && password.length > 0 && password.length < 12;
  const mismatch = mode === "type" && confirmPw.length > 0 && confirmPw !== password;
  const valid = mode === "generate" || (password.length >= 12 && password === confirmPw);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await post<{ temporaryPassword: string }>(`/admin/users/${user.id}/reset-password`,
        { password: mode === "type" ? password : null, mustChange });
      onDone({ email: user.email, password: r.temporaryPassword, mustChange });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal title={t("users.pw.title")} onClose={onClose}
      footer={<>
        <button className="btn-secondary" onClick={onClose}>{t("adm.cancel")}</button>
        <button className="btn-primary" disabled={!valid || busy} onClick={() => void submit()}><KeyRound size={16} /> {t("users.pw.submit")}</button>
      </>}>
      <div className="space-y-4 text-sm">
        <p className="text-slate-600"><bdi dir="auto" className="font-medium text-slate-800">{user.displayName}</bdi> · <span dir="ltr">{user.email}</span></p>

        <div className="grid gap-2 sm:grid-cols-2">
          {(["generate", "type"] as const).map((m) => (
            <label key={m} className={clsx("flex cursor-pointer gap-2 rounded-md border p-2.5",
              mode === m ? "border-brand-500 bg-brand-50" : "border-slate-200 hover:bg-slate-50")}>
              <input type="radio" name="pwmode" className="mt-0.5" checked={mode === m} onChange={() => setMode(m)} />
              <span>
                <span className="flex items-center gap-1.5 font-medium">{m === "generate" ? <Wand2 size={14} /> : <KeyRound size={14} />} {t(`users.pw.${m}`)}</span>
                <span className="block text-xs text-slate-500">{t(`users.pw.${m}.hint`)}</span>
              </span>
            </label>
          ))}
        </div>

        {mode === "type" && (
          <div className="space-y-3">
            <label className="block">
              <span className="mb-1 block text-slate-600">{t("users.pw.new")}</span>
              <div className="relative">
                <input className="input pe-10" dir="ltr" type={show ? "text" : "password"} autoComplete="new-password" autoFocus
                  value={password} onChange={(e) => setPassword(e.target.value)} />
                <button type="button" className="absolute inset-y-0 end-0 px-3 text-slate-500 hover:text-slate-800"
                  title={show ? t("users.pw.hide") : t("users.pw.show")} aria-label={show ? t("users.pw.hide") : t("users.pw.show")}
                  onClick={() => setShow(!show)}>{show ? <EyeOff size={16} /> : <Eye size={16} />}</button>
              </div>
              <span className={clsx("mt-1 block text-xs", tooShort ? "text-red-700" : "text-slate-500")}>{t("users.pw.rule")}</span>
            </label>
            <label className="block">
              <span className="mb-1 block text-slate-600">{t("users.pw.confirm")}</span>
              <input className="input" dir="ltr" type={show ? "text" : "password"} autoComplete="new-password"
                value={confirmPw} onChange={(e) => setConfirmPw(e.target.value)} />
              {mismatch && <span className="mt-1 block text-xs text-red-700">{t("users.pw.mismatch")}</span>}
            </label>
          </div>
        )}

        <label className="flex items-start gap-2">
          <input type="checkbox" className="mt-0.5" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} />
          <span>{t("users.pw.mustChange")}<span className="block text-xs text-slate-500">{t("users.pw.mustChange.hint")}</span></span>
        </label>

        <p className="rounded bg-amber-50 px-3 py-2 text-amber-800">{t("users.pw.effect")}</p>
        <ErrorNote error={error} />
      </div>
    </Modal>
  );
}

function Pill({ tone, children }: { tone: "green" | "slate" | "red" | "amber"; children: React.ReactNode }) {
  const cls = { green: "bg-emerald-50 text-emerald-700", slate: "bg-slate-100 text-slate-600", red: "bg-red-50 text-red-700", amber: "bg-amber-50 text-amber-800" }[tone];
  return <span className={`rounded-full px-2 py-0.5 text-xs ${cls}`}>{children}</span>;
}

function IconButton({ title, onClick, children }: { title: string; onClick: () => void; children: React.ReactNode }) {
  return (
    <button title={title} aria-label={title} onClick={onClick} className="rounded p-1.5 text-slate-500 hover:bg-slate-100 hover:text-slate-900">
      {children}
    </button>
  );
}
