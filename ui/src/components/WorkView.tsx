import { useEffect, useMemo, useState } from "react";
import type { MachineSummary, Repo, Snapshot, UnpushedCommit } from "../types";
import { fetchJobs, fetchSnapshot, requestDiff } from "../api";
import { ago, plural, shortPath } from "../format";
import { Mark } from "./Mark";

/** Host + path of a remote, so clones of one repo group together (mirrors GitParse.remoteKey). */
export function remoteKey(url: string): string | undefined {
  const u = url.trim();
  let host: string, path: string;
  if (u.includes("://")) {
    try { const p = new URL(u); if (!p.host || p.protocol === "file:") return undefined; host = p.hostname; path = decodeURIComponent(p.pathname); }
    catch { return undefined; }
  } else {
    const i = u.indexOf(":");
    if (i <= 1 || u.slice(0, i).includes("/")) return undefined;
    host = u.slice(u.indexOf("@") + 1, i); path = u.slice(i + 1);
  }
  path = path.replace(/^\/+|\/+$/g, "").replace(/\.git$/, "");
  return `${host}/${path}`.toLowerCase();
}

const primary = (r: Repo) => r.remotes.find((x) => x.name === "origin") ?? r.remotes[0];
const dirtyCount = (r: Repo) => r.changes ? r.changes.staged + r.changes.unstaged + r.changes.untracked + r.changes.conflicted : 0;

type Copy = { machine: MachineSummary; repo: Repo; unpushed: UnpushedCommit[]; count: number; stashes: number; dirty: number };
type Group = { key: string; label: string; local: boolean; copies: Copy[]; latest: string };

/** Each machine's repos with work no remote has: unpushed commits, stashes, uncommitted changes. */
function groups(snaps: { machine: MachineSummary; snap: Snapshot }[]): Group[] {
  const by = new Map<string, Copy[]>();
  for (const { machine, snap } of snaps) {
    for (const r of snap.repos) {
      if (machine.hidden.includes(r.path)) continue;
      // linked worktrees share their main clone's refs: count commits and stashes once
      const shared = !!r.worktreeOf;
      const f = r.inFlight;
      const c: Copy = { machine, repo: r, unpushed: shared ? [] : f?.unpushed ?? [], count: shared ? 0 : f?.unpushedCount ?? 0, stashes: shared ? 0 : f?.stashes ?? 0, dirty: dirtyCount(r) };
      if (c.count === 0 && c.stashes === 0 && c.dirty === 0) continue;
      const remote = primary(r);
      const key = (remote && remoteKey(remote.url)) ?? `${machine.name}:${r.path}`;
      by.set(key, [...(by.get(key) ?? []), c]);
    }
  }
  const latest = (c: Copy) => [c.unpushed[0]?.date, c.repo.lastModified].filter(Boolean).sort().at(-1) ?? "";
  return [...by.entries()]
    .map(([key, copies]) => {
      const first = copies[0]?.repo;
      const local = !first?.remotes.some((r) => remoteKey(r.url));
      return { key, label: local ? first?.name ?? key : key, local, copies, latest: copies.map(latest).sort().at(-1) ?? "" };
    })
    .sort((a, b) => b.latest.localeCompare(a.latest));
}

type DiffTarget = { machine: MachineSummary; path: string; arg: string; title: string };

/** Work in flight across machines; admins can pull a read-only diff from the machine that has it. */
export function WorkView({ machines, admin, now }: { machines: MachineSummary[]; admin: boolean; now: number }) {
  const approved = useMemo(() => machines.filter((m) => m.status === "approved"), [machines]);
  const stamp = approved.map((m) => `${m.id}@${m.lastPushAt}`).join(",");
  const [snaps, setSnaps] = useState<{ machine: MachineSummary; snap: Snapshot }[]>();
  const [err, setErr] = useState<string>();
  const [open, setOpen] = useState<string>();
  const [diff, setDiff] = useState<DiffTarget>();

  useEffect(() => {
    let live = true;
    Promise.all(approved.map(async (machine) => ({ machine, snap: await fetchSnapshot(machine.id) })))
      .then((s) => { if (live) { setSnaps(s); setErr(undefined); } }, (e: Error) => live && setErr(e.message));
    return () => { live = false; };
  }, [stamp]);

  const gs = useMemo(() => (snaps ? groups(snaps) : []), [snaps]);
  // the same unpushed commit on two machines (same branch pushed nowhere, fetched between them)
  const holders = useMemo(() => {
    const m = new Map<string, string[]>();
    for (const g of gs) for (const c of g.copies) for (const u of c.unpushed) m.set(u.sha, [...(m.get(u.sha) ?? []), c.machine.name]);
    return m;
  }, [gs]);

  if (err) return <p className="empty">{err}</p>;
  if (!snaps) return <p className="empty">loading…</p>;
  return (
    <div className="shared work">
      <p className="dim2 work-note">Commits on local branches that no remote has, stashes and uncommitted changes, per machine. As fresh as each repo's last fetch.</p>
      {gs.length === 0 && <p className="empty">Nothing in flight: every commit is on a remote and every tree is clean.</p>}
      {gs.map((g) => (
        <div key={g.key} className="group">
          <div className="group-head">
            <b>{g.label}</b>
            {g.local && <Mark tone="dim">no remote</Mark>}
            {g.copies.length > 1 && <Mark tone="warn">on {g.copies.length} machines</Mark>}
          </div>
          <ul className="copies">
            {g.copies.map((c) => {
              const id = `${c.machine.id}:${c.repo.path}`;
              const expanded = open === id;
              return (
                <li key={id} className="work-copy">
                  <div className="copy">
                    <b className="copy-machine">{c.machine.name}</b>
                    <span className="copy-path dim" title={c.repo.path}><span dir="ltr">{shortPath(c.repo.path)}</span></span>
                    <span className="copy-state">
                      <span className="dim">{c.repo.branch?.head ?? "detached"}</span>
                      {c.count > 0 && (
                        <button className="link" onClick={() => setOpen(expanded ? undefined : id)} aria-expanded={expanded}>
                          ↑{c.count} unpushed
                        </button>
                      )}
                      {c.stashes > 0 && <Mark tone="info">{plural(c.stashes, "stash")}</Mark>}
                      {c.dirty > 0 && (admin
                        ? <button className="link warnlink" onClick={() => setDiff({ machine: c.machine, path: c.repo.path, arg: "worktree", title: `${c.machine.name} · ${c.repo.name} · uncommitted` })}
                            title="show the uncommitted changes (read-only, from that machine)">{plural(c.dirty, "change")}</button>
                        : <Mark tone="warn">{plural(c.dirty, "change")}</Mark>)}
                    </span>
                    <span className="when">{ago(c.repo.lastModified, now)}</span>
                  </div>
                  {expanded && (
                    <ul className="commits">
                      {c.unpushed.map((u) => {
                        const also = (holders.get(u.sha) ?? []).filter((n) => n !== c.machine.name);
                        return (
                          <li key={u.sha}>
                            {admin
                              ? <button className="link sha" onClick={() => setDiff({ machine: c.machine, path: c.repo.path, arg: u.sha, title: `${c.machine.name} · ${c.repo.name} · ${u.sha.slice(0, 7)}` })} title="show this commit (read-only, from that machine)">{u.sha.slice(0, 7)}</button>
                              : <span className="sha">{u.sha.slice(0, 7)}</span>}
                            <span className="dim">{u.branch}</span>
                            <span className="subject">{u.subject}</span>
                            {also.length > 0 && <Mark tone="dim">also on {also.join(", ")}</Mark>}
                            <span className="when">{ago(u.date, now)}</span>
                          </li>
                        );
                      })}
                      {c.count > c.unpushed.length && <li className="dim2">… {c.count - c.unpushed.length} older</li>}
                    </ul>
                  )}
                </li>
              );
            })}
          </ul>
        </div>
      ))}
      {diff && <DiffDialog target={diff} onClose={() => setDiff(undefined)} />}
    </div>
  );
}

/** Queues the diff job, then polls that machine's jobs until it answers. */
function DiffDialog({ target, onClose }: { target: DiffTarget; onClose: () => void }) {
  const [state, setState] = useState<{ text?: string; error?: string }>({});
  useEffect(() => {
    let live = true;
    const started = Date.now();
    (async () => {
      try {
        const id = await requestDiff(target.machine.id, target.path, target.arg);
        while (live) {
          const job = (await fetchJobs(target.machine.id)).find((j) => j.id === id);
          if (job?.finishedAt) {
            if (live) setState(job.ok ? { text: job.output ?? "" } : { error: job.output ?? "failed" });
            return;
          }
          if (Date.now() - started > 90_000) {
            if (live) setState({ error: `${target.machine.name} hasn't answered yet; it runs the diff on its next contact with the hub.` });
            return;
          }
          await new Promise((r) => setTimeout(r, 1200));
        }
      } catch (e) { if (live) setState({ error: (e as Error).message }); }
    })();
    return () => { live = false; };
  }, [target]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="modal" role="dialog" aria-modal="true" aria-label={target.title} onClick={onClose}>
      <div className="modal-box diff-box" onClick={(e) => e.stopPropagation()}>
        <div className="diff-head">
          <b>{target.title}</b>
          <button className="link" onClick={onClose}>close</button>
        </div>
        {state.error && <p className="warn-text">{state.error}</p>}
        {!state.error && state.text === undefined && <p className="dim">asking {target.machine.name}…</p>}
        {state.text !== undefined && <DiffText text={state.text} />}
      </div>
    </div>
  );
}

const lineClass = (l: string) =>
  l.startsWith("+++") || l.startsWith("---") || l.startsWith("diff --git") ? "d-file"
  : l.startsWith("@@") ? "d-hunk"
  : l.startsWith("+") ? "d-add"
  : l.startsWith("-") ? "d-del"
  : "";

function DiffText({ text }: { text: string }) {
  return <pre className="diff">{text.split("\n").map((l, i) => <div key={i} className={lineClass(l)}>{l || " "}</div>)}</pre>;
}
