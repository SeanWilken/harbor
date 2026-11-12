import type { PackageChange } from "../types";

/** Package differences: installed, removed, changed, with before → after. */
export function ChangesList({ changes, left = "before", right = "after" }: { changes: PackageChange[]; left?: string; right?: string }) {
  if (changes.length === 0) return <p className="empty">No package differences.</p>;
  const kind = (c: PackageChange) => (c.before === undefined ? "added" : c.after === undefined ? "removed" : "changed");
  const counts = { added: 0, removed: 0, changed: 0 };
  changes.forEach((c) => counts[kind(c)]++);
  return (
    <>
      <p className="changes-sum mono dim">
        {counts.added} only in {right} · {counts.removed} only in {left} · {counts.changed} different versions
      </p>
      <ul className="table changes">
        <li className="thead"><span>package</span><span className="opt">manager</span><span>{left}</span><span>{right}</span></li>
        {changes.slice(0, 500).map((c) => (
          <li key={`${c.manager}:${c.name}`} className={`trow ${kind(c)}`}>
            <span className="name mono">{c.name}</span>
            <span className="opt mono dim">{c.manager}</span>
            <span className="mono dim">{c.before ?? "—"}</span>
            <span className="mono">{c.after ?? "—"}</span>
          </li>
        ))}
        {changes.length > 500 && <li className="empty">{changes.length - 500} more (see the export)</li>}
      </ul>
    </>
  );
}
