"use client";

import { AUTH_MODE, authHeaders, refreshSession } from "./auth";

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

/** fetch with the current sign-in. On 401 (access token expired or revoked) refreshes once and retries. */
async function authedFetch(input: string, init: RequestInit = {}): Promise<Response> {
  const send = async () => {
    const headers = new Headers(init.headers);
    for (const [k, v] of Object.entries(await authHeaders())) headers.set(k, v);
    return fetch(input, { ...init, headers });
  };
  const r = await send();
  if (r.status === 401 && AUTH_MODE === "session" && (await refreshSession())) return send();
  return r;
}

/** Calls the .NET API through the Next.js proxy (/api/*) with the current sign-in. */
export async function api<T = unknown>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.body && !(init.body instanceof FormData) && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");

  const r = await authedFetch(`/api${path}`, { ...init, headers });
  if (!r.ok) {
    let message = r.statusText;
    try {
      const problem = (await r.json()) as { detail?: string; title?: string };
      message = problem.detail ?? problem.title ?? message;
    } catch {
      /* not JSON */
    }
    throw new ApiError(r.status, message);
  }
  // 204 and empty 202 have no body.
  const text = r.status === 204 ? "" : await r.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const post = <T,>(path: string, body?: unknown) =>
  api<T>(path, { method: "POST", body: body === undefined ? undefined : JSON.stringify(body) });

export const put = <T,>(path: string, body: unknown) => api<T>(path, { method: "PUT", body: JSON.stringify(body) });

/** Fetches a binary resource (the original document) as an object URL. Caller revokes it. */
export async function blobUrl(path: string): Promise<{ url: string; type: string }> {
  const r = await authedFetch(`/api${path}`);
  if (!r.ok) throw new ApiError(r.status, r.statusText);
  const blob = await r.blob();
  return { url: URL.createObjectURL(blob), type: blob.type };
}
