import { useCallback, useEffect, useState } from "react";
import type { AlertAction, AlertList, AlertView } from "../types";
import { ackAlerts, checkAlertsNow, fetchAlerts, runAlertAction } from "../api";
import { ago } from "../format";

const RULES: Record<string, string> = {
  offline: "machine", role_failed: "role", unpushed: "repo", site_down: "site", cert: "certificate",
};

function AlertItem({ v, now, admin, busy, onAck, onAction }: {
  v: AlertView; now: number; admin: boolean; busy: boolean; onAck: () => void; onAction: (a: AlertAction) => void;
}) {
  const a = v.alert;
  const state = a.resolvedAt ? `cleared ${ago(a.resolvedAt, now)} ago` : `ongoing · checked ${ago(a.seenAt, now)} ago`;
  return (
    <li className={`alert sev-${a.severity} ${a.resolvedAt ? "cleared" : "open"} ${a.ackedAt ? "seen" : "unseen"}`}>
      <div className="alert-top">
        <span className="alert-kind">{RULES[a.rule] ?? a.rule}</span>
        <span className="dim2" title={new Date(a.openedAt).toLocaleString()}>{ago(a.openedAt, now)} ago</span>
      </div>
      <h4 className="alert-title">{a.title}</h4>
      {a.detail && <p className="alert-detail">{a.detail}</p>}
      <div className="alert-foot">
        <span className={a.resolvedAt ? "dim" : a.severity === "critical" ? "danger-text" : "warn-text"}>{state}</span>
        {a.ackedAt && <span className="dim2">seen{a.ackedBy ? ` by ${a.ackedBy.replace(/@.*/, "")}` : ""}</span>}
        <span className="alert-actions">
          {v.actions.map((x) => x.href
            ? <a key={x.label} className="btn" href={x.href} {...(x.href.startsWith("http") ? { target: "_blank", rel: "noopener noreferrer" } : {})}>{x.label}</a>
            : <button key={x.label} className="btn" disabled={busy} onClick={() => onAction(x)}>{x.label}</button>)}
          {admin && !a.ackedAt && <button className="btn seen-btn" disabled={busy} onClick={onAck}>Mark seen</button>}
        </span>
      </div>
    </li>
  );
}

/** The alert queue: unseen first (mark seen when you've taken it in), then what is still going on. */
export function AlertsView({ admin, now, onUnseen }: { admin: boolean; now: number; onUnseen: (n: number) => void }) {
  const [list, setList] = useState<AlertList>();
  const [history, setHistory] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string>();
  const load = useCallback(async () => {
    try {
      const l = await fetchAlerts(history ? 7 : undefined);
      setList(l); onUnseen(l.unseen); setErr(undefined);
    } catch (e) { setErr((e as Error).message); }
  }, [history, onUnseen]);
  useEffect(() => {
    void load();
    const t = setInterval(() => void load(), 30_000);
    return () => clearInterval(t);
  }, [load]);
  const act = async (work: () => Promise<unknown>) => {
    setBusy(true);
    try { await work(); await load(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const onAction = (x: AlertAction) => {
    if (x.confirm && !window.confirm(x.confirm)) return;
    void act(() => runAlertAction(x));
  };

  if (!list) return <p className="empty">{err ?? "loading…"}</p>;
  const unseen = list.alerts.filter((v) => !v.alert.ackedAt);
  const ongoing = list.alerts.filter((v) => v.alert.ackedAt && !v.alert.resolvedAt);
  const past = list.alerts.filter((v) => v.alert.ackedAt && v.alert.resolvedAt);
  const section = (title: string, items: AlertView[], empty?: string) => (items.length > 0 || empty) && (
    <section className="alert-group">
      <div className="label">{title} <b>{items.length}</b></div>
      {items.length === 0 ? <p className="dim2">{empty}</p> : (
        <ul className="alerts">
          {items.map((v) => <AlertItem key={v.alert.id} v={v} now={now} admin={admin} busy={busy}
            onAck={() => void act(() => ackAlerts([v.alert.id]))} onAction={onAction} />)}
        </ul>
      )}
    </section>
  );
  return (
    <div className="alerts-view">
      <div className="tablebar">
        <div className="filters">
          <button className={`chip ${history ? "on" : ""}`} aria-pressed={history} onClick={() => setHistory(!history)}>last 7 days</button>
        </div>
        {admin && (
          <span className="alert-bar">
            <button className="link" disabled={busy} onClick={() => void act(checkAlertsNow)} title="run every rule now (they also run each minute)">check now</button>
            {unseen.length > 0 && <button className="link" disabled={busy} onClick={() => void act(() => ackAlerts())}>mark all seen</button>}
          </span>
        )}
      </div>
      {err && <p className="warn-text">{err}</p>}
      {section("unseen", unseen, ongoing.length === 0 ? "Nothing new. Harbor checks machines, roles, unpushed work and sites every minute." : undefined)}
      {section("seen, still going on", ongoing)}
      {history && section("cleared", past, "Nothing cleared in the last 7 days.")}
    </div>
  );
}
