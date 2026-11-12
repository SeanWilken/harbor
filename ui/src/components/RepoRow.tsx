import type { Repo } from "../types";
import { ago, owner, remoteHost, shortPath } from "../format";
import { isFetchable, repoTags } from "../status";
import { Mark } from "./Mark";
import { Pill } from "./Pill";

/** What the row may offer; absent = not available (e.g. machine not approved). */
export type RepoActions = {
  fetch?: (path: string) => void;
  hide?: (path: string, hidden: boolean) => void;
  fetching: (path: string) => boolean;
};

type Props = {
  repo: Repo; now: number; selected: boolean; open: boolean; hidden: boolean;
  nested: boolean; // worktree drawn under its main repo
  actions: RepoActions; onToggle: () => void;
};

export function RepoRow({ repo: r, now, selected, open, hidden, nested, actions, onToggle }: Props) {
  const tags = repoTags(r);
  return (
    <li className={`repo ${selected ? "sel" : ""} ${open ? "open" : ""} ${nested ? "nested" : ""} ${hidden ? "hidden" : ""}`}>
      <button className="repo-line" onClick={onToggle} aria-expanded={open}>
        <span className="who">
          <span className="name"><span className="chev">{open ? "▾" : "›"}</span>{nested && <span className="dim2">└ </span>}{r.name}
            {tags.map((t) => <Mark key={t.label} tone={t.tone} title={t.title}>{t.label}</Mark>)}
            {actions.fetching(r.path) && <Mark tone="dim">fetching…</Mark>}
          </span>
          <small title={r.branch?.upstream ? `tracks ${r.branch.upstream}` : undefined}>
            {r.branch?.head ?? r.branch?.oid?.slice(0, 7) ?? "?"}{(r.branches?.length ?? 0) > 1 ? ` +${r.branches!.length - 1}` : ""}
            {" · "}{shortPath(r.path)}{r.worktreeOf && !nested ? ` · worktree of ${shortPath(r.worktreeOf)}` : ""}
          </small>
        </span>
        <span className="owner">{owner(r.remotes) ?? <span className="dim2">no remote</span>}</span>
        <span className="commit" title={r.lastCommit ? `${r.lastCommit.subject} — ${r.lastCommit.author}` : "no commits"}>
          {r.lastCommit ? `${ago(r.lastCommit.date, now)} ago` : "—"}
        </span>
        <span className="when" title={`last modified ${r.lastModified ?? "—"}`}>{ago(r.lastModified, now)}</span>
      </button>
      {open && <RepoDetail repo={r} now={now} hidden={hidden} actions={actions} />}
    </li>
  );
}

function RepoDetail({ repo: r, now, hidden, actions }: { repo: Repo; now: number; hidden: boolean; actions: RepoActions }) {
  return (
    <div className="detail">
      <dl>
        <dt>path</dt><dd>{r.path}</dd>
        {r.worktreeOf && <><dt>worktree of</dt><dd>{r.worktreeOf} <span className="dim">(shares its refs; fetch that one)</span></dd></>}
        <dt>head</dt><dd>{r.branch?.head ?? "(detached)"} <span className="dim">{r.branch?.oid?.slice(0, 12) ?? "no commits"}</span></dd>
        <dt>upstream</dt><dd>{r.branch?.upstream ?? <span className="dim">none</span>}</dd>
        {r.branches && r.branches.length > 0 && <>
          <dt>branches</dt>
          <dd><ul className="branches">
            {r.branches.slice().sort((a, b) => (b.date ?? "").localeCompare(a.date ?? "")).map((b) => (
              <li key={b.name} className={b.name === r.branch?.head ? "head" : ""}>
                <span className="bname">{b.name}</span>
                <span className="dim2">{b.gone ? <Mark tone="warn" title="its upstream was deleted">upstream gone</Mark> : b.upstream ?? "no upstream"}</span>
                <span>{b.ahead > 0 && <Mark tone="info">↑{b.ahead}</Mark>} {b.behind > 0 && <Mark tone="info">↓{b.behind}</Mark>}</span>
                <span className="dim r">{b.date ? `${ago(b.date, now)} ago` : ""}</span>
              </li>
            ))}
          </ul></dd>
        </>}
        <dt>fetched</dt>
        <dd>{r.lastFetch ? `${ago(r.lastFetch, now)} ago` : <span className="dim">never</span>}
          {r.fetchError && <div className="err">{r.fetchError}</div>}</dd>
        <dt>remotes</dt>
        <dd>{r.remotes.length ? r.remotes.map((x) => (
          <div key={x.name}>{x.name} <span className="dim">{remoteHost(x.url)}</span> {x.url}</div>
        )) : <span className="dim">none</span>}</dd>
        <dt>identity</dt><dd>{r.identity.name ?? "?"} &lt;{r.identity.email ?? "?"}&gt; <span className="dim">from {r.identity.scope ?? "nowhere"} config</span></dd>
        <dt>last commit</dt>
        <dd>{r.lastCommit ? <>{r.lastCommit.subject} <span className="dim">— {r.lastCommit.author}, {new Date(r.lastCommit.date).toLocaleString()}</span></> : <span className="dim">none</span>}</dd>
        <dt>modified</dt><dd>{r.lastModified ? new Date(r.lastModified).toLocaleString() : "—"}</dd>
        {r.error && <><dt>error</dt><dd className="err">{r.error}</dd></>}
      </dl>
      {r.changes && r.changes.paths.length > 0 && <pre>{r.changes.paths.join("\n")}</pre>}
      <div className="actions">
        {actions.fetch && isFetchable(r) && (
          <Pill onClick={() => actions.fetch!(r.path)} on={actions.fetching(r.path)}>
            {actions.fetching(r.path) ? "fetching…" : "f fetch"}
          </Pill>
        )}
        {actions.hide && <Pill onClick={() => actions.hide!(r.path, !hidden)}>{hidden ? "unhide" : "hide"}</Pill>}
      </div>
    </div>
  );
}
