import { useEffect, useMemo, useState } from "react";
import type { Manifest, ManifestDiff, ManifestInfo } from "../types";
import { fetchDiff, fetchManifest, fetchManifests, manifestMarkdownUrl } from "../api";
import { ago } from "../format";
import { ChangesList } from "./ChangesList";

const LIMIT = 400;

/** One machine's installed software: managers, packages, pending upgrades, and how it changed. */
export function PackagesView({ machineId, admin, now }: { machineId: number; admin: boolean; now: number }) {
  const [history, setHistory] = useState<ManifestInfo[] | null>(null);
  const [manifest, setManifest] = useState<Manifest | null>(null);
  const [manager, setManager] = useState<string>("all");
  const [query, setQuery] = useState("");
  const [diff, setDiff] = useState<ManifestDiff | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    setHistory(null); setManifest(null); setDiff(null); setErr(null);
    fetchManifests(machineId).then(setHistory, (e: Error) => setErr(e.message));
  }, [machineId]);
  const latest = history?.[0];
  useEffect(() => {
    if (!latest || !admin) return;
    fetchManifest(latest.id).then(setManifest, (e: Error) => setErr(e.message));
  }, [latest?.id, admin]);

  const rows = useMemo(() => {
    if (!manifest) return [];
    const q = query.toLowerCase();
    return manifest.sets
      .filter((s) => manager === "all" || manager === "upgrades" || s.manager === manager)
      .flatMap((s) => manager === "upgrades"
        ? s.upgrades.map((u) => ({ manager: s.manager, name: u.name, version: `${u.from} → ${u.to}` }))
        : s.packages.map((p) => ({ manager: s.manager, name: p.name, version: p.version })))
      .filter((r) => !q || r.name.toLowerCase().includes(q));
  }, [manifest, manager, query]);

  if (err) return <p className="empty">{err}</p>;
  if (!history) return <p className="empty">loading…</p>;
  if (history.length === 0)
    return <p className="empty">No manifest yet. Agents send one after their first scan, then whenever installed software changes.</p>;
  const upgrades = manifest?.sets.reduce((n, s) => n + s.upgrades.length, 0) ?? 0;

  return (
    <>
      <div className="tablebar">
        <div className="filters">
          {manifest && <>
            <button className={`chip ${manager === "all" ? "on" : ""}`} onClick={() => setManager("all")}>{latest!.packageCount} all</button>
            {manifest.sets.map((s) => (
              <button key={s.manager} className={`chip ${manager === s.manager ? "on" : ""}`} onClick={() => setManager(s.manager)}
                title={s.error}>{s.error ? "!" : s.packages.length} {s.manager}</button>
            ))}
            {upgrades > 0 && <button className={`chip attn ${manager === "upgrades" ? "on" : ""}`} onClick={() => setManager("upgrades")}>{upgrades} upgradable</button>}
          </>}
        </div>
        {manifest && <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="filter packages" aria-label="filter packages" />}
      </div>
      <div className="manifest-meta">
        <span>{latest!.os} · taken {ago(latest!.takenAt, now)} ago · <span className="mono dim2">{latest!.hash.slice(0, 12)}</span></span>
        {admin && <a className="link" href={manifestMarkdownUrl(latest!.id)} target="_blank" rel="noreferrer">export .md</a>}
      </div>
      {!admin && <p className="empty">Package lists are visible to admins only.</p>}
      {admin && manifest && (
        <ul className="table packages">
          <li className="thead"><span>package</span><span className="opt">manager</span><span className="r">version</span></li>
          {rows.slice(0, LIMIT).map((r) => (
            <li key={`${r.manager}:${r.name}`} className="trow">
              <span className="name mono">{r.name}</span><span className="opt mono dim">{r.manager}</span><span className="r mono dim">{r.version}</span>
            </li>
          ))}
          {rows.length > LIMIT && <li className="empty">{rows.length - LIMIT} more: filter to narrow down.</li>}
          {rows.length === 0 && <li className="empty">Nothing matches.</li>}
        </ul>
      )}
      <div className="panelbar"><span className="label">history · a new entry each time installed software changed</span></div>
      <ul className="table history">
        {history.map((h, i) => {
          const prev = history[i + 1];
          return (
            <li key={h.id} className="trow">
              <span className="mono">{new Date(h.takenAt).toLocaleString()}</span>
              <span className="opt dim">{h.os} · {h.packageCount} packages</span>
              <span className="r">
                {admin && prev
                  ? <button className="link" onClick={() => fetchDiff(prev.id, h.id).then(setDiff, (e: Error) => setErr(e.message))}>changes</button>
                  : <span className="dim2">{prev ? "" : "first"}</span>}
              </span>
            </li>
          );
        })}
      </ul>
      {diff && (
        <div className="diffbox">
          <div className="panelbar">
            <span className="label">{new Date(diff.a.takenAt).toLocaleString()} → {new Date(diff.b.takenAt).toLocaleString()}</span>
            <button className="link" onClick={() => setDiff(null)}>close</button>
          </div>
          <ChangesList changes={diff.changes} />
        </div>
      )}
    </>
  );
}
