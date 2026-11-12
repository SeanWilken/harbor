import type { Disk, MachineSummary, NetInterface, Repo, Resources } from "./types";
import { ago } from "./format";
import type { Tone } from "./components/Mark";

/** A repo's state as type-coloured tags, most urgent first. */
export type Tag = { label: string; tone: Tone; title: string };

export function repoTags(r: Repo): Tag[] {
  const tags: Tag[] = [];
  const c = r.changes, b = r.branch;
  if (r.error) tags.push({ label: "error", tone: "danger", title: r.error });
  if (r.fetchError) tags.push({ label: "fetch failed", tone: "danger", title: r.fetchError });
  if (c && c.conflicted) tags.push({ label: `!${c.conflicted}`, tone: "danger", title: "conflicted" });
  if (c && c.staged) tags.push({ label: `+${c.staged}`, tone: "warn", title: "staged" });
  if (c && c.unstaged) tags.push({ label: `~${c.unstaged}`, tone: "warn", title: "modified, not staged" });
  if (c && c.untracked) tags.push({ label: `?${c.untracked}`, tone: "warn", title: "untracked" });
  if (b && b.ahead) tags.push({ label: `↑${b.ahead}`, tone: "info", title: "ahead of upstream: unpushed" });
  if (b && b.behind) tags.push({ label: `↓${b.behind}`, tone: "info", title: "behind upstream" });
  if (b && !b.head) tags.push({ label: "detached", tone: "dim", title: "detached HEAD" });
  if (r.remotes.length === 0) tags.push({ label: "local only", tone: "dim", title: "no remotes" });
  else if (b?.head && !b.upstream) tags.push({ label: "no upstream", tone: "dim", title: "branch tracks nothing" });
  return tags;
}

export const isDirty = (r: Repo) =>
  !!r.changes && r.changes.staged + r.changes.unstaged + r.changes.untracked + r.changes.conflicted > 0;
export const needsSync = (r: Repo) => !!r.branch && (r.branch.ahead > 0 || r.branch.behind > 0);

/** online / stale / pending / rejected, for a machine summary. Stale = missed ~3 pushes. */
export type Liveness = { key: "online" | "stale" | "pending" | "rejected"; label: string; tone: Tone; title: string };

export function liveness(m: MachineSummary, now = Date.now()): Liveness {
  const last = m.lastPushAt ? Date.parse(m.lastPushAt) : undefined;
  const since = last === undefined ? "never pushed" : `last push ${ago(m.lastPushAt, now)} ago`;
  if (m.status === "pending") return { key: "pending", label: "pending", tone: "special", title: `waiting for approval · ${since}` };
  if (m.status === "rejected") return { key: "rejected", label: "rejected", tone: "danger", title: since };
  const window = Math.max(3 * (m.pushSeconds ?? 60), 90) * 1000;
  // A live link means the agent is up even between pushes.
  return (last !== undefined && now - last < window) || m.connected
    ? { key: "online", label: "online", tone: "ok", title: since }
    : { key: "stale", label: `stale ${ago(m.lastPushAt, now)}`, tone: "dim", title: since };
}

/** Can harbor fetch it: has a remote and isn't a worktree (worktrees share their main repo's refs). */
export const isFetchable = (r: Repo) => r.remotes.length > 0 && !r.worktreeOf;

/** Keep the sorted order, but move each worktree right after its main repo when that is listed. */
export function withWorktrees(sorted: Repo[]): Repo[] {
  const listed = new Set(sorted.map((r) => r.path));
  const byMain = new Map<string, Repo[]>();
  for (const r of sorted) if (r.worktreeOf && listed.has(r.worktreeOf)) byMain.set(r.worktreeOf, [...(byMain.get(r.worktreeOf) ?? []), r]);
  return sorted.flatMap((r) =>
    r.worktreeOf && listed.has(r.worktreeOf) ? [] : [r, ...(byMain.get(r.path) ?? [])]);
}

/** The stalest fetch among fetchable repos: "ahead/behind as of …". */
export function oldestFetch(repos: Repo[]): string | undefined {
  const times = repos.filter(isFetchable).map((r) => r.lastFetch ?? "");
  return times.length ? times.sort()[0] || undefined : undefined;
}

/** working / idle from seconds since the last keyboard or mouse input (5 min threshold). */
export const activity = (idle?: number) =>
  idle === undefined ? undefined : idle < 300 ? "working" : "idle";

/** The address to show for a machine: tailnet IPv4 first, then a LAN one. */
export function primaryAddress(nets?: NetInterface[]): string | undefined {
  const v4 = (n?: NetInterface) => n?.addresses.find((a) => !a.address.includes(":"))?.address;
  return v4(nets?.find((n) => n.kind === "tailnet")) ?? v4(nets?.find((n) => n.gateway));
}

export const memPercent = (r?: Resources) =>
  r?.memTotalBytes && r.memAvailableBytes !== undefined ? (100 * (r.memTotalBytes - r.memAvailableBytes)) / r.memTotalBytes : undefined;

/** The fullest real disk: the one most likely to need attention. */
const used = (d: Disk) => (d.totalBytes ? 1 - d.freeBytes / d.totalBytes : 0);
export const fullestDisk = (r?: Resources) => r?.disks.reduce<Disk | undefined>((m, d) => (!m || used(d) > used(m) ? d : m), undefined);
