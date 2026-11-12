import { useEffect, useState } from "react";
import type { RecapInfo, RecapReport, RepoNote } from "../types";
import { fetchRecap, fetchRecaps, fetchStoredRecap, recapMarkdownUrl } from "../api";
import { ago, shortPath } from "../format";

const day = (iso: string) => new Date(iso).toLocaleDateString();

function Notes({ title, rule, notes, now }: { title: string; rule: string; notes: RepoNote[]; now: number }) {
  return (
    <section className="recap-sec">
      <h3>{title} <span className="dim2">{notes.length}</span></h3>
      <p className="rule">{rule}</p>
      {notes.length === 0 ? <p className="dim2 none">Nothing.</p> : (
        <ul className="table notes">
          {notes.map((n) => (
            <li key={`${n.machine}:${n.path}`} className="trow">
              <span className="name">{n.name}<small className="mono">{n.machine} · {shortPath(n.path)}</small></span>
              <span className="opt mono dim">{n.detail}</span>
              <span className="r mono">{n.since ? `${ago(n.since, now)}` : "before harbor"}</span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** The recap: live for the last 7 days, or any stored weekly one. */
export function RecapView({ now }: { now: number }) {
  const [list, setList] = useState<RecapInfo[]>([]);
  const [pick, setPick] = useState<number | "live">("live");
  const [r, setR] = useState<RecapReport | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => { fetchRecaps().then(setList, () => undefined); }, []);
  useEffect(() => {
    setR(null); setErr(null);
    (pick === "live" ? fetchRecap(7) : fetchStoredRecap(pick)).then(setR, (e: Error) => setErr(e.message));
  }, [pick]);

  return (
    <div className="recap">
      <div className="compare-picks">
        <label className="pick"><span className="label">recap</span>
          <select value={pick} onChange={(e) => setPick(e.target.value === "live" ? "live" : Number(e.target.value))}>
            <option value="live">last 7 days (live)</option>
            {list.map((x) => <option key={x.id} value={x.id}>week to {day(x.periodEnd)}</option>)}
          </select>
        </label>
        <a className="link" href={recapMarkdownUrl(pick === "live" ? undefined : pick)} target="_blank" rel="noreferrer">export .md</a>
      </div>
      {err && <p className="empty">{err}</p>}
      {!r && !err && <p className="empty">loading…</p>}
      {r && <>
        <p className="mono dim">{day(r.periodStart)} – {day(r.periodEnd)}</p>
        <Notes title="Behind" rule={`behind upstream for ${r.settings.behindDays}+ days`} notes={r.behind} now={now} />
        <Notes title="Uncommitted" rule={`changes older than ${r.settings.dirtyDays} days`} notes={r.dirty} now={now} />
        <Notes title="Unpushed" rule={`local commits older than ${r.settings.dirtyDays} days`} notes={r.unpushed} now={now} />
        <Notes title="Neglected" rule={`no commits or changes for ${r.settings.neglectedDays}+ days`} notes={r.neglected} now={now} />
        {r.machines.length > 0 && (
          <section className="recap-sec">
            <h3>Machines</h3>
            <ul className="table notes">
              {r.machines.map((m) => {
                const added = m.changes.filter((c) => !c.before).length, removed = m.changes.filter((c) => !c.after).length;
                return (
                  <li key={m.machine} className="trow">
                    <span className="name">{m.machine}<small className="mono">{m.os} · {m.kernel}</small></span>
                    <span className="opt mono dim">+{added} −{removed} ~{m.changes.length - added - removed} this period</span>
                    <span className={`r mono ${m.upgrades.length ? "warn-text" : ""}`}>{m.upgrades.length} upgradable</span>
                  </li>
                );
              })}
            </ul>
          </section>
        )}
        {r.machines.length > 0 && (
          <section className="recap-sec">
            <h3>Version drift <span className="dim2">{r.drift.length + r.driftMore}</span></h3>
            <p className="rule">packages two or more machines have at different versions</p>
            {r.drift.length === 0 ? <p className="dim2 none">None.</p> : (
              <ul className="table notes">
                {r.drift.slice(0, 100).map((d) => (
                  <li key={`${d.manager}:${d.name}`} className="trow">
                    <span className="name mono">{d.name}<small>{d.manager}</small></span>
                    <span className="opt mono dim">{d.versions.map(([m, v]) => `${m} ${v}`).join(" · ")}</span>
                    <span />
                  </li>
                ))}
              </ul>
            )}
          </section>
        )}
      </>}
    </div>
  );
}
