"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import {
  Activity, ChevronDown, FileText, Inbox, KeyRound, Languages, LayoutDashboard, ListChecks, LogOut, Menu,
  MonitorOff, ScrollText, ShieldCheck, Truck, Upload, Users, X,
} from "lucide-react";
import clsx from "clsx";
import { AUTH_MODE, requiredAccountStep, useAuth } from "@/lib/auth";
import { useI18n, type I18nKey } from "@/lib/i18n";
import { roleName } from "@/lib/roles";

interface NavItem { href: string; label: I18nKey; icon: React.ComponentType<{ size?: number; className?: string }>; show: (p: string[]) => boolean }
interface NavSection { label: I18nKey; items: NavItem[] }

const any = (...codes: string[]) => (p: string[]) => codes.some((c) => p.includes(c));

const NAV: NavSection[] = [
  {
    label: "nav.section.overview",
    items: [{ href: "/dashboard", label: "nav.dashboard", icon: LayoutDashboard, show: () => true }],
  },
  {
    label: "nav.section.invoices",
    items: [
      { href: "/upload", label: "nav.upload", icon: Upload, show: any("invoice.submit") },
      { href: "/queue", label: "nav.invoices", icon: Inbox, show: any("invoice.read.all", "invoice.read.own") },
    ],
  },
  {
    label: "nav.section.admin",
    items: [
      { href: "/admin/users", label: "nav.users", icon: Users, show: any("admin.users") },
      { href: "/admin/roles", label: "nav.roles", icon: ShieldCheck, show: any("admin.roles", "admin.users") },
      { href: "/admin/rules", label: "nav.rules", icon: ListChecks, show: any("admin.rules", "audit.read") },
      { href: "/admin/suppliers", label: "nav.suppliers", icon: Truck, show: any("admin.suppliers") },
      { href: "/admin/audit", label: "nav.audit", icon: ScrollText, show: any("audit.read", "admin.users") },
      { href: "/admin/system", label: "nav.system", icon: Activity, show: any("admin.system") },
    ],
  },
];

export function Shell({ children }: { children: React.ReactNode }) {
  const { t, lang } = useI18n();
  const { me, loading } = useAuth();
  const router = useRouter();
  const pathname = usePathname();
  const [drawer, setDrawer] = useState(false);

  useEffect(() => {
    if (loading) return;
    if (!me) router.replace(`/login?next=${encodeURIComponent(pathname)}`);
    else {
      const step = requiredAccountStep(me, pathname);
      if (step) router.replace(step);
    }
  }, [loading, me, pathname, router]);

  useEffect(() => setDrawer(false), [pathname]);

  if (!me || me.mustChangePassword || me.mfaSetupRequired) {
    return <div className="p-8 text-slate-500">{t("common.loading")}</div>;
  }

  return (
    <div className="flex min-h-screen">
      {/* sidebar: fixed on large screens, drawer on small */}
      <aside className={clsx(
        "fixed inset-y-0 start-0 z-30 flex w-60 flex-col border-e border-slate-200 bg-white transition-transform lg:translate-x-0",
        drawer ? "translate-x-0" : "-translate-x-full rtl:translate-x-full lg:rtl:translate-x-0",
      )}>
        <div className="flex h-12 items-center gap-2 border-b border-slate-100 px-4">
          <FileText size={18} className="text-brand-500" />
          <Link href="/dashboard" className="font-semibold text-brand-600">{t("appName")}</Link>
          <button className="ms-auto text-slate-500 lg:hidden" onClick={() => setDrawer(false)} aria-label={t("common.close")}><X size={18} /></button>
        </div>
        <nav className="flex-1 overflow-y-auto px-2 py-3">
          {NAV.map((section) => {
            const items = section.items.filter((i) => i.show(me.permissions));
            if (items.length === 0) return null;
            return (
              <div key={section.label} className="mb-4">
                <p className="mb-1 px-3 text-[11px] font-semibold uppercase tracking-wide text-slate-400">{t(section.label)}</p>
                <ul className="space-y-0.5">
                  {items.map((item) => {
                    const active = pathname === item.href || pathname.startsWith(item.href + "/") ||
                      (item.href === "/queue" && pathname.startsWith("/invoices/"));
                    const Icon = item.icon;
                    return (
                      <li key={item.href}>
                        <Link href={item.href} className={clsx(
                          "flex items-center gap-2.5 rounded-md px-3 py-2 text-sm",
                          active ? "bg-brand-50 font-medium text-brand-700" : "text-slate-600 hover:bg-slate-50 hover:text-slate-900",
                        )}>
                          <Icon size={16} className={active ? "text-brand-600" : "text-slate-400"} />
                          {t(item.label)}
                        </Link>
                      </li>
                    );
                  })}
                </ul>
              </div>
            );
          })}
        </nav>
        <div className="border-t border-slate-100 px-4 py-3 text-xs text-slate-400">
          {me.roles.map((r) => roleName(r, lang)).join(" · ") || "—"}
        </div>
      </aside>
      {drawer && <div className="fixed inset-0 z-20 bg-black/20 lg:hidden" onClick={() => setDrawer(false)} />}

      <div className="flex min-w-0 flex-1 flex-col lg:ms-60">
        <header className="sticky top-0 z-10 flex h-12 items-center gap-3 border-b border-slate-200 bg-white/95 px-4 backdrop-blur">
          <button className="text-slate-600 lg:hidden" onClick={() => setDrawer(true)} aria-label={t("nav.menu")}><Menu size={20} /></button>
          <div className="ms-auto flex items-center gap-3 text-sm">
            <LanguageButton />
            <UserMenu />
          </div>
        </header>
        {me.permissions.length === 0
          ? <p className="p-6 text-amber-700">{t("auth.noRole")}</p>
          : <main className="flex-1">{children}</main>}
      </div>
    </div>
  );
}

function LanguageButton() {
  const { t, toggle } = useI18n();
  return (
    <button className="flex items-center gap-1 rounded px-2 py-1 text-slate-600 hover:bg-slate-100 hover:text-slate-900" onClick={toggle}>
      <Languages size={16} /> {t("common.language")}
    </button>
  );
}

function UserMenu() {
  const { t, lang } = useI18n();
  const { me, signOut, signOutEverywhere } = useAuth();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener("mousedown", close);
    return () => document.removeEventListener("mousedown", close);
  }, [open]);

  if (!me) return null;
  const initials = me.name.split(/\s+/).map((w) => w[0]).join("").slice(0, 2).toUpperCase();

  return (
    <div ref={ref} className="relative">
      <button className="flex items-center gap-2 rounded px-1.5 py-1 hover:bg-slate-100" onClick={() => setOpen((o) => !o)} aria-haspopup="menu" aria-expanded={open}>
        <span className="flex h-7 w-7 items-center justify-center rounded-full bg-brand-500 text-xs font-semibold text-white">{initials}</span>
        <span className="hidden text-slate-700 sm:inline">{me.name}</span>
        <ChevronDown size={14} className="text-slate-500" />
      </button>
      {open && (
        <div role="menu" className="card absolute end-0 z-30 mt-1 w-64 py-1 shadow-lg">
          <div className="border-b border-slate-100 px-3 py-2">
            <p className="font-medium" dir="auto">{me.name}</p>
            {me.email && <p className="truncate text-xs text-slate-500" dir="ltr">{me.email}</p>}
            <p className="mt-0.5 text-xs text-slate-500">{me.roles.map((r) => roleName(r, lang)).join(lang === "ar" ? "، " : ", ")}</p>
          </div>
          {AUTH_MODE === "session" && (
            <>
              <Link role="menuitem" href="/account/password" className="flex items-center gap-2 px-3 py-2 hover:bg-slate-50" onClick={() => setOpen(false)}>
                <KeyRound size={14} /> {t("auth.changePassword")}
              </Link>
              <Link role="menuitem" href="/account/security" className="flex items-center gap-2 px-3 py-2 hover:bg-slate-50" onClick={() => setOpen(false)}>
                <ShieldCheck size={14} /> {t("mfa.menu")}
                <span className={me.mfaEnabled ? "ms-auto text-xs text-emerald-700" : "ms-auto text-xs text-slate-400"}>{me.mfaEnabled ? t("mfa.on") : t("mfa.off")}</span>
              </Link>
              <button role="menuitem" className="flex w-full items-center gap-2 px-3 py-2 text-start hover:bg-slate-50" onClick={() => void signOutEverywhere()}>
                <MonitorOff size={14} /> {t("auth.signOutEverywhere")}
              </button>
            </>
          )}
          <button role="menuitem" className="flex w-full items-center gap-2 px-3 py-2 text-start text-red-700 hover:bg-red-50" onClick={() => void signOut()}>
            <LogOut size={14} /> {t("auth.signOut")}
          </button>
        </div>
      )}
    </div>
  );
}
