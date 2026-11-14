import type { JobInfo, MachineSummary, Manifest, ManifestDiff, ManifestInfo, Me, RecapInfo, RecapReport, SharedRepo, Snapshot } from "./types";

async function get<T>(url: string): Promise<T> {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`${url}: HTTP ${r.status}`);
  return r.json() as Promise<T>;
}

async function post(url: string, body?: unknown): Promise<void> {
  const r = await fetch(url, body === undefined ? { method: "POST" }
    : { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
  if (!r.ok) {
    const body = await r.json().catch(() => null) as { message?: string } | null;
    throw new Error(body?.message ?? `${url}: HTTP ${r.status}`);
  }
}

export const fetchMachines = () => get<MachineSummary[]>("/api/machines");
export const fetchSnapshot = (id: number) => get<Snapshot>(`/api/machines/${id}/snapshot`);
export const fetchShared = () => get<SharedRepo[]>("/api/shared");
export const setStatus = (id: number, verb: "approve" | "reject") => post(`/api/machines/${id}/${verb}`);
/** Rescans the hub's own machine (mode `both`); remote agents rescan on their own timer. */
export const rescan = () => post("/api/scan");
/** Every linked machine rescans and pushes now. */
export const syncAll = () => post("/api/sync");
export const fetchJobs = (id: number) => get<JobInfo[]>(`/api/machines/${id}/jobs`);
/** Queue a `git fetch` on that machine: one repo, or all of them when path is omitted. */
export const requestFetch = (id: number, path?: string) => post(`/api/machines/${id}/jobs`, { kind: "fetch", path });
/** A read-only diff from the machine that has the work: a commit sha or "worktree". Admins only. */
export const requestDiff = async (id: number, path: string, arg: string): Promise<number> => {
  const r = await fetch(`/api/machines/${id}/jobs`, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ kind: "diff", path, arg }) });
  const body = await r.json().catch(() => null) as { id?: number; message?: string } | null;
  if (!r.ok || body?.id === undefined) throw new Error(body?.message ?? `diff: HTTP ${r.status}`);
  return body.id;
};
/** Switch a machine to one of the roles its config declares (admins; runs that role's actions there). */
export const requestRole = (id: number, role: string) => post(`/api/machines/${id}/jobs`, { kind: "role", arg: role });
export const requestPull = (id: number, path?: string) => post(`/api/machines/${id}/jobs`, { kind: "pull", path });
export const setHidden = (id: number, path: string, hidden: boolean) => post(`/api/machines/${id}/hidden`, { path, hidden });
export const fetchMe = () => get<Me>("/api/me");
export const fetchManifests = (machineId: number) => get<ManifestInfo[]>(`/api/machines/${machineId}/manifests`);
export const fetchManifest = (id: number) => get<Manifest>(`/api/manifests/${id}`);
export const fetchDiff = (a: number, b: number) => get<ManifestDiff>(`/api/manifests/${a}/diff/${b}`);
export const fetchRecap = (days = 7) => get<RecapReport>(`/api/recap?days=${days}`);
export const fetchRecaps = () => get<RecapInfo[]>("/api/recaps");
export const fetchStoredRecap = (id: number) => get<RecapReport>(`/api/recaps/${id}`);
/** Markdown exports open as plain pages (download or share from the browser). */
export const manifestMarkdownUrl = (id: number) => `/api/manifests/${id}?format=md`;
export const recapMarkdownUrl = (id?: number, days = 7) => (id ? `/api/recaps/${id}?format=md` : `/api/recap?days=${days}&format=md`);
export type PairingCode = { code: string; expiresAt: string; minutes: number };
export const createPairingCode = async (): Promise<PairingCode> => {
  const r = await fetch("/api/pairing", { method: "POST" });
  if (!r.ok) throw new Error((await r.json().catch(() => null) as { message?: string } | null)?.message ?? `HTTP ${r.status}`);
  return r.json() as Promise<PairingCode>;
};
export const pairingState = (code: string) => get<{ expiresAt: string; usedBy?: string }>(`/api/pairing/${encodeURIComponent(code)}`);
/** Ask the machine's agent to rescan and push now (the hub rate-limits this). */
export const requestRefresh = (id: number) => post(`/api/machines/${id}/refresh`);
