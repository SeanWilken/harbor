import type { Copy, SharedRepo } from "../types";
import { ago, shortPath } from "../format";
import { Mark } from "./Mark";

/** Newest HEAD by last-commit date; only meaningful when HEADs differ. */
const newest = (copies: Copy[]) =>
  copies.reduce<Copy | undefined>((best, c) => (!best || (c.lastCommitAt ?? "") > (best.lastCommitAt ?? "") ? c : best), undefined);

function CopyRow({ c, mark, now }: { c: Copy; mark: boolean; now: number }) {
  return (
    <li className="copy">
      <b className="copy-machine">{c.machine}</b>
      <span className="copy-path dim" title={c.path}><span dir="ltr">{shortPath(c.path)}</span></span>
      <span className="copy-state">
        <span className="dim">{c.branch ?? "detached"}</span>
        <span title={c.headOid}>{(c.headOid ?? "no commits").slice(0, 7)}</span>
        {mark && <Mark tone="ok" title="newest last commit of the differing HEADs">newest</Mark>}
        {c.ahead > 0 && <Mark tone="info">↑{c.ahead}</Mark>}
        {c.behind > 0 && <Mark tone="info">↓{c.behind}</Mark>}
        {c.dirty && <Mark tone="warn">dirty</Mark>}
      </span>
      <span className="when">{ago(c.lastCommitAt, now)}</span>
    </li>
  );
}

/** Repos cloned on two or more machines, matched by normalized remote URL. */
export function SharedView({ groups, now }: { groups: SharedRepo[]; now: number }) {
  return (
    <div className="shared">
      {groups.length === 0 && <p className="empty">No remote is cloned on more than one approved machine yet.</p>}
      {groups.map((g) => {
        const top = g.sameHead ? undefined : newest(g.copies);
        return (
          <div key={g.remoteKey} className="group">
            <div className="group-head">
              <b>{g.remoteKey}</b>
              {g.sameHead ? <Mark tone="dim">same HEAD</Mark> : <Mark tone="warn">HEADs differ</Mark>}
            </div>
            <ul className="copies">
              {g.copies.map((c) => <CopyRow key={`${c.machineId}:${c.path}`} c={c} mark={c === top} now={now} />)}
            </ul>
          </div>
        );
      })}
    </div>
  );
}
