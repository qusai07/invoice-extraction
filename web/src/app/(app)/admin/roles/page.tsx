"use client";

import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, Lock, Plus, Trash2 } from "lucide-react";
import clsx from "clsx";
import { api, post, put } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { useI18n } from "@/lib/i18n";
import { PERMISSIONS } from "@/lib/permissions";
import { Card, ErrorNote, PageHeader, Toast } from "@/components/ui";
import { SUPER_ADMIN, roleDescription, roleName } from "@/lib/roles";

interface Role { id: string; name: string; description: string; isSystem: boolean; permissions: string[]; userCount: number }

const GROUPS = ["invoices", "audit", "admin"] as const;

export default function RolesPage() {
  const { t, lang } = useI18n();
  const { me } = useAuth();
  const canEdit = !!me?.permissions.includes("admin.roles");
  const qc = useQueryClient();
  const roles = useQuery({ queryKey: ["admin", "roles"], queryFn: () => api<Role[]>("/admin/roles") });

  const [selected, setSelected] = useState<string | "new" | null>(null);
  const [draft, setDraft] = useState<{ name: string; description: string; permissions: string[] }>({ name: "", description: "", permissions: [] });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [toast, setToast] = useState<string | null>(null);

  const role = roles.data?.find((r) => r.id === selected) ?? null;

  useEffect(() => {
    if (!selected && roles.data?.[0]) setSelected(roles.data[0].id);
  }, [roles.data, selected]);

  useEffect(() => {
    setError(null);
    if (selected === "new") setDraft({ name: "", description: "", permissions: [] });
    else if (role) setDraft({ name: role.name, description: role.description, permissions: role.permissions });
  }, [selected, role]);

  const dirty = selected === "new" || (role && (draft.name !== role.name || draft.description !== role.description ||
    draft.permissions.slice().sort().join() !== role.permissions.slice().sort().join()));

  const save = async () => {
    setBusy(true);
    setError(null);
    try {
      if (selected === "new") {
        const r = await post<{ id: string }>("/admin/roles", draft);
        await qc.invalidateQueries({ queryKey: ["admin"] });
        setSelected(r.id);
      } else if (role) {
        await put(`/admin/roles/${role.id}`, draft);
        await qc.invalidateQueries({ queryKey: ["admin"] });
      }
      setToast(t("adm.saved"));
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!role || !confirm(t("roles.deleteConfirm"))) return;
    try {
      await api(`/admin/roles/${role.id}`, { method: "DELETE" });
      setSelected(null);
      await qc.invalidateQueries({ queryKey: ["admin"] });
    } catch (e) {
      setError(e);
    }
  };

  return (
    <div className="mx-auto max-w-7xl p-4 lg:p-6">
      <PageHeader title={t("page.roles.title")} intro={t("page.roles.intro")}
        actions={canEdit && <button className="btn-primary" onClick={() => setSelected("new")}><Plus size={16} /> {t("roles.add")}</button>} />
      {!canEdit && <p className="mb-4 rounded bg-slate-100 px-3 py-2 text-sm text-slate-600">{t("adm.readOnly")}</p>}

      <div className="grid gap-4 lg:grid-cols-[16rem_minmax(0,1fr)]">
        <Card className="h-fit p-2">
          <ul className="space-y-0.5">
            {(roles.data ?? []).map((r) => (
              <li key={r.id}>
                <button onClick={() => setSelected(r.id)} className={clsx("flex w-full items-center gap-2 rounded-md px-3 py-2 text-start text-sm",
                  selected === r.id ? "bg-brand-50 font-medium text-brand-700" : "hover:bg-slate-50")}>
                  <span className="flex-1">{roleName(r.name, lang)}</span>
                  {r.isSystem && <Lock size={12} className="text-slate-400" aria-label={t("roles.system")} />}
                  <span className="text-xs text-slate-400">{r.userCount}</span>
                </button>
              </li>
            ))}
          </ul>
        </Card>

        {(role || selected === "new") && (
          <Card>
            <fieldset disabled={!canEdit} className="space-y-4">
              {role?.name === SUPER_ADMIN
                ? <p className="rounded bg-amber-50 px-3 py-2 text-xs text-amber-800">{t("roles.superNote")}</p>
                : role?.isSystem && <p className="rounded bg-slate-50 px-3 py-2 text-xs text-slate-600">{t("roles.systemNote")}</p>}
              <div className="grid gap-3 sm:grid-cols-2">
                <label className="block">
                  <span className="mb-1 block text-sm text-slate-600">{t("roles.name")}</span>
                  <input className="input" dir="auto" disabled={role?.isSystem} value={role?.isSystem ? roleName(draft.name, lang) : draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
                </label>
                <label className="block">
                  <span className="mb-1 block text-sm text-slate-600">{t("roles.description")}</span>
                  <input className="input" dir="auto" value={role?.isSystem ? roleDescription(draft.name, draft.description, lang) : draft.description} onChange={(e) => setDraft({ ...draft, description: e.target.value })} />
                </label>
              </div>

              {GROUPS.map((g) => (
                <div key={g}>
                  <h3 className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400">{t(`perm.group.${g}`)}</h3>
                  <div className="grid gap-2 md:grid-cols-2">
                    {PERMISSIONS.filter((p) => p.group === g).map((p) => {
                      const on = draft.permissions.includes(p.code);
                      return (
                        <label key={p.code} className={clsx("flex cursor-pointer gap-2.5 rounded-md border p-2.5 text-sm",
                          on ? "border-brand-500 bg-brand-50" : "border-slate-200 hover:bg-slate-50")}>
                          <input type="checkbox" className="mt-0.5" checked={on} disabled={role?.name === SUPER_ADMIN}
                            onChange={(e) => setDraft({ ...draft, permissions: e.target.checked ? [...draft.permissions, p.code] : draft.permissions.filter((x) => x !== p.code) })} />
                          <span>
                            <span className="font-medium">{p[lang][0]}</span>
                            <span className="block text-xs text-slate-500">{p[lang][1]}</span>
                          </span>
                        </label>
                      );
                    })}
                  </div>
                </div>
              ))}

              <ErrorNote error={error} />
              {canEdit && (
                <div className="flex items-center gap-2">
                  <button className="btn-primary" disabled={!dirty || busy || !draft.name.trim()} onClick={() => void save()}>
                    {selected === "new" ? t("adm.create") : t("adm.save")}
                  </button>
                  {role && !role.isSystem && (
                    <button className="btn-danger ms-auto" disabled={role.userCount > 0} title={role.userCount > 0 ? `${role.userCount} ${t("roles.users")}` : undefined}
                      onClick={() => void remove()}><Trash2 size={14} /> {t("adm.delete")}</button>
                  )}
                </div>
              )}
            </fieldset>
          </Card>
        )}
      </div>

      <Card title={t("roles.matrix")} className="mt-4">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-slate-100 text-xs text-slate-500">
                <th className="py-2 text-start font-medium" />
                {(roles.data ?? []).map((r) => <th key={r.id} className="px-2 py-2 text-center font-medium">{roleName(r.name, lang)}</th>)}
              </tr>
            </thead>
            <tbody>
              {PERMISSIONS.map((p) => (
                <tr key={p.code} className="border-b border-slate-50">
                  <td className="py-1.5 pe-3">{p[lang][0]}</td>
                  {(roles.data ?? []).map((r) => (
                    <td key={r.id} className="px-2 py-1.5 text-center">
                      {r.permissions.includes(p.code) ? <Check size={16} className="inline text-emerald-600" aria-label="yes" /> : <span className="text-slate-300">·</span>}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>

      {toast && <Toast message={toast} onDone={() => setToast(null)} />}
    </div>
  );
}
