import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { JobInfo, MachineSummary, Me, Repo, SharedRepo, Snapshot } from "./types";
import { fetchJobs, fetchMachines, fetchMe, fetchShared, fetchSnapshot, requestFetch, requestPull, requestRefresh, requestRole, rescan, syncAll, setHidden, setStatus } from "./api";
import { isDirty, isFetchable, pullBlocker, withWorktrees } from "./status";
import { Header } from "./components/Header";
import { DevicesDialog } from "./components/DevicesDialog";
import { parseHash, sameTab, tabHash, Tabs, type Tab } from "./components/Tabs";
import { MachinePanel } from "./components/MachinePanel";
import { NET_VIEWS, NetworkView, type NetView } from "./components/NetworkView";
import { PendingView } from "./components/PendingView";
import { ServicesTable } from "./components/ServicesTable";
import { DisksTable } from "./components/DisksTable";
import { PackagesView } from "./components/PackagesView";
import { RepoRow, type RepoActions } from "./components/RepoRow";

const FILTERS = {
  all: () => true,
  dirty: isDirty,
  behind: (r: Repo) => (r.branch?.behind ?? 0) > 0,
  unpushed: (r: Repo) => (r.branch?.ahead ?? 0) > 0,
  local: (r: Repo) => r.remotes.length === 0,
  hidden: () => true, // the hidden list itself; see `repos` below
} satisfies Record<string, (r: Repo) => boolean>;
type Filter = keyof typeof FILTERS;

const SORTS = {
  name: (a: Repo, b: Repo) => a.name.localeCompare(b.name),
  commit: (a: Repo, b: Repo) => (b.lastCommit?.date ?? "").localeCompare(a.lastCommit?.date ?? ""),
  modified: (a: Repo, b: Repo) => (b.lastModified ?? "").localeCompare(a.lastModified ?? ""),
};
type Sort = keyof typeof SORTS;

const CONTENT = ["repos", "packages", "services", "storage"] as const;
type Content = (typeof CONTENT)[number];

const stored = (k: string, d: string) => { try { return localStorage.getItem(k) ?? d; } catch { return d; } };
const store = (k: string, v: string) => { try { localStorage.setItem(k, v); } catch { /* private mode */ } };
const storedPins = (): number[] => { try { return (JSON.parse(stored("harbor.pins", "[]")) as unknown[]).filter((x): x is number => typeof x === "number"); } catch { return []; } };

function viewerMachine(machines: MachineSummary[] | null, me: Me | undefined) {
  if (!machines) return undefined;
  if (!me || me.self) return machines.find((m) => m.local);
  return me.node ? machines.find((m) => m.nodeName === me.node && m.status === "approved") : undefined;
}

export function App() {
  const [machines, setMachines] = useState<MachineSummary[] | null>(null);
  const [me, setMe] = useState<Me>();
  // The hash links straight to a view (#this, #network/map, #pending, #m/<id>): handy as iPad bookmarks.
  const [tab, setTab] = useState<Tab>(() => parseHash(location.hash) ?? parseHash(stored("harbor.tab", "#this")) ?? { kind: "this" });
  const [netView, setNetView] = useState<NetView>(() => {
    const v = location.hash.split("/")[1] ?? stored("harbor.net", "grid");
    return (NET_VIEWS as string[]).includes(v) ? (v as NetView) : "grid";
  });
  const [content, setContent] = useState<Content>(() => {
    const c = stored("harbor.content", "repos");
    return (CONTENT as readonly string[]).includes(c) ? (c as Content) : "repos";
  });
  const [pins, setPins] = useState<number[]>(storedPins);
  const [shared, setShared] = useState<SharedRepo[]>([]);
  const [snap, setSnap] = useState<Snapshot | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const [now, setNow] = useState(Date.now());
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [sort, setSort] = useState<Sort>("modified");
  const [sel, setSel] = useState(0);
  const [open, setOpen] = useState<string | null>(null);
  const [jobs, setJobs] = useState<JobInfo[]>([]);
  const [devices, setDevices] = useState(false);
  const search = useRef<HTMLInputElement>(null);

  // "This machine" is the one the browser runs on: the hub's own (same host) or an agent's, by Tailscale node.
  const local = useMemo(() => viewerMachine(machines, me), [machines, me]);
  const current = useMemo(() =>
    tab.kind === "this" ? local : tab.kind === "machine" ? machines?.find((m) => m.id === tab.id) : undefined,
  [tab, machines, local]);

  const load = useCallback(async () => {
    try {
      setMachines(await fetchMachines());
      setShared(await fetchShared());
      setErr(null);
      setNow(Date.now());
    } catch (e) { setErr((e as Error).message); }
  }, []);
  useEffect(() => { void load(); const t = setInterval(load, 10_000); return () => clearInterval(t); }, [load]);
  useEffect(() => { fetchMe().then(setMe, () => undefined); }, []);

  // Follow hash changes (back/forward, bookmarks, links) after load too.
  useEffect(() => {
    const onHash = () => {
      const t = parseHash(location.hash);
      if (t) setTab((cur) => (sameTab(cur, t) ? cur : t));
      const v = location.hash.split("/")[1];
      if (t?.kind === "network") setNetView((NET_VIEWS as string[]).includes(v ?? "") ? (v as NetView) : "grid");
    };
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  // A browser that isn't on a known machine (an iPad, say) has no "this machine": show the network instead.
  useEffect(() => { if (machines && me && tab.kind === "this" && !local) setTab({ kind: "network" }); }, [machines, me, tab, local]);
  // The viewer's own machine is the "this" tab, never a pinned or opened one.
  useEffect(() => { if (local && tab.kind === "machine" && tab.id === local.id) setTab({ kind: "this" }); }, [local, tab]);

  useEffect(() => {
    const hash = tabHash(tab, tab.kind === "network" && netView !== "grid" ? netView : undefined);
    store("harbor.tab", tabHash(tab)); store("harbor.net", netView);
    if (location.hash !== hash) history.replaceState(null, "", hash);
  }, [tab, netView]);
  useEffect(() => store("harbor.content", content), [content]);
  useEffect(() => store("harbor.pins", JSON.stringify(pins)), [pins]);

  // Opening a machine asks for fresh state; the new push then updates lastPushAt below.
  useEffect(() => { if (current?.status === "approved") requestRefresh(current.id).catch(() => undefined); }, [current?.id]);

  // Refetch the snapshot when the machine changes or pushes again.
  useEffect(() => {
    if (!current) { setSnap(null); return; }
    let live = true;
    fetchSnapshot(current.id).then((s) => { if (live) setSnap(s); }, (e: Error) => live && setErr(e.message));
    return () => { live = false; };
  }, [current?.id, current?.lastPushAt]);

  // Jobs for the shown machine: poll fast while one is open, and refresh when it ends.
  const openJobs = jobs.filter((j) => !j.finishedAt);
  const loadJobs = useCallback(async (id: number) => {
    try { setJobs(await fetchJobs(id)); } catch (e) { setErr((e as Error).message); }
  }, []);
  useEffect(() => { setJobs([]); if (current) void loadJobs(current.id); }, [current?.id, loadJobs]);
  useEffect(() => {
    if (!current || openJobs.length === 0) return;
    const t = setInterval(() => void loadJobs(current.id), 2000);
    return () => clearInterval(t);
  }, [current?.id, openJobs.length, loadJobs]);
  useEffect(() => { if (openJobs.length === 0) void load(); }, [openJobs.length, load]);

  // Viewers get a read-only UI; the hub enforces it either way.
  const admin = me?.admin ?? false;
  const approved = current?.status === "approved" && admin;
  const hidden = current?.hidden ?? [];
  const running = useCallback((kind: string, path?: string) =>
    openJobs.some((j) => j.kind === kind && (j.path === undefined || j.path === path)), [openJobs]);
  const fetching = useCallback((path?: string) => running("fetch", path), [running]);
  const doJob = useCallback(async (request: (id: number, path?: string) => Promise<unknown>, path?: string) => {
    if (!current) return;
    try { await request(current.id, path); await loadJobs(current.id); } catch (e) { setErr((e as Error).message); }
  }, [current, loadJobs]);
  const doFetch = useCallback((path?: string) => doJob(requestFetch, path), [doJob]);
  const doPull = useCallback((path?: string) => doJob(requestPull, path), [doJob]);
  const doRole = useCallback((name: string) => doJob((id) => requestRole(id, name)), [doJob]);
  // Pull one repo on several machines at once (shared repos): only copies that can fast-forward.
  const pullCopies = useCallback(async (copies: { machineId: number; path: string }[]) => {
    try { await Promise.all(copies.map((c) => requestPull(c.machineId, c.path))); setTimeout(() => void load(), 4000); }
    catch (e) { setErr((e as Error).message); }
  }, [load]);
  const doHide = useCallback(async (path: string, hide: boolean) => {
    if (!current) return;
    try { await setHidden(current.id, path, hide); await load(); } catch (e) { setErr((e as Error).message); }
  }, [current, load]);
  const actions: RepoActions = {
    fetch: approved ? (p) => void doFetch(p) : undefined,
    hide: approved ? (p, h) => void doHide(p, h) : undefined,
    fetching: (p) => fetching(p),
    pull: approved ? (p) => void doPull(p) : undefined,
    pulling: (p) => running("pull", p),
  };
  // Job failures surface once, as the banner.
  const lastFailed = jobs.find((j) => j.finishedAt && j.ok === false);
  const failedSeen = useRef<number | null>(null);
  useEffect(() => {
    if (jobs.length === 0) return; // still loading; the first real list is the baseline
    const newest = Math.max(...jobs.map((j) => j.id));
    if (failedSeen.current !== null && lastFailed && lastFailed.id > failedSeen.current) setErr(`${lastFailed.kind} failed: ${lastFailed.output ?? ""}`);
    failedSeen.current = Math.max(failedSeen.current ?? 0, newest);
  }, [jobs]);

  const go = useCallback((t: Tab) => { setTab(t); setSel(0); setOpen(null); }, []);
  const openMachine = useCallback((m: MachineSummary) => go(m.id === local?.id ? { kind: "this" } : { kind: "machine", id: m.id }), [go, local]);
  const togglePin = useCallback((id: number) => setPins((ps) => (ps.includes(id) ? ps.filter((p) => p !== id) : [...ps, id])), []);
  const allTabs = useMemo<Tab[]>(() => [
    ...(local ? [{ kind: "this" } as Tab] : []), { kind: "network" }, { kind: "pending" },
    ...pins.filter((id) => id !== local?.id && machines?.some((m) => m.id === id)).map((id): Tab => ({ kind: "machine", id })),
  ], [local, pins, machines]);
  const cycle = useCallback((d: number) => {
    const i = allTabs.findIndex((t) => sameTab(t, tab));
    const next = allTabs[(i + d + allTabs.length) % allTabs.length];
    if (next) go(next);
  }, [allTabs, tab, go]);

  const decide = useCallback(async (id: number, verb: "approve" | "reject") => {
    try { await setStatus(id, verb); await load(); } catch (e) { setErr((e as Error).message); }
  }, [load]);

  const [syncing, setSyncing] = useState(false);
  const doSync = useCallback(async () => {
    setSyncing(true);
    try {
      await syncAll();
      // agents rescan, then push; most answer within a few seconds
      for (const ms of [2500, 6000]) { await new Promise((r) => setTimeout(r, ms)); await load(); }
    } catch (e) { setErr((e as Error).message); } finally { setSyncing(false); }
  }, [load]);

  const canScan = !!current?.local && admin;
  const doScan = useCallback(async () => {
    if (!canScan) return;
    setScanning(true);
    try { await rescan(); await load(); } catch (e) { setErr((e as Error).message); } finally { setScanning(false); }
  }, [load, canScan]);

  const visible = useMemo(() => (snap?.repos ?? []).filter((r) => !hidden.includes(r.path)), [snap, hidden]);
  const repos = useMemo(() => {
    const q = query.toLowerCase();
    const sorted = (snap?.repos ?? [])
      .filter((r) => (filter === "hidden") === hidden.includes(r.path))
      .filter(FILTERS[filter])
      .filter((r) => !q || r.path.toLowerCase().includes(q) || (r.branch?.head ?? "").toLowerCase().includes(q))
      .sort(SORTS[sort]);
    return withWorktrees(sorted);
  }, [snap, query, filter, sort, hidden]);
  const listed = useMemo(() => new Set(repos.map((r) => r.path)), [repos]);
  useEffect(() => { if (filter === "hidden" && hidden.length === 0) setFilter("all"); }, [filter, hidden.length]);
  useEffect(() => setSel((s) => Math.min(s, Math.max(0, repos.length - 1))), [repos.length]);

  const showRepos = !!current && content === "repos";
  // Terminal-style keys: j/k move, enter/o expand, / search, h/l tabs, f/F fetch, r rescan, S sync all, esc clears.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement) {
        if (e.key === "Escape") e.target.blur();
        return;
      }
      if (e.metaKey || e.ctrlKey || e.altKey) return;
      const cur = showRepos ? repos[sel] : undefined;
      if (showRepos && (e.key === "j" || e.key === "ArrowDown")) setSel((s) => Math.min(s + 1, repos.length - 1));
      else if (showRepos && (e.key === "k" || e.key === "ArrowUp")) setSel((s) => Math.max(s - 1, 0));
      else if ((e.key === "Enter" || e.key === "o") && cur) setOpen((o) => (o === cur.path ? null : cur.path));
      else if (e.key === "/" && showRepos) search.current?.focus();
      else if (e.key === "r" && canScan) void doScan();
      else if (e.key === "S" && !syncing) void doSync();
      else if (e.key === "f" && cur && approved && isFetchable(cur)) void doFetch(cur.path);
      else if (e.key === "F" && approved) void doFetch();
      else if (e.key === "p" && cur && approved && !pullBlocker(cur)) void doPull(cur.path);
      else if (e.key === "P" && approved) void doPull();
      else if (e.key === "h" || e.key === "ArrowLeft") cycle(-1);
      else if (e.key === "l" || e.key === "ArrowRight") cycle(1);
      else if (e.key === "Escape") { setOpen(null); setQuery(""); }
      else return;
      e.preventDefault();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [repos, sel, showRepos, doScan, canScan, cycle, approved, doFetch, doPull, doSync, syncing]);
  useEffect(() => { document.querySelector(".repo.sel")?.scrollIntoView({ block: "nearest" }); }, [sel]);

  const counts: Record<Filter, number> = useMemo(() => ({
    all: visible.length, dirty: visible.filter(FILTERS.dirty).length, behind: visible.filter(FILTERS.behind).length,
    unpushed: visible.filter(FILTERS.unpushed).length, local: visible.filter(FILTERS.local).length, hidden: hidden.length,
  }), [visible, hidden.length]);
  const contentCount: Record<Content, string> = {
    repos: String(visible.length), packages: "", services: snap ? String(snap.services.length) : "—", storage: snap?.resources ? String(snap.resources.disks.length) : "—",
  };
  const sortHead = (s: Sort, label: string, cls: string) => (
    <button className={`${cls} ${sort === s ? "on" : ""}`} onClick={() => setSort(s)} aria-pressed={sort === s}>
      {label}{sort === s ? " ▾" : ""}
    </button>
  );

  return (
    <main className="app">
      <Header machines={machines ?? []} me={me} onPending={() => go({ kind: "pending" })} onDevices={() => setDevices(true)} onSync={() => void doSync()} syncing={syncing} />
      {devices && <DevicesDialog me={me} machines={machines ?? []} onClose={() => setDevices(false)} />}
      {err && <div className="banner" role="alert">⚠ {err}<button className="link" onClick={() => setErr(null)}>dismiss</button></div>}
      {!machines ? <p className="empty">loading…</p> : (
        <>
          <Tabs tab={tab} machines={machines} self={local} pins={pins} now={now} onTab={go} onPin={togglePin} onReorder={setPins} />
          <section className={`card view-${tab.kind}`}>
            {tab.kind === "network" && (
              <NetworkView machines={machines} shared={shared} admin={admin} view={netView} pins={pins} selfId={local?.id} now={now}
                onView={setNetView} onOpen={openMachine} onPin={togglePin} onPullCopies={admin ? (cs) => void pullCopies(cs) : undefined} />
            )}
            {tab.kind === "pending" && <PendingView machines={machines} now={now} onOpen={openMachine} onDecide={admin ? (id, v) => void decide(id, v) : undefined} onPaired={() => void load()} agentUrl={me?.agentUrl} />}
            {current && !snap && <p className="empty">loading {current.name}…</p>}
            {current && snap && (
              <>
                <MachinePanel summary={current} snap={snap} now={now}
                  pinned={pins.includes(current.id)} onPin={current.id === local?.id || current.status !== "approved" ? undefined : () => togglePin(current.id)}
                  onFetchAll={approved ? () => void doFetch() : undefined} fetchingAll={openJobs.some((j) => j.kind === "fetch" && !j.path)}
                  onPullAll={approved ? () => void doPull() : undefined} pullingAll={openJobs.some((j) => j.kind === "pull" && !j.path)}
                  onScan={canScan ? () => void doScan() : undefined} scanning={scanning}
                  onRole={approved ? (n) => void doRole(n) : undefined} switchingRole={openJobs.find((j) => j.kind === "role")?.arg} />
                <nav className="ctabs" role="tablist" aria-label="machine content">
                  {CONTENT.map((c) => (
                    <button key={c} role="tab" aria-selected={c === content} className={c === content ? "on" : ""} onClick={() => setContent(c)}>
                      {c[0]!.toUpperCase() + c.slice(1)}<b>{contentCount[c]}</b>
                    </button>
                  ))}
                </nav>
                {content === "repos" && (
                  <>
                    <div className="tablebar">
                      <div className="filters">
                        {(Object.keys(FILTERS) as Filter[]).filter((f) => f === "all" || counts[f] > 0 || f === filter).map((f) => (
                          <button key={f} className={`chip ${f === filter ? "on" : ""} ${f === "dirty" || f === "behind" || f === "unpushed" ? "attn" : ""}`}
                            onClick={() => setFilter(f === filter ? "all" : f)} aria-pressed={f === filter}>
                            {counts[f]} {f === "local" ? "local only" : f}
                          </button>
                        ))}
                      </div>
                      <input ref={search} value={query} onChange={(e) => setQuery(e.target.value)} placeholder="/ filter path or branch" aria-label="filter repos" />
                    </div>
                    <ul className="repo-list">
                      <li className="thead repo-cols">
                        {sortHead("name", "repo", "")}<span className="owner">owner / org</span>
                        {sortHead("commit", "last commit", "commit")}{sortHead("modified", "modified", "when")}
                      </li>
                      {repos.map((r, i) => (
                        <RepoRow key={r.path} repo={r} now={now} selected={i === sel} open={open === r.path}
                          hidden={hidden.includes(r.path)} nested={!!r.worktreeOf && listed.has(r.worktreeOf)} actions={actions}
                          onToggle={() => { setSel(i); setOpen((o) => (o === r.path ? null : r.path)); }} />
                      ))}
                      {repos.length === 0 && <li className="empty">No repos match.</li>}
                    </ul>
                  </>
                )}
                {content === "services" && <ServicesTable services={snap.services} />}
                {content === "packages" && <PackagesView machineId={current.id} admin={admin} now={now} />}
                {content === "storage" && <DisksTable disks={snap.resources?.disks} />}
              </>
            )}
          </section>
          <footer className="keys">j/k move · enter expand · h/l tabs · / filter{approved ? " · f fetch · F fetch all · p pull · P pull all" : ""}{canScan ? " · r rescan" : ""} · esc clear</footer>
        </>
      )}
    </main>
  );
}
