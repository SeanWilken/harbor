import { useEffect, useRef, useState } from "react";
import type { RoleInfo, RoleState, Work } from "../types";
import { ago } from "../format";

export const workText: Record<Work, string> = {
  full: "takes heavy work",
  light: "light work only",
  none: "takes no outside work",
};

/** What each work level means to the rest of the suite (and to harbor itself). */
const capability: Record<Work, string[]> = {
  full: ["Spare capacity: builds, batch jobs and AI inference may be sent here.", "Harbor runs its fetches and package scans as usual."],
  light: ["Small jobs only; heavy work and inference go to other machines.", "Harbor runs its fetches and package scans as usual."],
  none: ["Busy with its own duty: no outside work is sent here.", "Harbor pauses its own git fetches and package scans; it keeps reporting."],
};

const titleOf = (d: RoleInfo) => d.title ?? d.name[0]!.toUpperCase() + d.name.slice(1);

/** The machine's roles as cards: page left and right, inspect what a role does, activate one. */
export function RoleBar({ machine, role, now, onSwitch, switching }: {
  machine: string; role?: RoleState; now: number; onSwitch?: (name: string) => void; switching?: string;
}) {
  const track = useRef<HTMLDivElement>(null);
  const [page, setPage] = useState(0);
  const declared = role?.declared ?? [];
  const current = Math.max(0, declared.findIndex((d) => d.name === role?.name));

  const go = (i: number, smooth = true) => {
    const el = track.current;
    if (!el) return;
    const n = Math.max(0, Math.min(declared.length - 1, i));
    el.scrollTo({ left: n * el.clientWidth, behavior: smooth ? "smooth" : "auto" });
    setPage(n);
  };
  // open on the active role, and follow it when it changes
  useEffect(() => { go(current, false); }, [role?.name, declared.length]);

  if (!role) return null;
  const onScroll = () => {
    const el = track.current;
    if (el && el.clientWidth) setPage(Math.round(el.scrollLeft / el.clientWidth));
  };
  return (
    <div className="roles">
      <div className="roles-head">
        <span className="label">role</span>
        {declared.length > 1 && (
          <span className="pager">
            <button className="link" onClick={() => go(page - 1)} disabled={page === 0} aria-label="previous role">‹</button>
            <span className="dim2">{page + 1} / {declared.length}</span>
            <button className="link" onClick={() => go(page + 1)} disabled={page >= declared.length - 1} aria-label="next role">›</button>
          </span>
        )}
      </div>
      <div className="role-track" ref={track} onScroll={onScroll} tabIndex={0} aria-label={`${machine} roles`}
        onKeyDown={(e) => { if (e.key === "ArrowLeft") go(page - 1); if (e.key === "ArrowRight") go(page + 1); }}>
        {declared.map((d) => (
          <RoleCard key={d.name} machine={machine} d={d} state={d.name === role.name ? role : undefined} now={now}
            onActivate={onSwitch} switching={switching} />
        ))}
      </div>
    </div>
  );
}

function RoleCard({ machine, d, state, now, onActivate, switching }: {
  machine: string; d: RoleInfo; state?: RoleState; now: number; onActivate?: (name: string) => void; switching?: string;
}) {
  const [inspect, setInspect] = useState(false);
  const title = titleOf(d);
  const activate = () => {
    if (window.confirm(`Switch ${machine.toUpperCase()} to ${title}? It runs this role's duties on ${machine}.`)) onActivate?.(d.name);
  };
  const duties = d.start === undefined ? undefined : [
    ...(d.stop ?? []).map((s) => `stop ${s}`),
    ...(d.power ? [`power profile: ${d.power}`] : []),
    ...d.start.map((s) => `start ${s}`),
  ];
  return (
    <section className={`role-card w-${d.work} ${state ? "active" : ""}`} aria-label={title}>
      <div className="role-top">
        <span className={`work-chip w-${d.work}`}>{workText[d.work]}</span>
        {state && <span className="role-active">● active</span>}
      </div>
      <h3 className="role-title">{title}</h3>
      {d.title && <div className="role-name dim2">{d.name}</div>}
      <p className="role-desc">{d.description ?? capability[d.work][0]}</p>
      {inspect && (
        <div className="role-detail">
          <div className="label">capabilities</div>
          <ul>{capability[d.work].map((c) => <li key={c}>{c}</li>)}</ul>
          <div className="label">duties when activated</div>
          {duties === undefined
            ? <p className="dim2">Visible to hub admins.</p>
            : duties.length === 0
              ? <p className="dim2">None: switching only changes what the machine reports.</p>
              : <ul className="mono">{duties.map((x) => <li key={x}>{x}</li>)}</ul>}
          {state && state.applied.length > 0 && (
            <>
              <div className="label">last switch</div>
              <ul className="mono">{state.applied.map((x) => <li key={x} className={x.includes("failed") ? "warn-text" : undefined}>{x}</li>)}</ul>
            </>
          )}
        </div>
      )}
      <div className="role-foot">
        <button className="link" onClick={() => setInspect(!inspect)} aria-expanded={inspect}>{inspect ? "hide details" : "inspect"}</button>
        {state
          ? <span className="dim2">{state.by === "default" ? "default role" : `since ${ago(state.since, now)} ago · ${state.by}`}</span>
          : onActivate && (switching
            ? <span className="dim">{switching === d.name ? "activating…" : `switching to ${switching}…`}</span>
            : <button className="role-go" onClick={activate}>Activate</button>)}
      </div>
    </section>
  );
}
